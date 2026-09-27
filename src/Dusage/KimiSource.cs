using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dusage;

/// <summary>
/// Kimi Code's limits (5-hour, weekly on older plans, the membership's monthly pool), read with Kimi Code CLI's
/// sign-in — the same numbers as its /usage. The token is only read, never refreshed or written: Kimi Code renews
/// it whenever you use it, and its refresh tokens rotate, so refreshing here would sign Kimi Code out.
/// </summary>
sealed class KimiSource : IUsageSource
{
    const string Stale = "Paused: Kimi Code's sign-in has expired. It renews the next time you use Kimi Code.";

    /// <summary>
    /// Kimi Code keeps one sign-in per region: the mainland-China one as kimi-code.json, the global (kimi.ai) one
    /// under a name hashed from its hosts, the way Kimi Code derives it.
    /// </summary>
    static readonly (string File, string UsageUrl)[] Regions =
    [
        ("kimi-code.json", "https://api.kimi.com/coding/v1/usages"),
        (Scoped("https://auth.kimi.ai", "https://api.kimi.ai/coding/v1"), "https://api.kimi.ai/coding/v1/usages"),
    ];

    string? _rejectedToken;

    public string Key => "kimi";
    public string Name => "Kimi";
    public string Via => "Kimi Code";
    public string SignInHint => "Sign in to Kimi Code to show this: run `kimi`, then /login.";

    static string CredentialsDir => Path.Combine(
        Environment.GetEnvironmentVariable("KIMI_CODE_HOME") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code"),
        "credentials");

    static string Scoped(string oauthHost, string baseUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { oauthHost, baseUrl })));
        return $"kimi-code-env-{Convert.ToHexStringLower(hash)[..16]}.json";
    }

    /// <summary>The region signed in to most recently, if either is.</summary>
    static (string Path, string UsageUrl)? SignIn() =>
        Regions.Select(r => (Path: Path.Combine(CredentialsDir, r.File), r.UsageUrl))
            .Where(r => File.Exists(r.Path))
            .OrderByDescending(r => File.GetLastWriteTimeUtc(r.Path))
            .Select(r => ((string, string)?)r)
            .FirstOrDefault();

    public bool HasSignIn()
    {
        if (SignIn() is not { } signIn) return false;
        using var credentials = Net.ReadJsonFile(signIn.Path);
        return credentials?.RootElement.Str("access_token") is { Length: > 0 };
    }

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct, Action<JsonElement>? inspect = null)
    {
        if (SignIn() is not (var path, var usageUrl)) throw new UsageException(SignInHint, UsageException.Recheck);
        string token;
        using (var credentials = Net.ReadJsonFile(path))
        {
            var root = credentials?.RootElement;
            token = root?.Str("access_token") ?? throw new UsageException(SignInHint, UsageException.Recheck);
            if (root?.Num("expires_at") is { } expires && DateTimeOffset.FromUnixTimeSeconds((long)expires) <= DateTimeOffset.UtcNow)
                throw new UsageException(Stale, UsageException.Recheck);
        }
        if (token == _rejectedToken)
            throw new UsageException(Stale, UsageException.Recheck);

        using var request = new HttpRequestMessage(HttpMethod.Get, usageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        JsonDocument doc;
        try
        {
            doc = await Net.GetJsonAsync(request, ct);
        }
        catch (UsageException e) when (e.Status is 401)
        {
            _rejectedToken = token;
            throw new UsageException(Stale, UsageException.Recheck, e.Status);
        }

        using (doc)
        {
            inspect?.Invoke(doc.RootElement);
            return Snapshot(doc.RootElement);
        }
    }

    /// <summary>
    /// The 5-hour window, then the weekly one (plans from before the monthly pool) or else the monthly pool, which is
    /// shared with the rest of Kimi. With both, the pool is an extra limit.
    /// </summary>
    static UsageSnapshot Snapshot(JsonElement root)
    {
        var usages = root.Obj("usages");
        var session = Window(usages?.Obj("limit_5h"), TimeSpan.FromHours(5));
        var weekly = Window(usages?.Obj("limit_7d"), TimeSpan.FromDays(7));
        var month = Window(usages?.Obj("limit_month_total"), null);
        if (weekly is null) return new UsageSnapshot(null, session, month, []);
        return new UsageSnapshot(null, session, weekly, month is null ? [] : [new ExtraLimit("month_total", "Pool", null, month)]);
    }

    /// <param name="length">Null for a calendar month, which ends at the reset.</param>
    static UsageWindow? Window(JsonElement? w, TimeSpan? length)
    {
        if (w is not { } window || Ratio(window) is not { } used) return null;
        var resets = Date(window.Str("reset_time"));
        length ??= resets is { } at ? at - at.AddMonths(-1) : TimeSpan.FromDays(30);
        return new UsageWindow(Math.Round(used * 100, 6), resets, length.Value);
    }

    /// <summary>used_ratio, 0..1, which comes as a number or a string.</summary>
    static double? Ratio(JsonElement w) =>
        w.Num("used_ratio") ?? (double.TryParse(w.Str("used_ratio"), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null);

    static DateTimeOffset? Date(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
}

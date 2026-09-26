using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Dusage;

/// <summary>
/// Claude plan limits, read with Claude Code's own sign-in (the same numbers as its /usage screen).
/// The token is only read, never refreshed or written: Claude Code renews it whenever you use it.
/// </summary>
sealed class ClaudeSource : IUsageSource
{
    const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    const string Stale = "Paused: Claude Code's sign-in has expired. It renews the next time you use Claude Code.";
    static readonly TimeSpan FiveHours = TimeSpan.FromHours(5), Week = TimeSpan.FromDays(7);

    string? _rejectedToken;

    public string Key => "claude";
    public string Name => "Claude";
    public string Via => "Claude Code";
    public string SignInHint => "Sign in to Claude Code to show this: run `claude`, then /login.";

    static string CredentialsPath => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        ".credentials.json");

    public bool HasSignIn()
    {
        using var credentials = Net.ReadJsonFile(CredentialsPath);
        return credentials?.RootElement.Obj("claudeAiOauth")?.Str("accessToken") is { Length: > 0 };
    }

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct, Action<JsonElement>? inspect = null)
    {
        string token;
        string? plan;
        using (var credentials = Net.ReadJsonFile(CredentialsPath))
        {
            var oauth = credentials?.RootElement.Obj("claudeAiOauth");
            token = oauth?.Str("accessToken") ?? throw new UsageException(SignInHint, UsageException.Recheck);
            plan = oauth?.Str("subscriptionType");
            if (oauth?.Num("expiresAt") is { } expiresMs && DateTimeOffset.FromUnixTimeMilliseconds((long)expiresMs) <= DateTimeOffset.UtcNow)
                throw new UsageException(Stale, UsageException.Recheck);
        }
        if (token == _rejectedToken)
            throw new UsageException(Stale, UsageException.Recheck);

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");

        JsonDocument doc;
        try
        {
            doc = await Net.GetJsonAsync(request, ct);
        }
        catch (UsageException e) when (e.Status is 401 or 403)
        {
            _rejectedToken = token;
            throw new UsageException(Stale, UsageException.Recheck, e.Status);
        }

        using (doc)
        {
            var root = doc.RootElement;
            inspect?.Invoke(root);
            return new UsageSnapshot(plan, Window(root.Obj("five_hour"), FiveHours), Window(root.Obj("seven_day"), Week), Extras(root));
        }
    }

    /// <summary>Per-model caps (e.g. Opus on Max plans). The "limits" list names every limit the account has;
    /// the older seven_day_* fields are the fallback.</summary>
    static List<ExtraLimit> Extras(JsonElement root)
    {
        var extras = new List<ExtraLimit>();
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in limits.EnumerateArray())
            {
                if (item.Str("kind") is not { } kind || kind is "session" or "weekly_all") continue;
                if (item.Num("percent") is not { } percent) continue;
                var isSession = item.Str("group") == "session";
                var window = new UsageWindow(percent, Date(item.Str("resets_at")), isSession ? FiveHours : Week);
                var label = item.Str("scope") is { Length: > 0 and <= 20 } scope ? Fmt.Label(scope) : Fmt.Label(kind);
                extras.Add(isSession ? new ExtraLimit(kind, label, window, null) : new ExtraLimit(kind, label, null, window));
            }
            return extras;
        }

        foreach (var (key, label) in new[] { ("seven_day_opus", "Opus"), ("seven_day_sonnet", "Sonnet") })
            if (Window(root.Obj(key), Week) is { } weekly)
                extras.Add(new ExtraLimit(key, label, null, weekly));
        return extras;
    }

    static UsageWindow? Window(JsonElement? w, TimeSpan length) =>
        w?.Num("utilization") is { } used ? new UsageWindow(used, Date(w?.Str("resets_at")), length) : null;

    static DateTimeOffset? Date(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
}

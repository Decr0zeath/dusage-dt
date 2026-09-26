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
    const string Stale = "Sign-in token expired — it renews the next time you use Claude Code.";
    static readonly TimeSpan FiveHours = TimeSpan.FromHours(5), Week = TimeSpan.FromDays(7);

    string? _rejectedToken;

    public string Key => "claude";
    public string Name => "Claude";

    static string CredentialsPath => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        ".credentials.json");

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct, Action<JsonElement>? inspect = null)
    {
        string token;
        string? plan;
        using (var credentials = Net.ReadJsonFile(CredentialsPath))
        {
            var oauth = credentials?.RootElement.Obj("claudeAiOauth");
            token = oauth?.Str("accessToken")
                ?? throw new UsageException("Not signed in — run `claude` and log in with your Claude account.", UsageException.Recheck);
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
            var extra = new List<NamedWindow>();
            if (Window(root, "seven_day_opus", Week) is { } opus) extra.Add(new("Opus weekly", opus));
            if (Window(root, "seven_day_sonnet", Week) is { } sonnet) extra.Add(new("Sonnet weekly", sonnet));
            return new UsageSnapshot(plan, Window(root, "five_hour", FiveHours), Window(root, "seven_day", Week), extra);
        }
    }

    static UsageWindow? Window(JsonElement root, string key, TimeSpan length)
    {
        if (root.Obj(key) is not { } w || w.Num("utilization") is not { } used) return null;
        DateTimeOffset? resets = DateTimeOffset.TryParse(w.Str("resets_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
        return new UsageWindow(used, resets, length);
    }
}

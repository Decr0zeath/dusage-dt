using System.Net.Http.Headers;
using System.Text.Json;

namespace Dusage;

/// <summary>
/// ChatGPT plan limits (the Codex 5-hour and weekly windows), read with the Codex CLI/app sign-in —
/// the same numbers as Codex's /status. The token is only read, never refreshed or written.
/// </summary>
sealed class CodexSource : IUsageSource
{
    const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    const string Stale = "Sign-in was rejected — it renews the next time you use Codex.";

    string? _rejectedToken;

    public string Key => "codex";
    public string Name => "ChatGPT";

    static string AuthPath => Path.Combine(
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
        "auth.json");

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct, Action<JsonElement>? inspect = null)
    {
        string token;
        string? account;
        using (var auth = Net.ReadJsonFile(AuthPath))
        {
            var tokens = auth?.RootElement.Obj("tokens");
            if (tokens?.Str("access_token") is not { } accessToken)
                throw new UsageException(
                    auth?.RootElement.Str("OPENAI_API_KEY") is { Length: > 0 }
                        ? "Codex uses an API key — only a ChatGPT sign-in has plan limits."
                        : "Not signed in — run `codex login` with your ChatGPT account.",
                    UsageException.Recheck);
            token = accessToken;
            account = tokens?.Str("account_id");
        }
        if (token == _rejectedToken)
            throw new UsageException(Stale, UsageException.Recheck);

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (account is not null) request.Headers.Add("ChatGPT-Account-Id", account);

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
            var limits = root.Obj("rate_limit");
            UsageWindow? session = null, weekly = null;
            foreach (var (key, fallbackLength) in new[] { ("primary_window", TimeSpan.FromHours(5)), ("secondary_window", TimeSpan.FromDays(7)) })
            {
                if (limits?.Obj(key) is not { } w || w.Num("used_percent") is not { } used) continue;
                var length = w.Num("limit_window_seconds") is { } seconds && seconds > 0 ? TimeSpan.FromSeconds(seconds) : fallbackLength;
                DateTimeOffset? resets = w.Num("reset_at") is { } at ? DateTimeOffset.FromUnixTimeSeconds((long)at)
                    : w.Num("reset_after_seconds") is { } after ? DateTimeOffset.UtcNow.AddSeconds(after)
                    : null;
                var window = new UsageWindow(used, resets, length);
                // Sort by length rather than trusting primary/secondary: the short one is the session window.
                if (length <= TimeSpan.FromDays(1)) session ??= window;
                else weekly ??= window;
            }
            return new UsageSnapshot(root.Str("plan_type"), session, weekly, []);
        }
    }
}

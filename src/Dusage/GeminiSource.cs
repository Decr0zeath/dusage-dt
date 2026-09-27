using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dusage;

/// <summary>
/// Gemini CLI's daily request limits, one per model family, read with its Google sign-in (the numbers behind its
/// /stats). The token is only read, never refreshed or written: Gemini CLI renews it whenever you use it, and these
/// limits only move while you do.
/// </summary>
sealed partial class GeminiSource : IUsageSource
{
    const string Api = "https://cloudcode-pa.googleapis.com/v1internal:";
    const string Stale = "Paused: Gemini CLI's sign-in has expired. It renews the next time you use Gemini CLI.";
    static readonly TimeSpan Day = TimeSpan.FromDays(1);

    string? _rejectedToken;
    /// <summary>The Google Cloud project that holds the quota, and the plan, as of the token they were looked up with.</summary>
    (string Token, string? Project, string? Plan)? _account;

    public string Key => "gemini";
    public string Name => "Gemini";
    public string Via => "Gemini CLI";
    public string SignInHint => "Sign in to Gemini CLI with Google to show this: run `gemini` and pick Login with Google.";

    static string CredentialsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "oauth_creds.json");

    public bool HasSignIn()
    {
        using var credentials = Net.ReadJsonFile(CredentialsPath);
        return credentials?.RootElement.Str("access_token") is { Length: > 0 };
    }

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct, Action<JsonElement>? inspect = null)
    {
        string token;
        using (var credentials = Net.ReadJsonFile(CredentialsPath))
        {
            var root = credentials?.RootElement;
            token = root?.Str("access_token") ?? throw new UsageException(SignInHint, UsageException.Recheck);
            if (root?.Num("expiry_date") is { } expiresMs && DateTimeOffset.FromUnixTimeMilliseconds((long)expiresMs) <= DateTimeOffset.UtcNow)
                throw new UsageException(Stale, UsageException.Recheck);
        }
        if (token == _rejectedToken)
            throw new UsageException(Stale, UsageException.Recheck);

        try
        {
            // Tokens last an hour, so this asks at most about once an hour.
            if (_account?.Token != token)
            {
                var project = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT") is { Length: > 0 } env ? env : null;
                using var setup = await PostAsync("loadCodeAssist", token, new
                {
                    cloudaicompanionProject = project,
                    metadata = new { ideType = "IDE_UNSPECIFIED", platform = "PLATFORM_UNSPECIFIED", pluginType = "GEMINI", duetProject = project },
                }, ct);
                var root = setup.RootElement;
                inspect?.Invoke(root);
                project ??= root.Str("cloudaicompanionProject") ?? root.Obj("cloudaicompanionProject")?.Str("id");
                _account = (token, project, Plan(root.Obj("currentTier")?.Str("id")));
            }

            var (_, projectId, plan) = _account.Value;
            using var doc = await PostAsync("retrieveUserQuota", token, new { project = projectId }, ct);
            inspect?.Invoke(doc.RootElement);
            return Snapshot(doc.RootElement, plan);
        }
        catch (UsageException e) when (e.Status is 401)
        {
            _rejectedToken = token;
            throw new UsageException(Stale, UsageException.Recheck, e.Status);
        }
    }

    static async Task<JsonDocument> PostAsync(string method, string token, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Api + method)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await Net.GetJsonAsync(request, ct);
    }

    /// <summary>Leaves out what isn't known, e.g. the project before Google has said which it is.</summary>
    static readonly JsonSerializerOptions Body = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// Every model has its own bucket. Models of one family (2.5 Pro, 3 Pro) are summed up by the most used of them;
    /// Pro is the main row and the other families are extra limits.
    /// </summary>
    static UsageSnapshot Snapshot(JsonElement root, string? plan)
    {
        var families = new Dictionary<string, UsageWindow>();
        if (root.TryGetProperty("buckets", out var buckets) && buckets.ValueKind == JsonValueKind.Array)
        {
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.Str("modelId") is not { } model || bucket.Num("remainingFraction") is not { } left) continue;
                // Rounded, or 0.8 left would come out as 19.999…% used and show as 19.
                var window = new UsageWindow(Math.Round((1 - left) * 100, 6), Date(bucket.Str("resetTime")), Day);
                var family = Family(model);
                if (!families.TryGetValue(family, out var seen) || window.Percent > seen.Percent) families[family] = window;
            }
        }

        var main = families.ContainsKey("pro") ? "pro" : families.OrderByDescending(f => f.Value.Percent).Select(f => f.Key).FirstOrDefault();
        // "Flash Lite" is "Lite" on the widget, where the name sits under Flash's.
        var extras = families.Where(f => f.Key != main)
            .Select(f => new ExtraLimit(f.Key, Fmt.Label(f.Key.Replace('-', '_')), f.Value, null, Fmt.Label(f.Key.Split('-')[^1])))
            .ToList();
        return new UsageSnapshot(plan, main is null ? null : families[main], null, extras, main is null ? null : Fmt.Label(main.Replace('-', '_')));
    }

    /// <summary>"gemini-2.5-flash-lite" → "flash-lite", "gemini-3-pro-preview" → "pro".</summary>
    static string Family(string model) => ModelFamily().Match(model) is { Success: true } m ? m.Groups["family"].Value : model;

    [GeneratedRegex(@"^(?:gemini-)?[\d.]+-(?<family>[a-z]+(?:-[a-z]+)*?)(?:-(?:preview|exp|latest|\d).*)?$")]
    private static partial Regex ModelFamily();

    /// <summary>"free-tier" → "free", "standard-tier" → "standard".</summary>
    static string? Plan(string? tier) => tier is null ? null : tier.Replace("-tier", "").Replace('-', ' ');

    static DateTimeOffset? Date(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
}

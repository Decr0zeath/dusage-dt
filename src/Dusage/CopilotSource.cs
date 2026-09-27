using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Dusage;

/// <summary>
/// GitHub Copilot's monthly allowance (premium requests, now AI credits; chat and completions on Copilot Free).
/// Not Microsoft Copilot, the Windows assistant, which has no limits to read. The GitHub sign-in comes from, in order:
/// COPILOT_GITHUB_TOKEN, dUsage/dt's own Sign in with GitHub (<see cref="GitHubSignIn"/>), or the GitHub CLI, which
/// Copilot CLI falls back on too. Tokens are only read, never refreshed, and a CLI's is never written anywhere.
/// </summary>
sealed class CopilotSource : IUsageSource
{
    const string UsageUrl = "https://api.github.com/copilot_internal/user";
    const string EnvToken = "COPILOT_GITHUB_TOKEN";
    const string OwnSignIn = "GitHub sign-in", GhCli = "GitHub CLI";
    /// <summary>Asking gh starts a process, so its answer is kept this long.</summary>
    static readonly TimeSpan LookAgain = TimeSpan.FromMinutes(5);
    /// <summary>An account without Copilot stays hidden this long before it's asked again.</summary>
    static readonly TimeSpan NoCopilotFor = TimeSpan.FromHours(6);

    sealed record SignIn(string Token, string Via);

    readonly Lock _lock = new();
    SignIn? _gh;
    DateTimeOffset _lookedAt;
    bool _looking;
    string? _rejectedToken, _noCopilotToken;
    DateTimeOffset _noCopilotUntil;

    public string Key => "copilot";
    public string Name => "GitHub Copilot";
    public string Via => Current()?.Via ?? GhCli;

    public string SignInHint =>
        Current() is { } signIn && IsNoCopilot(signIn.Token) ? $"This GitHub account (via {signIn.Via}) has no Copilot plan."
        : GitHubSignIn.Available ? "Sign in with GitHub to show this, or sign in to the GitHub CLI (`gh auth login`)."
        : "Sign in to the GitHub CLI to show this: run `gh auth login`.";

    public bool HasSignIn() => Current() is { } signIn && !IsNoCopilot(signIn.Token);

    bool IsNoCopilot(string token) => token == _noCopilotToken && DateTimeOffset.Now < _noCopilotUntil;

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct, Action<JsonElement>? inspect = null)
    {
        var signIn = Current() ?? throw new UsageException(SignInHint, UsageException.Recheck);
        var stale = signIn.Via switch
        {
            EnvToken => $"Paused: GitHub turned down the token in {EnvToken}.",
            OwnSignIn => "Paused: GitHub no longer accepts dUsage/dt's sign-in. Sign in again in Settings.",
            _ => "Paused: GitHub turned down the GitHub CLI's sign-in. Run `gh auth login` to sign in again.",
        };
        if (signIn.Token == _rejectedToken)
            throw new UsageException(stale, UsageException.Recheck);

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", signIn.Token);

        JsonDocument doc;
        try
        {
            doc = await Net.GetJsonAsync(request, ct);
        }
        catch (UsageException e) when (e.Status is 401)
        {
            _rejectedToken = signIn.Token;
            if (signIn.Via == OwnSignIn) GitHubSignIn.Rejected();
            throw new UsageException(stale, UsageException.Recheck, e.Status);
        }
        catch (UsageException e) when (e.Status is 404)
        {
            throw NoCopilot(signIn);
        }

        using (doc)
        {
            inspect?.Invoke(doc.RootElement);
            return Snapshot(doc.RootElement) ?? throw NoCopilot(signIn);
        }
    }

    /// <summary>
    /// Every quota runs for a calendar month; the ones without a cap (chat and completions on paid plans) don't show.
    /// A paid plan's allowance is the right bar, as the long window it is. Copilot Free's chat and completions share
    /// one line, each bar named for what it counts, since "mo" twice wouldn't tell them apart.
    /// Null when the account has no Copilot at all.
    /// </summary>
    static UsageSnapshot? Snapshot(JsonElement root)
    {
        if (root.Obj("quota_snapshots") is not { } quotas) return null;
        var plan = root.Str("access_type_sku") is { } sku && sku.StartsWith("free_limited", StringComparison.Ordinal) ? "free" : root.Str("copilot_plan");

        var reset = Date(root.Str("quota_reset_date_utc") ?? root.Str("quota_reset_date") ?? root.Str("limited_user_reset_date"));
        var month = reset is { } at ? at - at.AddMonths(-1) : TimeSpan.FromDays(30);
        var limits = new List<(string Key, UsageWindow Window)>();
        foreach (var quota in quotas.EnumerateObject())
        {
            var q = quota.Value;
            if (q.Bool("unlimited") == true || q.Bool("has_quota") == false || q.Num("percent_remaining") is not { } left) continue;
            limits.Add((quota.Name, new UsageWindow(100 - left, reset, month)));
        }
        if (limits.Count == 0) return new UsageSnapshot(plan, null, null, []);

        var premium = limits.FindIndex(l => l.Key == "premium_interactions");
        if (premium >= 0)
            return new UsageSnapshot(plan, null, limits[premium].Window, Extras(limits.Where((_, i) => i != premium)));
        var named = limits.Select(l => l.Window with { Name = Fmt.Label(l.Key) }).ToList();
        return new UsageSnapshot(plan, named[0], named.ElementAtOrDefault(1), Extras(limits.Skip(2)));

        static List<ExtraLimit> Extras(IEnumerable<(string Key, UsageWindow Window)> rest) =>
            rest.Select(l => new ExtraLimit(l.Key, Fmt.Label(l.Key), null, l.Window, Fmt.Short(Fmt.Label(l.Key)))).ToList();
    }

    /// <summary>Hides Copilot for a while: a GitHub sign-in alone doesn't mean a Copilot plan.</summary>
    UsageException NoCopilot(SignIn signIn)
    {
        _noCopilotToken = signIn.Token;
        _noCopilotUntil = DateTimeOffset.Now + NoCopilotFor;
        return new UsageException($"This GitHub account (via {signIn.Via}) has no Copilot plan.", UsageException.Recheck);
    }

    /// <summary>The sign-in to use. The environment and Credential Manager are cheap to read every time; gh isn't.</summary>
    SignIn? Current()
    {
        if (Environment.GetEnvironmentVariable(EnvToken) is { Length: > 0 } env) return new SignIn(env.Trim(), EnvToken);
        if (GitHubSignIn.Token is { Length: > 0 } own) return new SignIn(own, OwnSignIn);
        return Gh();
    }

    /// <summary>
    /// The GitHub CLI's sign-in. The first call asks gh right away; later ones answer from memory and ask again in the
    /// background every few minutes, so signing in or out of gh is noticed without holding up the widget.
    /// </summary>
    SignIn? Gh()
    {
        lock (_lock)
        {
            if (_lookedAt == default)
            {
                _gh = GhToken() is { } token ? new SignIn(token, GhCli) : null;
                _lookedAt = DateTimeOffset.Now;
            }
            else if (!_looking && DateTimeOffset.Now - _lookedAt >= LookAgain)
            {
                _looking = true;
                Task.Run(() =>
                {
                    var signIn = GhToken() is { } token ? new SignIn(token, GhCli) : null;
                    lock (_lock)
                    {
                        (_gh, _lookedAt, _looking) = (signIn, DateTimeOffset.Now, false);
                    }
                });
            }
            return _gh;
        }
    }

    /// <summary>What `gh auth token` prints: gh's own answer, wherever it keeps the token.</summary>
    static string? GhToken()
    {
        try
        {
            using var gh = Process.Start(new ProcessStartInfo("gh", "auth token --hostname github.com")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            if (gh is null) return null;
            var output = gh.StandardOutput.ReadToEndAsync();
            if (!gh.WaitForExit(5000))
            {
                gh.Kill();
                return null;
            }
            return gh.ExitCode == 0 && output.Result.Trim() is { Length: > 0 } token ? token : null;
        }
        catch (Win32Exception)
        {
            return null; // gh isn't installed
        }
    }

    static DateTimeOffset? Date(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
}

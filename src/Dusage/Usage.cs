using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Dusage;

static class AppInfo
{
    public const string Name = "dUsage/dt";
    public const string Author = "Decr0zeath";
    public const string AuthorUrl = "https://decr0zeath.github.io";
    public const string RepoUrl = "https://github.com/Decr0zeath/dusage-dt";

    public static readonly string Version =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>Opens a web page in the default browser.</summary>
    public static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}

/// <summary>One rate-limit window (a 5-hour session, a daily or weekly cap, a monthly allowance) as last reported.</summary>
/// <param name="Name">What it counts, when a line's two bars run over the same window and their lengths
/// can't tell them apart: Copilot Free's "Chat" and "Completions", both monthly.</param>
public sealed record UsageWindow(double Percent, DateTimeOffset? ResetsAt, TimeSpan Length, string? Name = null)
{
    public bool HasReset(DateTimeOffset now) => ResetsAt is { } at && at <= now;

    /// <summary>Once the reset time passes the window is empty again, even before the next fetch confirms it.</summary>
    public double PercentAt(DateTimeOffset now) => HasReset(now) ? 0 : Percent;

    /// <summary>Share of the window's time already gone (0..1), drawn as the pace tick on the bar.</summary>
    public double? ElapsedAt(DateTimeOffset now) =>
        ResetsAt is { } at && at > now && Length > TimeSpan.Zero ? Math.Clamp(1 - (at - now) / Length, 0, 1) : null;
}

/// <summary>A limit on top of the plan's main ones, e.g. a weekly cap for one model.</summary>
/// <param name="Short">Stands in for <paramref name="Label"/> on the widget, where a long name would widen every line.</param>
public sealed record ExtraLimit(string Key, string Label, UsageWindow? Session, UsageWindow? Weekly, string? Short = null);

/// <summary>
/// A service's limits. <see cref="Session"/> is the short window (a day or less) and <see cref="Weekly"/> the long one
/// (a week, a month), whatever their actual length: they're the widget's left and right bars.
/// </summary>
/// <param name="Label">Names the main limits when they only cover some models, e.g. Gemini's "Pro".</param>
public sealed record UsageSnapshot(string? Plan, UsageWindow? Session, UsageWindow? Weekly, IReadOnlyList<ExtraLimit> Extra, string? Label = null)
{
    /// <summary>Every limit there is, main ones first, each named by its window: "5-hour", "Weekly", "Opus weekly",
    /// "Pro daily", "Chat monthly".</summary>
    public IEnumerable<(string Name, UsageWindow Window)> Limits()
    {
        IEnumerable<(string? Label, UsageWindow? Window)> all =
        [
            (Label, Session), (Label, Weekly),
            .. (Extra ?? []).Where(e => e.Key is not null).SelectMany(e => new[] { (e.Label, e.Session), (e.Label, e.Weekly) }),
        ];
        foreach (var (label, window) in all)
            if (window is not null)
                yield return ((window.Name ?? label) is { } name ? $"{name} {Fmt.Window(window.Length).ToLowerInvariant()}" : Fmt.Window(window.Length), window);
    }
}

/// <summary>What the widget knows about one provider. Persisted so a restart shows the last numbers immediately.</summary>
public sealed class ProviderState
{
    public UsageSnapshot? Last { get; set; }
    public DateTimeOffset? FetchedAt { get; set; }
    public string? Problem { get; set; }
}

public interface IUsageSource
{
    string Key { get; }
    string Name { get; }

    /// <summary>The tool whose sign-in is borrowed, e.g. "Claude Code".</summary>
    string Via { get; }

    string SignInHint { get; }

    /// <summary>Cheap local check: is there a saved sign-in to use at all?</summary>
    bool HasSignIn();

    /// <param name="inspect">Receives the raw response; only used by <c>--probe-raw</c>.</param>
    Task<UsageSnapshot> FetchAsync(CancellationToken ct, Action<JsonElement>? inspect = null);

    /// <summary>Every service, in the order the widget lists them.</summary>
    static IUsageSource[] All() => [new ClaudeSource(), new CodexSource(), new CopilotSource(), new GeminiSource(), new KimiSource()];
}

/// <summary>An expected failure, worded for the tooltip.</summary>
public sealed class UsageException(string message, TimeSpan? retryAfter = null, int status = 0) : Exception(message)
{
    /// <summary>How soon to look again when a sign-in is missing or stale. Only the local file is read until it changes.</summary>
    public static readonly TimeSpan Recheck = TimeSpan.FromMinutes(1);

    public TimeSpan? RetryAfter { get; } = retryAfter;
    public int Status { get; } = status;
}

static class Fmt
{
    public static string Pct(double percent) => ((int)Math.Floor(Math.Clamp(percent, 0, 100))).ToString(CultureInfo.CurrentCulture);

    public static string Title(string? s) => string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>"weekly_opus" → "Opus", "code_review" → "Code Review".</summary>
    public static string Label(string key) =>
        string.Join(' ', key.Replace("weekly_", "").Replace("session_", "")
            .Split('_', StringSplitOptions.RemoveEmptyEntries).Select(Title));

    /// <summary>A window by its length: "5-hour", "Daily", "Weekly", "Monthly".</summary>
    public static string Window(TimeSpan length) => length.TotalDays switch
    {
        < 0.9 => $"{Math.Round(length.TotalHours)}-hour",
        < 1.1 => "Daily",
        >= 6.5 and < 7.5 => "Weekly",
        >= 27 and < 32 => "Monthly",
        var days => $"{Math.Round(days)}-day",
    };

    /// <summary>The same, tiny, for the widget's bar labels: "5h", "1d", "7d", "mo".</summary>
    public static string Tag(TimeSpan length) => length.TotalDays switch
    {
        < 0.9 => $"{Math.Round(length.TotalHours)}h",
        >= 27 and < 32 => "mo",
        var days => $"{Math.Round(days)}d",
    };

    /// <summary>A bar's label: its window ("5h"), or what it counts when it has a name ("Completions" → "comp").</summary>
    public static string Tag(UsageWindow w) => w.Name is { Length: > 0 } name ? Short(name).ToLowerInvariant() : Tag(w.Length);

    /// <summary>A name cut to fit the widget: "Completions" → "Comp", "Premium Interactions" → "Prem".</summary>
    public static string Short(string name)
    {
        var word = name.Split(' ')[0];
        return word.Length > 4 ? word[..4] : word;
    }

    public static string Span(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m"
        : "<1m";

    public static string Ago(TimeSpan t) => t.TotalMinutes < 1 ? "just now" : Span(t) + " ago";

    public static string Reset(UsageWindow w, DateTimeOffset now, ResetStyle style = ResetStyle.Both)
    {
        if (w.ResetsAt is not { } at) return w.Percent > 0 ? "" : "not started";
        if (at <= now) return "reset — refreshing soon";
        return style switch
        {
            ResetStyle.Time => $"resets {When(at, now)}",
            ResetStyle.Countdown => $"resets in {Span(at - now)}",
            _ => $"resets {When(at, now)} · in {Span(at - now)}",
        };
    }

    static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == today) return time;
        if (local.Date == today.AddDays(1)) return "tomorrow " + time;
        return local.ToString("ddd ", CultureInfo.CurrentCulture) + time;
    }
}

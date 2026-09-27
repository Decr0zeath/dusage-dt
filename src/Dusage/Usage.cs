using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Dusage;

static class AppInfo
{
    public const string Name = "dUsage/dt";
    public const string Author = "Decr0zeath";
    public const string AuthorUrl = "https://github.com/Decr0zeath";
    public const string RepoUrl = "https://github.com/Decr0zeath/dusage-dt";

    public static readonly string Version =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>Opens a web page in the default browser.</summary>
    public static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}

/// <summary>One rate-limit window (a 5-hour session or a weekly cap) as last reported.</summary>
public sealed record UsageWindow(double Percent, DateTimeOffset? ResetsAt, TimeSpan Length)
{
    public bool HasReset(DateTimeOffset now) => ResetsAt is { } at && at <= now;

    /// <summary>Once the reset time passes the window is empty again, even before the next fetch confirms it.</summary>
    public double PercentAt(DateTimeOffset now) => HasReset(now) ? 0 : Percent;

    /// <summary>Share of the window's time already gone (0..1), drawn as the pace tick on the bar.</summary>
    public double? ElapsedAt(DateTimeOffset now) =>
        ResetsAt is { } at && at > now && Length > TimeSpan.Zero ? Math.Clamp(1 - (at - now) / Length, 0, 1) : null;
}

/// <summary>A limit on top of the plan's main ones, e.g. a weekly cap for one model.</summary>
public sealed record ExtraLimit(string Key, string Label, UsageWindow? Session, UsageWindow? Weekly);

public sealed record UsageSnapshot(string? Plan, UsageWindow? Session, UsageWindow? Weekly, IReadOnlyList<ExtraLimit> Extra);

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

    public static string Span(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m"
        : "<1m";

    public static string Ago(TimeSpan t) => t.TotalMinutes < 1 ? "just now" : Span(t) + " ago";

    public static string Reset(UsageWindow w, DateTimeOffset now)
    {
        if (w.ResetsAt is not { } at) return w.Percent > 0 ? "" : "not started";
        if (at <= now) return "reset — refreshing soon";
        return $"resets {When(at, now)} · in {Span(at - now)}";
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

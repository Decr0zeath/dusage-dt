using System.Reflection;
using System.Text.RegularExpressions;

namespace Dusage;

/// <summary>One kind of change in a release ("Added", "Fixed", …) and its lines.</summary>
public sealed record ChangeGroup(string Kind, List<string> Items);

/// <summary>A release's entry in CHANGELOG.md.</summary>
public sealed record ReleaseNotes(Version Version, List<ChangeGroup> Groups);

/// <summary>
/// What changed in each release, from the CHANGELOG.md built into the exe. It's the same file the release workflow
/// publishes as the notes on GitHub, so the app and the release page always say the same thing.
/// </summary>
static partial class Changelog
{
    /// <summary>Newest first. "Unreleased" and anything else that isn't a version is left out.</summary>
    static readonly List<ReleaseNotes> Releases = Load();

    /// <summary>
    /// The releases after <paramref name="from"/> up to this one, newest first; only this one's when it isn't known
    /// which version ran before. Empty when there's nothing to tell: no entries, or <paramref name="from"/> is this
    /// version or a later one.
    /// </summary>
    public static List<ReleaseNotes> Since(string? from)
    {
        if (AppInfo.ParseVersion(AppInfo.Version) is not { } current) return [];
        var after = AppInfo.ParseVersion(from);
        return Releases.Where(r => r.Version <= current && (after is null ? r.Version == current : r.Version > after)).ToList();
    }

    static List<ReleaseNotes> Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
            if (stream is null) return [];
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception e)
        {
            Log.Error(e);
            return [];
        }
    }

    /// <summary>
    /// "## 1.4.0 - date" starts a release, "### Added" a group, and "- " an item; indented lines carry an item on.
    /// Everything else (the preamble, blank lines) is skipped.
    /// </summary>
    internal static List<ReleaseNotes> Parse(string markdown)
    {
        var releases = new List<ReleaseNotes>();
        ReleaseNotes? release = null;
        var continues = false; // the last line was an item, so an indented line adds to it
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## "))
            {
                var version = AppInfo.ParseVersion(line[3..].Trim().Split(' ')[0].Trim('[', ']'));
                release = version is null ? null : new ReleaseNotes(version, []);
                if (release is not null) releases.Add(release);
                continues = false;
            }
            else if (release is null)
            {
                continue;
            }
            else if (line.StartsWith("### "))
            {
                release.Groups.Add(new ChangeGroup(line[4..].Trim(), []));
                continues = false;
            }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                if (release.Groups.Count == 0) release.Groups.Add(new ChangeGroup("", []));
                release.Groups[^1].Items.Add(Plain(line[2..]));
                continues = true;
            }
            else if (continues && line.Length > 0 && char.IsWhiteSpace(line[0]))
            {
                var items = release.Groups[^1].Items;
                items[^1] += " " + Plain(line);
            }
            else
            {
                continues = false;
            }
        }
        releases.RemoveAll(r => r.Groups.All(g => g.Items.Count == 0));
        releases.Sort((a, b) => b.Version.CompareTo(a.Version));
        return releases;
    }

    /// <summary>[links](…) and `code` read as their text; **bold** is kept for the window to draw.</summary>
    static string Plain(string text) => Link().Replace(text.Trim(), "$1").Replace("`", "");

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Dusage;

/// <summary>Everything dusage keeps lives in %LOCALAPPDATA%\dusage — no tokens are ever copied there.</summary>
static class AppData
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dusage");

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }, // "layout": "line", not 1
    };

    public static T? Read<T>(string name)
    {
        try
        {
            var path = Path.Combine(Dir, name);
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default;
        }
        catch (Exception e)
        {
            Log.Error(e);
            return default;
        }
    }

    public static void Write<T>(string name, T value)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var path = Path.Combine(Dir, name);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Error(e);
        }
    }
}

/// <summary>The widget's shape: services stacked in a small box, or side by side in one line.</summary>
public enum WidgetLayout { Box, Line }

/// <summary>What the percentages count: how much of a limit is used, or how much is left.</summary>
public enum NumberStyle { Used, Left }

/// <summary>How reset times read: the time and a countdown, or just one of them.</summary>
public enum ResetStyle { Both, Time, Countdown }

/// <summary>The corner of the widget that stays put as rows come and go.</summary>
public enum WidgetCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>Every preference, as a new one has them; Restore defaults swaps in a fresh instance.</summary>
public sealed class Settings
{
    const string FileName = "settings.json";

    /// <summary>Widget position; null means the default spot above the clock.</summary>
    public double? Left { get; set; }
    public double? Top { get; set; }
    /// <summary>
    /// The widget's far edges, and the corner it keeps still: the one nearest the screen's corner when it was last
    /// dragged. Started up at a different size (a service signed in or out meanwhile), it lines up that corner, not
    /// its top-left. Older settings files don't have these; the corner is then worked out from the position.
    /// </summary>
    public double? Right { get; set; }
    public double? Bottom { get; set; }
    public WidgetCorner? Corner { get; set; }

    /// <summary>Back to the default spot above the clock.</summary>
    public void ForgetPosition()
    {
        Left = Top = Right = Bottom = null;
        Corner = null;
    }
    public WidgetLayout Layout { get; set; } = WidgetLayout.Box;
    /// <summary>"5h" and "7d" (or "1d", "mo") before the widget's bars.</summary>
    public bool BarLabels { get; set; } = true;
    public bool Topmost { get; set; } = true;
    public bool ShowPace { get; set; } = true;
    public double RefreshMinutes { get; set; } = 3;
    public double Opacity { get; set; } = 1;
    public bool CheckForUpdates { get; set; } = true;

    public NumberStyle Numbers { get; set; } = NumberStyle.Used;
    /// <summary>"44%" on the widget rather than a bare "44".</summary>
    public bool PercentSign { get; set; } = true;
    public ResetStyle ResetTimes { get; set; } = ResetStyle.Both;

    /// <summary>A limit turns amber, then red, once this much of it is used (percent), whichever way the numbers count.</summary>
    public bool WarningColors { get; set; } = true;
    public double AmberAt { get; set; } = 75;
    public double RedAt { get; set; } = 90;

    /// <summary>Services ("claude") and extra limits ("claude:weekly_opus") switched off in Settings.
    /// Everything else shows, so services added later appear without any setup.</summary>
    public List<string> Hidden { get; set; } = [];

    /// <summary>No settings file yet: this is the first run.</summary>
    [JsonIgnore] public bool IsNew { get; private set; }

    [JsonIgnore] public TimeSpan RefreshInterval => TimeSpan.FromMinutes(Math.Clamp(RefreshMinutes, 1, 60));

    /// <summary>A limit's percent used, counted the way the numbers read.</summary>
    public double Count(double usedPercent) => Numbers == NumberStyle.Left ? 100 - usedPercent : usedPercent;

    /// <summary>The pace tick's place (0..1) for a window whose time is this far gone, counted like the numbers.</summary>
    public double Pace(double elapsed) => Numbers == NumberStyle.Left ? 1 - elapsed : elapsed;

    public bool IsShown(string key) => !Hidden.Contains(key);

    public void SetShown(string key, bool shown)
    {
        Hidden.Remove(key);
        if (!shown) Hidden.Add(key);
    }

    public static Settings Load() => AppData.Read<Settings>(FileName) ?? new Settings { IsNew = true };

    public void Save() => AppData.Write(FileName, this);
}

static class StateStore
{
    const string FileName = "state.json";

    public static Dictionary<string, ProviderState> Load() => AppData.Read<Dictionary<string, ProviderState>>(FileName) ?? [];

    public static void Save(Dictionary<string, ProviderState> state) => AppData.Write(FileName, state);
}

/// <summary>"Start with Windows" through the per-user Run key; no admin rights needed.</summary>
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "dusage";

    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

static class Log
{
    public static void Error(Exception e)
    {
        try
        {
            Directory.CreateDirectory(AppData.Dir);
            var path = Path.Combine(AppData.Dir, "error.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTimeOffset.Now:u} {e}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Nowhere left to report to.
        }
    }
}

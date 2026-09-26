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

public sealed class Settings
{
    const string FileName = "settings.json";

    /// <summary>Widget position; null means the default spot above the clock.</summary>
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Topmost { get; set; } = true;
    public bool ShowPace { get; set; } = true;
    public double RefreshMinutes { get; set; } = 3;
    public double Opacity { get; set; } = 1;

    /// <summary>Services ("claude") and extra limits ("claude:weekly_opus") switched off in Settings.
    /// Everything else shows, so services added later appear without any setup.</summary>
    public List<string> Hidden { get; set; } = [];

    /// <summary>No settings file yet: this is the first run.</summary>
    [JsonIgnore] public bool IsNew { get; private set; }

    [JsonIgnore] public TimeSpan RefreshInterval => TimeSpan.FromMinutes(Math.Clamp(RefreshMinutes, 1, 60));

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

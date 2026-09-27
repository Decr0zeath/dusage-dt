using System.ComponentModel;
using System.Diagnostics;

namespace Dusage;

public sealed record Release(Version Version, string Url);

/// <summary>
/// Looks on GitHub for a newer release, once a day unless that's switched off, and installs it by running the
/// install.ps1 the installer leaves next to the exe: an update is exactly a reinstall. A copy that wasn't
/// installed that way (unzipped by hand, or a build) gets sent to the release page instead.
/// </summary>
sealed class Updater
{
    const string LatestUrl = "https://api.github.com/repos/Decr0zeath/dusage-dt/releases/latest";
    static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1), Daily = TimeSpan.FromDays(1), Retry = TimeSpan.FromHours(1);

    public Release? Available { get; private set; }
    public DateTimeOffset? CheckedAt { get; private set; }

    /// <summary>Why the last check or install attempt failed, worded for Settings.</summary>
    public string? Problem { get; private set; }
    public bool Checking { get; private set; }
    public bool Installing { get; private set; }

    /// <summary>A minute after starting, so a PC that just woke up has its network back.</summary>
    public DateTimeOffset NextCheck { get; private set; } = DateTimeOffset.Now + FirstCheck;

    /// <summary>Raised on the UI thread whenever any of the above changes.</summary>
    public event Action? Changed;

    static string? Installer =>
        Path.GetDirectoryName(Environment.ProcessPath) is { } dir && Path.Combine(dir, "install.ps1") is var script && File.Exists(script)
            ? script
            : null;

    /// <summary>This copy was installed by the install command, so it can update itself.</summary>
    public static bool CanInstall => Installer is not null;

    public async Task CheckAsync(CancellationToken ct)
    {
        if (Checking) return;
        Checking = true;
        Changed?.Invoke();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            using var release = await Net.GetJsonAsync(request, ct);
            var latest = ParseVersion(release.RootElement.Str("tag_name")) ?? throw new UsageException("GitHub listed a release without a version.");
            Available = latest > ParseVersion(AppInfo.Version)
                ? new Release(latest, release.RootElement.Str("html_url") ?? AppInfo.RepoUrl + "/releases/latest")
                : null;
            Problem = null;
            CheckedAt = DateTimeOffset.Now;
            NextCheck = DateTimeOffset.Now + Daily;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            if (e is not UsageException) Log.Error(e);
            Problem = "Couldn't check for updates: " + e.Message;
            NextCheck = DateTimeOffset.Now + Retry;
        }
        finally
        {
            Checking = false;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Runs the installer in a console window, where the download's progress and any error show. Once the download
    /// checks out it closes this copy and starts the new one; if anything fails first, this copy keeps running.
    /// </summary>
    public async Task InstallAsync()
    {
        if (Available is not { } release || Installing) return;
        if (Installer is not { } script)
        {
            AppInfo.Open(release.Url);
            return;
        }

        Installing = true;
        Changed?.Invoke();
        try
        {
            using var installer = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Update" },
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            });
            if (installer is not null) await installer.WaitForExitAsync();
        }
        catch (Win32Exception e)
        {
            Log.Error(e);
            Problem = "Couldn't start the installer: " + e.Message;
        }
        Installing = false; // still here, so the update didn't happen
        Changed?.Invoke();
    }

    /// <summary>"v1.2.0" → 1.2.0; anything after a '-' or '+' (a pre-release or build tag) is ignored.</summary>
    static Version? ParseVersion(string? text) =>
        Version.TryParse(text?.TrimStart('v', 'V').Split('-', '+')[0], out var version) ? version : null;
}

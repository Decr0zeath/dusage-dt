# dUsage/dt installer
#
#   Install or update:  irm https://raw.githubusercontent.com/Decr0zeath/dusage-dt/main/install.ps1 | iex
#   Uninstall:          Settings > Apps > dUsage/dt, or run the copy this leaves in the install folder with -Uninstall
#
# Per-user install; no admin rights needed. It downloads the latest release's dusage.zip, checks its SHA-256,
# puts it in %LOCALAPPDATA%\Programs\dusage, adds a Start menu entry, an entry in Settings > Apps, and
# "start with Windows" (switch that off in the widget's settings), then starts it.
#
# The widget's Update button runs the copy in the install folder with -Update, in a window of its own.

param([switch]$Uninstall, [switch]$Update)

& {
    param([bool]$Uninstall, [bool]$Update)

    # With -Update the window closes as soon as this ends, so hold any error on screen until it's read.
    trap { if ($Update) { Write-Host "The update failed: $_" -ForegroundColor Red; Read-Host 'Press Enter to close' | Out-Null }; break }

    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue' # the progress bar makes Invoke-WebRequest crawl on Windows PowerShell 5.1

    $repo = 'Decr0zeath/dusage-dt'
    $dir = Join-Path $env:LOCALAPPDATA 'Programs\dusage'
    $exe = Join-Path $dir 'dusage.exe'
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'dUsage.lnk'
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $appKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\dusage'

    # Any running copy, wherever it was started from: only one runs at a time, and a new one would
    # just hand over to the old one.
    function Stop-Widget {
        Get-Process dusage -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }
    }

    if ($Uninstall) {
        Stop-Widget
        Remove-ItemProperty $runKey -Name dusage -ErrorAction SilentlyContinue
        Remove-Item $appKey -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $shortcut -Force -ErrorAction SilentlyContinue
        foreach ($path in $dir, (Join-Path $env:LOCALAPPDATA 'dusage'), (Join-Path $env:TEMP '.net\dusage')) {
            Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
        }
        cmdkey /delete:dusage:github.com *> $null # Sign in with GitHub, if it was used
        Write-Host 'dUsage/dt is uninstalled.' -ForegroundColor Green
        return
    }

    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $download = "https://github.com/$repo/releases/latest/download"
    $temp = Join-Path ([IO.Path]::GetTempPath()) ('dusage-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $temp | Out-Null
    try {
        Write-Host 'Downloading dUsage/dt (about 56 MB)...'
        Invoke-WebRequest "$download/dusage.zip" -OutFile "$temp\dusage.zip" -UseBasicParsing
        Invoke-WebRequest "$download/dusage.zip.sha256" -OutFile "$temp\dusage.zip.sha256" -UseBasicParsing
        Invoke-WebRequest "https://raw.githubusercontent.com/$repo/main/install.ps1" -OutFile "$temp\install.ps1" -UseBasicParsing

        $expected = ((Get-Content "$temp\dusage.zip.sha256" -Raw).Trim() -split '\s+')[0]
        $actual = (Get-FileHash "$temp\dusage.zip" -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw 'The download is damaged (SHA-256 mismatch). Nothing was installed; please try again.' }
        Expand-Archive "$temp\dusage.zip" $temp -Force

        Stop-Widget
        New-Item -ItemType Directory $dir -Force | Out-Null
        Copy-Item "$temp\dusage.exe" $exe -Force
        Copy-Item "$temp\install.ps1" (Join-Path $dir 'install.ps1') -Force # used by Settings > Apps > Uninstall
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }

    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    $link.TargetPath = $exe
    $link.WorkingDirectory = $dir
    $link.Description = 'Claude, ChatGPT, Copilot, Gemini and Kimi usage limits, at a glance'
    $link.Save()

    $version = (Get-Item $exe).VersionInfo.ProductVersion
    New-Item $appKey -Force | Out-Null
    $entry = @{
        DisplayName     = 'dUsage/dt'
        DisplayVersion  = $version
        Publisher       = 'Decr0zeath'
        DisplayIcon     = $exe
        InstallLocation = $dir
        URLInfoAbout    = "https://github.com/$repo"
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dir\install.ps1`" -Uninstall"
    }
    foreach ($name in $entry.Keys) { Set-ItemProperty $appKey -Name $name -Value $entry[$name] }
    foreach ($name in 'NoModify', 'NoRepair') { Set-ItemProperty $appKey -Name $name -Value 1 -Type DWord }
    Set-ItemProperty $appKey -Name EstimatedSize -Value ([int]((Get-Item $exe).Length / 1KB)) -Type DWord

    Set-ItemProperty $runKey -Name dusage -Value "`"$exe`""

    Start-Process $exe
    Write-Host ''
    if ($Update) {
        Write-Host "Updated to dUsage/dt $version." -ForegroundColor Green
        Start-Sleep 3
        return
    }
    Write-Host "dUsage/dt $version is installed and running." -ForegroundColor Green
    Write-Host '  Look for the small pill at the bottom-right of your screen; hover it for details.'
    Write-Host '  Right-click it for settings. It starts with Windows (you can turn that off there).'
    Write-Host '  It shows Claude, ChatGPT, GitHub Copilot, Gemini and Kimi limits using the sign-ins'
    Write-Host '  Claude Code, Codex, the GitHub CLI, Gemini CLI and Kimi Code saved (Copilot can also sign in from Settings).'
    Write-Host '  It tells you when an update is out; right-click it to install one.'
    Write-Host '  To uninstall: Settings > Apps > dUsage/dt.'
} $Uninstall.IsPresent $Update.IsPresent

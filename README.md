<img src="docs/icon.png" width="64" alt="dUsage/dt icon">

# dUsage/dt

A tiny always-on-top Windows widget that shows how much of your **Claude** and **ChatGPT** plan limits you've used: the 5-hour window and the weekly window, at a glance.

![The widget: a Claude row and a ChatGPT row, each with a 5-hour bar and a weekly bar](docs/widget.png)

> **Made for Claude Code and Codex users.** dUsage/dt reads the sign-in that Claude Code and Codex save on your PC. If you only use claude.ai or ChatGPT in a browser, there's nothing for it to read.

## Install

Paste this into PowerShell on Windows 10 or 11:

```powershell
irm https://raw.githubusercontent.com/Decr0zeath/dusage-dt/main/install.ps1 | iex
```

No admin rights, and nothing else to install (.NET is built in). The command:

- downloads the latest release and checks its SHA-256
- installs it to `%LOCALAPPDATA%\Programs\dusage`
- adds a Start menu entry and an entry in Settings › Apps
- sets it to start with Windows, then starts it

Run the same command again to update.

**Downloading by hand instead?** Get `dusage.zip` from [Releases](https://github.com/Decr0zeath/dusage-dt/releases/latest), unzip it, and run `dusage.exe`. The app isn't code-signed, so Windows SmartScreen may say "Windows protected your PC"; choose **More info › Run anyway**. The install command doesn't hit that prompt.

**Uninstall:** Settings › Apps › dUsage/dt › Uninstall. That removes the app, its Start menu entry, the start-with-Windows entry and its settings.

## Reading the widget

|                     | left bar      | right bar     |
| ------------------- | ------------- | ------------- |
| Claude logo         | 5-hour window | weekly window |
| OpenAI logo         | 5-hour window | weekly window |

- The number is the percent used. A bar turns **amber at 75%** and **red at 90%**.
- The thin **tick** marks how much of the window's *time* has passed. If the fill runs past the tick, you're using the limit faster than the clock.
- A **faded logo** means that row couldn't update just now (for example, Claude Code's sign-in expired). The numbers are the last ones it got; hover to see why.
- A service you aren't signed in to doesn't appear at all.

Hover for details: reset times, plan, and when it last updated.

![The hover tooltip with reset times for each window](docs/tooltip.png)

**Drag** to move it anywhere, even onto the taskbar. **Double-click** to refresh. **Right-click** for *Refresh now*, *Settings…* and *Exit*.

## Settings

Right-click the widget and choose **Settings…**, or launch dUsage/dt from the Start menu while it's running. That also brings back a widget you've lost track of. Changes apply immediately.

![The settings window: services with on/off switches, and widget options](docs/settings.png)

- **Services:** switch Claude or ChatGPT on or off. If your plan has separate limits for particular models (for example, an Opus weekly cap on Claude Max), they appear under the service with their own switch, and turning one on adds a row for it.
- **Widget:** always on top, start with Windows, the pace tick, how often to refresh (1–15 minutes), opacity, and a button to reset the position.

## How it works

It asks each service for your usage every 3 minutes by default (every 15 while you're away from the PC), using the sign-in the CLI already saved:

|         | sign-in it reads                          | endpoint                             | same numbers as      |
| ------- | ----------------------------------------- | ------------------------------------ | -------------------- |
| Claude  | `%USERPROFILE%\.claude\.credentials.json` | `api.anthropic.com/api/oauth/usage`  | Claude Code `/usage` |
| ChatGPT | `%USERPROFILE%\.codex\auth.json`          | `chatgpt.com/backend-api/wham/usage` | Codex `/status`      |

(`CLAUDE_CONFIG_DIR` and `CODEX_HOME` are honored if you've set them.)

- **Claude:** its limits are shared by claude.ai, the desktop app and Claude Code, so this row covers all of them.
- **ChatGPT:** this row shows your ChatGPT plan's **Codex** limits. Those are the 5-hour and weekly windows ChatGPT has; regular ChatGPT chat doesn't expose a meter like this.

The sign-in files are **only ever read**. dUsage/dt never refreshes, copies or writes a token, and sends it only to the endpoint above, so it can't interfere with Claude Code or Codex. The trade-off: Claude Code's sign-in expires a few hours after you last used Claude Code. Until you use it again, the Claude row keeps its last numbers with a faded logo. A bar whose reset time passes still drops to 0.

Both endpoints are the ones the official CLIs call. They aren't a documented public API and could change. If numbers stop appearing, run the probe below.

Settings and the last fetched numbers live in `%LOCALAPPDATA%\dusage`. No tokens are stored there.

## Troubleshooting

```powershell
& "$env:LOCALAPPDATA\Programs\dusage\dusage.exe" --probe | Out-String       # fetch once and print what the widget would show
& "$env:LOCALAPPDATA\Programs\dusage\dusage.exe" --probe-raw | Out-String   # also print each response's layout (ids and emails redacted)
```

Unexpected errors are logged to `%LOCALAPPDATA%\dusage\error.log`.

## Build from source

You need the .NET 10 SDK.

- `build.cmd` produces `dist\dusage.exe`, a single file with .NET built in.
- `dotnet run --project src/Dusage` runs it while you work on it. Exit a running widget first, since only one copy runs at a time.
- Pushing a tag such as `v1.2.0` runs [the release workflow](.github/workflows/release.yml). It builds `dusage.zip` and publishes a GitHub release, which the install command picks up.

The app is a small WPF project in `src/Dusage`:

- `ClaudeSource.cs` and `CodexSource.cs` fetch usage. To add a service, implement `IUsageSource` and give it a logo in `Logos.cs`.
- `MainWindow` is the widget.
- `SettingsWindow` is the settings window.

## License

[MIT](LICENSE) © Decr0zeath. Made with Claude.

Not affiliated with Anthropic or OpenAI. Claude is a trademark of Anthropic, PBC; ChatGPT and Codex are trademarks of OpenAI. The logos come from [Simple Icons](https://simpleicons.org) (CC0).

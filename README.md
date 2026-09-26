# dusage

A tiny always-on-top Windows widget that shows how much of your **Claude** and **ChatGPT** plan limits you've used, for both the 5-hour window and the weekly window, in about 150 × 37 pixels.

![The widget: two rows, C and G, each with a 5-hour bar and a weekly bar](docs/widget.png)

|                                   | left bar      | right bar     |
| --------------------------------- | ------------- | ------------- |
| **C** · Claude (orange)           | 5-hour window | weekly window |
| **G** · ChatGPT / Codex (green)   | 5-hour window | weekly window |

- The number is the percent used. A bar turns **amber at 75%** and **red at 90%**.
- The thin **tick** marks how much of the window's *time* has passed. If the fill runs past the tick, you're using the limit faster than the clock.
- A **dimmed row** means its numbers may be out of date. Hover to see why.

Hover for the details: reset times, plan, and when it last updated.

![The hover tooltip with reset times for each window](docs/tooltip.png)

**Drag** to move it (it remembers the spot, and it can sit on top of the taskbar). **Double-click** refreshes. **Right-click** gives *Refresh now*, *Always on top*, *Start with Windows* and *Exit*.

## Setup

Needs Windows 10 or 11. Building needs the .NET 10 SDK; running needs only the .NET 10 Desktop Runtime.

1. Sign in to the CLIs the widget reads from (you probably already are):
   - **Claude Code**, with your Claude subscription (`claude`, then `/login`)
   - **Codex**, with your ChatGPT account (`codex login`)
2. Run `build.cmd`. It produces `dist\dusage.exe`, a single file of about 230 KB.
3. Start `dist\dusage.exe`, then right-click it and choose **Start with Windows**.

To rebuild while the widget is running, exit it first (right-click, *Exit*) so the exe can be replaced.

## How it works

Every 3 minutes, or every 15 minutes while you're away from the PC, it asks each service for your usage. It signs in with the credentials the CLI has already saved on this PC:

|         | sign-in it reads                          | endpoint                             | same numbers as    |
| ------- | ----------------------------------------- | ------------------------------------ | ------------------ |
| Claude  | `%USERPROFILE%\.claude\.credentials.json` | `api.anthropic.com/api/oauth/usage`  | Claude Code `/usage` |
| ChatGPT | `%USERPROFILE%\.codex\auth.json`          | `chatgpt.com/backend-api/wham/usage` | Codex `/status`    |

(`CLAUDE_CONFIG_DIR` and `CODEX_HOME` are honored if you've set them.)

Claude's limits are shared by claude.ai, the desktop app and Claude Code, so the Claude row covers all of them. The ChatGPT row shows your ChatGPT plan's **Codex** limits. Those are the 5-hour and weekly windows ChatGPT has; regular ChatGPT chat doesn't expose a meter like this.

The sign-in files are **only ever read**. The widget never refreshes, copies or writes a token, so it can't interfere with Claude Code or Codex. The trade-off: Claude Code's token expires a few hours after you last used Claude Code. Until you use it again, the Claude row stays dimmed and shows the last numbers it fetched. When a window's reset time passes, the row still drops that bar to 0. Codex's sign-in lasts much longer.

Both endpoints are the ones the official CLIs call. They aren't a documented public API and could change; if numbers stop appearing, run the probe below.

## Settings

`%LOCALAPPDATA%\dusage\settings.json` is written when you move the widget or change an option. Edit it while the widget is closed.

| key              | default        | meaning                                         |
| ---------------- | -------------- | ----------------------------------------------- |
| `left`, `top`    | bottom-right   | position; delete both to go back to the corner  |
| `topmost`        | `true`         | stay above other windows                        |
| `refreshMinutes` | `3`            | how often to fetch while you're active (1–60)   |
| `opacity`        | `1`            | whole-widget opacity (0.2–1)                    |

The same folder holds `state.json`, which holds the last numbers so a restart shows them right away. It contains no tokens.

## Troubleshooting

```powershell
.\dist\dusage.exe --probe | Out-String       # fetch once and print what the widget would show
.\dist\dusage.exe --probe-raw | Out-String   # also print each response's layout (ids and emails redacted)
```

Unexpected errors go to `%LOCALAPPDATA%\dusage\error.log`.

## Code

`src/Dusage` is a small WPF app:

- `ClaudeSource.cs` and `CodexSource.cs` read a sign-in and fetch the usage.
- `MainWindow.xaml(.cs)` is the widget: polling, drawing, tooltip and menu.
- `Meter.cs` is the bar.
- `Storage.cs` handles settings, the cached state, the Start-with-Windows entry and the log.

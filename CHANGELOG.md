# Changelog

What's new in each release of dUsage/dt.

This file is built into the app: the first time dUsage/dt starts after an update, the expanded widget opens on Info and lists what changed since the version you had. Pushing a release tag publishes the same entry as the release's notes on GitHub, and the release fails if its version has no entry here.

How to write an entry:

- Put changes under `## Unreleased` as you make them. Before tagging, rename it to the version and date, like `## 1.5.0 - 2026-11-20`, and start a new empty `## Unreleased` above it.
- Group them under `### Added`, `### Changed`, `### Fixed` or `### Removed`.
- Write each `- ` line for the people using the widget: what they'll see or can now do, in a sentence or two. `**bold**` shows bold in the app.

## Unreleased

### Added
- **What's new:** the first time dUsage/dt starts after an update, the expanded widget opens on Info to show what was added, changed, fixed or removed since your last version. It stays under Info until the next update.

## 1.4.1 - 2026-11-16

### Fixed
- **In the tray** now shows on Windows 11's taskbar, just left of the tray icons. Before, nothing appeared there.

## 1.4.0 - 2026-11-16

### Added
- **In the tray** (Settings › Widget › Placement): your services built into the taskbar, just left of the tray icons, instead of the floating widget. Each logo has its 5-hour % at the top left and its weekly % at the top right. Hover for details, click to expand, right-click for the menu.

## 1.3.0 - 2026-09-27

### Added
- **GitHub Copilot, Gemini and Kimi**, alongside Claude and ChatGPT.
- **Sign in with GitHub** on Copilot's row in Settings › Services, for Copilot users without the GitHub CLI.
- **Line** layout: every service side by side in one row, to sit on the taskbar.
- Bar labels: a tiny 5h, 7d and so on before each bar.
- Show the percent left instead of the percent used, with or without the % sign, and reset times as the time, a countdown or both.
- Switch the warning colors off, or choose where amber and red start.
- **Restore defaults** at the bottom of Settings.

### Changed
- Settings is now in sections (Services, Widget, Numbers and Colors), one open at a time. The expanded widget keeps one size on every page.

### Fixed
- The widget no longer wanders sideways when a service signs in or out.
- Settings switches now work with screen readers.

## 1.2.0 - 2026-09-27

### Added
- **Updates:** dUsage/dt checks GitHub once a day, and **Update to …** in the right-click menu installs the new version. The daily check can be switched off under Info.
- **The expanded widget:** right-click › Expand turns the widget into a bigger panel with every limit in full and its reset time, plus Settings and Info.
- A tray icon: click it to expand, right-click it for the menu.

### Changed
- A new look in deep space colors, and an hourglass logo.

### Removed
- The separate settings window. Its options are in the expanded widget now.

## 1.1.0 - 2026-09-27

### Added
- First release: your Claude and ChatGPT limits in a small always-on-top widget, with 5-hour and weekly bars that turn amber and red, and a tick for how much of each window's time has passed.
- One-line install, with an entry in Settings › Apps and start with Windows.

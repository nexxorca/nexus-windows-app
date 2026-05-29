# Nexus Windows App

A native WPF tray application that keeps your Claude Code session transcripts synced to the Nexus server and your `~/.claude/` configuration in sync with the PM's source-of-truth snapshot.

## Features

**Transcript Sync** — Automatically discovers and uploads Claude Code session transcripts from `~/.claude/projects/` to the Nexus server. Upload detection, retry on transient failure, progress tracking via the activity log. Active sessions are deferred until the next cycle.

**Auto-Update** — Velopack-powered auto-update system. On startup, after login, and every 4 hours, the app checks for new releases and prompts you to restart. No manual installer needed.

**AI Config Sync** — Keeps your `~/.claude/` (agents, skills, conventions, hooks, settings) in sync with the PM's server-side configuration snapshot. Automatic on login, on-demand via a button, and every 4 hours. Backs up your prior config before applying updates and can roll back on failure.

## Installation

Download `Setup.exe` from the latest release or install via the auto-update mechanism after first run.

**Requirements:** Windows 10 or later, .NET 8.0 runtime (included in the installer; self-contained app).

## First Run

1. Launch the app. A tray icon appears in the system tray (bottom-right corner on Windows).
2. Right-click the tray icon and select **Open Nexus**.
3. Enter your Nexus server URL, email, and password.
4. Log in. The app begins transcript sync immediately and checks for updates.

The app runs in the system tray after you close the window — it does not appear in the taskbar.

## Usage

### Tray Icon Menu

Right-click the tray icon to:

- **Open Nexus** — Launch the main window showing sync status and recent activity
- **Sync Now** — Trigger an immediate transcript sync cycle
- **Activity Log** — View recent upload, error, and state-change events
- **Settings** — Configure sync interval, launch-on-startup, and view the app version
- **Check for Updates** — Manually check for app updates (automatic every 4 hours)
- **Check AI Config** — Manually check for and apply Claude Code configuration updates
- **Logout** — Clear your Nexus credentials and stop syncing
- **Exit** — Quit the application

### Main Window

The main window displays:

- **Sync Status** — Whether the app is currently syncing transcripts
- **Last Sync Time** — When the most recent sync cycle completed
- **Quick Actions** — Buttons to sync, open logs, open settings, check updates, check AI config, and logout

The window minimizes to the tray when closed.

### Activity Log

All events (uploads, errors, state changes, update checks, config syncs) are logged to the activity window. Logs are timestamped, categorized by type (ok, warning, error), and retained for inspection.

### Settings

- **Sync Interval** — Minutes between automatic transcript scans (default: 5)
- **Launch on Startup** — Start the app when Windows boots
- **Version** — Current app version (bottom-left, greyed)

## Auto-Update

The app checks for updates on startup, after login, and every 4 hours. When an update is available, a popup prompts you to **Restart Now** or **Later**.

- **Restart Now** — Downloads and applies the update, then relaunches the app. No manual steps needed.
- **Later** — Defers the update until the next check (after 4 hours or manual "Check for Updates").

Your login credentials are persisted securely and are not lost after an update.

## AI Config Sync

The app keeps your `~/.claude/` directory (Claude Code agents, skills, conventions, hooks) in sync with the PM's server configuration snapshot.

### How It Works

On login, after logout, and every 4 hours, the app:

1. Checks if Claude Code is installed at the standard path (`%LOCALAPPDATA%\Programs\claude\claude.exe`)
2. Fetches the latest configuration snapshot from the Nexus server
3. Compares the snapshot version against the applied version marker
4. If a new version is available, shows an update popup

**The Popup** — "Close Claude Code sessions before applying. Continue with vX → vY?" with **Apply Now** and **Later** buttons.

- **Apply Now** — Backs up your current `~/.claude/`, downloads all files from the snapshot, verifies integrity, atomically applies the changes, and deletes any files no longer in the snapshot (outside the exclusion list).
- **Later** — Defers the update until the next trigger (next login, next 4h check, or manual button).

### What Gets Synced

The snapshot includes all config files and directories except those in the exclusion list:

**Protected (never overwritten or deleted):**
- `projects/` — Your local test projects
- `settings.local.json` — Personal overrides
- `.credentials*` — Authentication files
- `backups/`, `cache/`, `sessions/`, `plugins/`, `telemetry/`, etc.

**Synced (updated/deleted as needed):**
- `agents/` — Agent definitions
- `skills/` — Skill modules
- `conventions/` — Code and process conventions
- `hooks/` — Custom event hooks
- `settings.json` — PM-controlled base settings
- Root markdown files (MEMORY.md, etc.)

### Manual Escape Hatch

If something goes wrong, your pre-apply backup is retained in `~/.claude/backups/{YYYY-MM-DD_HHmmss}/`. The last 5 backups are kept; older ones are automatically deleted.

To restore a prior version manually:
1. Copy files from the backup folder back into `~/.claude/`
2. Delete `%LOCALAPPDATA%\Nexus\ai-config-version` to force a re-check on the next trigger

The app is resilient: any failure (network, disk, corruption) is logged, and the next trigger retries from scratch.

### Requirements

Claude Code must be installed at the standard installer path. If it's not detected, the app logs a warning and silently skips the check — transcript sync and other features continue working normally.

## Troubleshooting

### Transcripts not uploading

1. Check the **Activity Log** (tray menu → Activity Log) for error messages
2. Ensure you're logged in and your Nexus token is valid
3. Try **Sync Now** from the tray menu
4. Check that transcript files exist in `~/.claude/projects/` (hidden by default on Windows; enable "Show Hidden Files" in Folder Options)

### Update won't apply

1. Close any other instances of the Nexus app
2. Ensure you have write permissions to your `%AppData%\Nexus\` folder
3. Check the activity log for network or disk errors
4. Try manually checking for updates (tray → Check for Updates)

### AI Config Sync issues

1. Ensure Claude Code is installed at `%LOCALAPPDATA%\Programs\claude\claude.exe`
2. Close Claude Code before applying updates (the popup warns about this)
3. Check the Activity Log for sync errors
4. If an apply failed, your `~/.claude/` is either fully on the old version or fully on the new version — never half-applied. You can restore from a backup folder if needed.

## Development

This is a three-project .NET 8.0 solution:

- **Nexus.App** — WPF UI, tray management, update checks
- **Nexus.Core** — API client, configuration, logging
- **Nexus.Sync** — Transcript scanning, parsing, upload orchestration, AI config sync

See `project_docs/structure.md` for detailed architecture and component reference.

### Building

```bash
dotnet publish src/Nexus.App/Nexus.App.csproj -c Release -r win-x64 --self-contained -o publish
```

Self-contained output (~70MB nupkg, ~162MB unpacked) requires no .NET runtime on the target machine.

## License

NEXXOR inc. — All rights reserved.

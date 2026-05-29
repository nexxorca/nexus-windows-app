# Changelog

## [1.2.0] - 2026-05-29

### Added
- AI Config Sync — pulls PM's `~/.claude/` snapshot from Nexus on login, every 4 hours, and via manual MainWindow button
- `AiConfigUpdatePromptWindow` popup surfaces "close Claude Code first" warning on every applicable trigger
- `AiConfigApplyService` — backup → download → SHA-256 verify → atomic two-phase write/rename → rollback-on-failure → perfect-fit delete → version marker persist
- `ClaudeCodeInstallProbe` — single `File.Exists` at `%LOCALAPPDATA%\Programs\claude\claude.exe`; re-checked on every trigger
- Backup retention: last 5 pre-apply snapshots kept in `~/.claude/backups/`
- Version marker at `%LOCALAPPDATA%\Nexus\ai-config-version` (machine-local; does not roam)
- `NexusApiClient` gains `GetAiConfigManifest()` and `DownloadAiConfigFile()` over the existing Sanctum bearer

## [1.1.0] - 2026-05-28

### Added
- MainWindow dashboard — tray-launched single-instance window with state panel (user, last sync, status, version) and action buttons (Sync Now, Activity Log, Settings, Check for Updates, Logout)
- "Open Nexus" tray menu item (visible only when logged in)
- `SyncEngine` event surface — `IsRunning`, `LastSync`, `SyncStarted`, `SyncCompleted` — consumed by both tray and MainWindow
- `SyncResult` immutable record carrying sync cycle counts, timestamp, and status

### Changed
- Sync state (running flag, last sync time, error flag) moved from `TrayIconManager` to `SyncEngine` — single source of truth for tray + MainWindow
- Shared UI actions (`TriggerSync`, `ShowActivityLogWindow`, `ShowSettingsWindow`) centralized in `App` so tray menu and MainWindow buttons call the same paths
- Tray "Exit" now routes through `App.PrepareForShutdown()` to close MainWindow before `App.Shutdown()` (required under `OnExplicitShutdown` mode)

### Removed
- `SyncEngine.LastSyncHadErrors` — replaced by `SyncResult.Status` on `LastSync`
- `TrayIconManager` local sync-state fields (`_lastSyncTime`, `_isSyncing`, `_lastSyncFailed`) and the private `RunSync()` wrapper

## [1.0.11] - 2026-03-17

### Added
- "Launch on startup" setting — registers app in Windows startup via HKCU registry key
- StartupManager static helper for registry read/write
- Checkbox in Settings window to toggle launch-on-startup

## [1.0.10] - 2026-03-16

### Fixed
- Sync engine no longer re-uploads active session files (defers files modified within last 5 minutes)
- Upload timeout increased from 120s to 300s to prevent false timeouts on larger transcripts via S3
- Failed upload errors now include the filename in the activity log for easier debugging
- Added 1 retry with 5s backoff for failed transcript uploads (re-reads file between attempts)

### Added
- `Dev branch: dev` config line in CLAUDE.md for push-dev skill
- `project_docs/structure.md` — full project structure documentation

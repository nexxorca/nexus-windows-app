# Changelog

## [1.2.10] - 2026-07-03

### Changed
- Documentation reconciliation, no functional code changes. AI Config Sync assessment updated to reflect the shipped wipe-and-replace rewrite (30 findings resolved, CRITICAL cleared); completed Main Window plan archived; hardening plan release step closed. First commit to capture the 1.2.8/1.2.9 work that was released without being committed.

## [1.2.9] - 2026-06-09

### Fixed
- Login and 4-hour-timer paths now `await TriggerUpdateCheck()` before calling `TriggerAiConfigCheck()`, eliminating the double-dialog race where AI Config's fast JSON fetch finished before Velopack's nupkg download, causing both prompts to appear simultaneously.
- `_pendingVelopackUpdate` is now set as soon as `CheckForUpdatesAsync()` returns a non-null result — before `DownloadUpdatesAsync()` begins — so any concurrent manual AI Config trigger observes the flag immediately and defers correctly. The flag is also cleared in the catch block so a failed download does not block AI Config for the rest of the session.

## [1.2.8] - 2026-06-09

### Added
- "Enable AI Config Sync" checkbox in Settings — when unchecked, all triggers (login, 4-hour timer, manual button) short-circuit; auto-triggers log silently, manual trigger shows an informational dialog. Default: enabled (existing installs unchanged on upgrade).

## [1.2.5] - 2026-06-08

### Added
- Backup-before-wipe — managed roots are copied to `~/.claude/backups/{timestamp}/` before each apply. Keeps last 5 backups; older ones are pruned automatically. If backup fails, the apply aborts (wipe does not proceed).

### Fixed
- AI Config Sync prompt and Velopack restart prompt no longer fire simultaneously. When a Velopack update is pending, AI sync defers until after the user restarts into the new version.

### Removed
- Post-write SHA verification check — HTTPS already establishes transport integrity; the manifest SHA is not a trust anchor (manifest and files come from the same server). Removed the verify step entirely; apply pipeline is now backup → wipe → download → done.

## [1.2.4] - 2026-06-08

### Changed
- AI Config Sync apply pipeline simplified — files now download directly to final location (no temp-then-move), SHA verification happens AFTER placement as a diagnostic warning instead of a pre-write gate. Files always get placed; mismatches are reported to the user but don't block the apply.

### Removed
- Temp-dir download staging and pre-write SHA gate that blocked apply on benign manifest/file drift (line endings, stale deploys, etc.).

## [1.2.3] - 2026-06-08

### Fixed
- Removed `ClaudeCodeInstallProbe` that gated all AI sync triggers on a path (`%LOCALAPPDATA%\Programs\claude\claude.exe`) that does not exist on VSCode-extension installs. The feature has been a no-op on the entire NEXXOR dev fleet since 1.2.0.

### Changed
- Manual "Check AI Config" button now shows a `MessageBox` for every visible outcome: already up to date, no snapshot available, apply success, apply failure, and unexpected error. Auto-triggers (login hook, 4h timer) remain silent except for the activity log.

## [1.2.2] - 2026-06-08

### Changed
- AI Config Sync apply pipeline replaced with wipe-and-replace — deletes managed roots entirely, then writes selected files from manifest
- Manifest schema gains `managed_roots: string[]` declaring the scope of server-managed directories (e.g., `agents/`, `conventions/`)
- `AiConfigApplyResult.Status` converted from string to enum `AiConfigApplyStatuses` (values: `applied`, `aborted`, `skipped_no_snapshot`, `skipped_no_install`)

### Security
- Path containment checks at all I/O sites — ensures write targets fall within declared `managed_roots`
- Manifest path validation rejects traversal sequences (`..`), absolute paths, control chars, colons, and embedded backslashes
- Symlink and junction pre-flight refusal — apply aborts if any reparse point exists under managed roots
- Claude Code running probe — apply defers if `claude.exe` is running; user prompted to close it and retry
- HTTPS enforcement in `NexusApiClient.Configure()` and `LoginWindow` URL field (localhost exception for dev)
- Exception boundary around `TriggerAiConfigCheck` — outer try/catch prevents apply failures from crashing the dispatcher or vanishing silently

### Removed
- Backup ladder (`~/.claude/backups/`) — no longer created or retained; legacy backups cleaned up automatically on first 1.2.2 launch
- Rollback machinery and `rolled_back` status
- Perfect-fit delete logic and `ExclusionList`
- Legacy `ai-config-version` marker (one-shot cleanup on startup)

### Tests
- Introduced `INexusApiClient` interface for integration test seams
- Added `AiConfigApplyServiceTests.cs` (10 tests) — wipe-and-replace pipeline, path validation, symlink pre-flight, partial failure idempotence
- Added `AiConfigPathsTests.cs` (9 tests) — path containment, reparse-point detection, manifest entry validation
- Added `NexusApiClientAiConfigTests.cs` (14 tests) — manifest fetch with/without `managed_roots`, 404/401/timeout, JSON parsing, HTTPS validation
- Extended `ManifestFingerprintTests.cs` — Unicode paths, case sensitivity, single-file manifests, duplicate entries; replaced brittle constant-pinned test with behavioral assertions
- Total: 57 tests passing

### Fixed
- `Logout()` converted from `async void` to `async Task` — fire-and-forget intent now explicit at call sites

## [1.2.1] - 2026-06-02

### Added
- Unit test scaffolding — `Nexus.Sync.Tests` xUnit project covering `ManifestFingerprint`
- `ManifestFingerprint.Compute(manifest)` — sha256 over sorted `{path}|{sha}` pairs; trigger catches server-side selection changes that leave `manifest.version` unchanged
- `AiConfigPaths.FingerprintPath` (`%LOCALAPPDATA%\Nexus\ai-config-fingerprint`)

### Changed
- AI Config Sync **disabled** in this release — `TriggerAiConfigCheck()` early-returns pending a server-side kill switch on Nexus web. All triggers (login, 4h timer, MainWindow button) silently no-op. Re-enable by removing the early `return;` at the top of the method
- AI Config Sync trigger compares manifest fingerprint instead of `manifest.version`
- `AiConfigUpdatePromptWindow` takes only the new manifest version (no old → new pair)
- `AiConfigApplyService` persists the fingerprint marker on success and deletes the legacy `ai-config-version` marker

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

# Nexus Windows App - Structure

> Last updated: 2026-06-02 | Project: `nexus-windows-app`

## Purpose
Native WPF tray application that synchronizes Claude Code session transcripts from `~/.claude/projects/` to the Nexus server via API, with Velopack auto-update support.

---

## Solution Structure (3 projects + 1 test)

```
src/
├── Nexus.App/                       # WPF executable — tray icon, windows, update checks
├── Nexus.Core/                      # Shared library — API client, config, logging
└── Nexus.Sync/                      # Sync engine — scanning, parsing, upload orchestration

tests/
└── Nexus.Sync.Tests/                # xUnit unit tests — currently covers ManifestFingerprint
```

- **Framework**: .NET 8.0 (`net8.0-windows`), self-contained win-x64
- **Shutdown**: `OnExplicitShutdown` — app lives in tray, no main window

---

## Models (11)

```
src/Nexus.Core/Models/
├── AppConfig.cs                     # NexusUrl, AuthToken (DPAPI encrypted), UserHashId, UserName, SyncIntervalSeconds, LaunchOnStartup
├── ApiResult.cs                     # Success, StatusCode, Message, ErrorDetail; IsAuthError (401), IsValidationError (422); generic variant ApiResult<T>
├── AiConfigManifest.cs              # Version, GeneratedAt, Files[] — manifest DTO from /api/v1/ai-config/manifest
└── AiConfigManifestFile.cs          # Path, Sha, Size — single file entry in manifest

src/Nexus.Sync/Models/
├── TranscriptFile.cs                # FilePath, ProjectSlug, FileSize — discovered .jsonl file
├── SubagentInfo.cs                  # AgentId, SubagentType, ToolUseId — detected subagent invocation
├── SyncState.cs                     # Files dict<string, FileState> — upload tracking; FileState: Size, Timestamp, Status
├── SyncResult.cs                    # Uploaded, Skipped, ParseSkipped, Errors, CompletedAt, Status — immutable result emitted by SyncEngine.SyncCompleted
├── AiConfigApplyResult.cs           # Success, Status ("applied"|"rolled_back"|"aborted"|"skipped_no_snapshot"|"skipped_no_install"), Version?, Message?
└── AiConfigPaths.cs                 # Constants: ClaudeRoot, BackupsRoot, VersionMarkerPath, FingerprintPath; ExclusionList; IsExcluded(relPath) predicate
```

### AppConfig Details
- Storage: `%AppData%\Nexus\config.json`
- Token encryption: DPAPI (`DataProtectionScope.CurrentUser`), stored as Base64
- Methods: `Load()`, `Save()`, `SetLoginData()`, `ClearLoginData()`, `DecryptedToken`
- Legacy import: reads `.claude/nexus.env` on first load

---

## Services (11)

```
src/Nexus.Core/Services/
├── NexusApiClient.cs                # API client — Login, UploadTranscript, RevokeToken, GetAiConfigManifest, DownloadAiConfigFile
├── ClaudeCodeInstallProbe.cs        # Static probe: File.Exists at %LOCALAPPDATA%\Programs\claude\claude.exe; IsInstalled() bool
├── LogService.cs                    # Thread-safe debug.log writer — Write(), Error()
└── ActivityLogService.cs            # In-memory + JSONL activity log — Log(), GetRecent(), Flush()

src/Nexus.Sync/Services/
├── SyncEngine.cs                    # Main sync orchestrator — RunSync(), OnAuthFailed, event surface (IsRunning, LastSync, SyncStarted, SyncCompleted)
├── AiConfigApplyService.cs          # AI config apply orchestrator — ApplyAsync(manifest) with backup, download, verify, write, rename, rollback, finalize; persists fingerprint marker
├── ManifestFingerprint.cs           # Static helper — Compute(manifest) → sha256_hex of sorted "{path}|{sha}" pairs; trigger input for re-apply decision
├── StateManager.cs                  # Sync state persistence — HasChanged(), MarkUploaded(), MarkIgnored(), Save()
├── TranscriptScanner.cs             # Discovers .jsonl files in ~/.claude/projects/*/ — Scan()
├── TranscriptParser.cs              # Extracts metadata from first 30 lines — Parse() → TranscriptMetadata
└── SubagentMapper.cs                # Detects subagent Task invocations — MapSubagents() → List<SubagentInfo>
```

### NexusApiClient
- `Login(nexusUrl, email, password)` — POST `/api/v1/auth/login`, 30s timeout, rate-limit aware (429)
- `UploadTranscript(projectHashId?, sessionId, type, content, ...)` — POST `/api/v1/transcripts`, 300s timeout, nullable projectHashId for unassigned
- `RevokeToken()` — POST `/api/v1/auth/logout`, fire-and-forget
- `GetAiConfigManifest()` — GET `/api/v1/ai-config/manifest`, 30s timeout, 404 = "no snapshot", returns `ApiResult<AiConfigManifest>`
- `DownloadAiConfigFile(path, stream)` — GET `/api/v1/ai-config/file?path=...`, 300s timeout, streamed via `ResponseHeadersRead` + `CopyToAsync`

### SyncEngine
- Scans → parses → uploads transcripts and subagents each cycle
- Defers files modified within last 5 minutes (active session detection)
- Retry: 2 attempts per upload, 5s delay, re-reads file between attempts
- Max file size: 30MB (server validation limit)
- Stops on auth failure (401), continues on other errors
- Event surface: `IsRunning` (bool), `LastSync` (`SyncResult?`), `event Action? SyncStarted`, `event Action<SyncResult>? SyncCompleted` — subscribers must run on UI dispatcher
- Ordering invariant: `IsRunning = true` before `SyncStarted` fires; `LastSync` set and `IsRunning = false` before `SyncCompleted` fires

### TranscriptParser
- Reads `CLAUDE.md` from transcript's `cwd` to extract `NEXUS_PROJECT_HASH_ID`
- XAMPP fallback: tries `c:\xampp7\htdocs\` if `c:\xampp\htdocs\` path not found
- Filters out: `file-history-snapshot`, `queue-operation` entry types

### SubagentMapper
- Valid types: `dev`, `tech-lead`, `tester`, `test-creation-dev`, `code-reviewer`, `database-specialist`, `security-auditor`, `doc-writer`
- Skipped types: `Explore`, `Plan`, `claude-code-guide`, `Bash`, `general-purpose`, `statusline-setup`

---

## Windows / Views (5)

```
src/Nexus.App/
├── MainWindow.xaml                  # Tray-launched dashboard — singleton, hides on close; mirrors tray actions; subscribes to SyncEngine events; has btnCheckAiConfig button
├── LoginWindow.xaml                 # Nexus URL, email, password — emits LoginSucceeded event
├── SettingsWindow.xaml              # Sync interval config, launch-on-startup toggle, version display (bottom-left)
├── ActivityLogWindow.xaml           # Recent activity (200 entries), filter by type, timestamp/type/description/status columns
└── AiConfigUpdatePromptWindow.xaml  # Modal popup — shows new manifest version; "Close Claude Code first"; buttons: Apply Now / Later; Topmost, CenterScreen
```

## App Entry Point

```
src/Nexus.App/
├── App.xaml.cs                      # Single-instance (Mutex), wires services, manages tray + sync + update timers; TriggerAiConfigCheck() + login hook + 4h piggy-back; orphan .tmp cleanup on startup
├── TrayIconManager.cs               # System tray icon, context menu, sync timer, tooltip updates
└── StartupManager.cs                # Static helper — registers/unregisters app in HKCU\...\Run for Windows startup
```

### TrayIconManager Menu
- Open Nexus (when logged in) | Sync Now | Activity Log | Settings | Check for Updates | Logout | Exit

### Update & AI Config Checks
- On startup (if logged in) + after login + every 4 hours via `DispatcherTimer`
- Velopack `UpdateManager` fetches `{baseUrl}/api/v1/app/releases` → `releases.win.json`
- AI Config Sync triggers on: login (after Velopack) + manual "Check AI Config" button + 4h timer (piggy-backs Velopack timer); uses `TriggerAiConfigCheck()` shared entry point with popup-stacking guard
- **AI Config Sync is currently disabled** — `TriggerAiConfigCheck()` early-returns at the top, pending a server-side kill switch on Nexus web. All triggers (login, timer, MainWindow button) silently no-op. To re-enable: remove the early `return;` at the top of `TriggerAiConfigCheck()` in `App.xaml.cs`.

---

## Persistence Files

```
%AppData%\Nexus/
├── config.json                      # App configuration (DPAPI-encrypted token)
├── sync-state.json                  # File upload tracking (path → size/status)
├── ai-config-fingerprint            # Single-line text file: sha256 fingerprint of last-applied manifest files[]; machine-local (does NOT roam); drives re-apply trigger
├── ai-config-version                # Legacy: deleted by AiConfigApplyService after first successful apply post-1.3.0 upgrade
├── debug.log                        # Timestamped debug/error log
└── activity.log                     # JSONL activity entries (flushed on sync complete + exit)

%USERPROFILE%/.claude/
├── backups/{YYYY-MM-DD_HHmmss}/     # Pre-apply backups; cap-on-take retention ≤ 5; manual escape hatch for restore
└── [other config files]             # Agents, skills, conventions, hooks, settings.json — synced from server
```

---

## AI Config Sync

Keeps the dev's `~/.claude/` (agents, skills, conventions, hooks) in sync with the PM's server-side snapshot.

> **Status: disabled in 1.2.1.** `TriggerAiConfigCheck()` early-returns at the top — all flow below describes the implementation, which is dormant until a server-side kill switch ships on Nexus web. Re-enable by removing the early return in `App.xaml.cs`.

**Triggers:** Login + Manual "Check AI Config" button in MainWindow + every 4 hours (piggy-backs Velopack timer)

**Flow:** Probe Claude Code install → fetch manifest → compute `ManifestFingerprint.Compute(manifest)` → compare to stored `ai-config-fingerprint` → popup (if fingerprint differs) → backup → download all files to temp → SHA-256 verify → two-phase write (`.tmp` + atomic rename) → rollback-on-failure (from backup) → perfect-fit delete (non-manifest non-excluded items) → fingerprint marker persist (legacy version marker auto-deleted after first successful apply post-upgrade)

**Trigger rationale:** Fingerprint = `sha256_hex(sorted("{path}|{sha}" for each file in manifest.files))`. Catches both snapshot version bumps AND sysadmin selection changes that leave `manifest.version` unchanged — any change to `files[]` shape (added/removed/sha-changed entry) produces a different fingerprint. `manifest.version` is preserved for log/UI display only, not for the trigger decision.

**Key invariants:**
- `~/.claude/` is always fully on fingerprint F or fully on fingerprint F' (no half-applied states; rollback restores from pre-apply backup on rename failure)
- Fingerprint marker (`%LOCALAPPDATA%\Nexus\ai-config-fingerprint`) advances only on full success
- Exclusion list protects dev-local state: `projects/`, `settings.local.json`, credentials, caches, backups, etc.

**Backup retention:** Last 5 backups in `~/.claude/backups/{YYYY-MM-DD_HHmmss}/`; cap-on-take pruning deletes oldest before each new apply. Manual escape hatch: copy files from backup folder to restore if needed.

**Requires:** Claude Code installed at `%LOCALAPPDATA%\Programs\claude\claude.exe` (standard installer path); logs warning and skips silently if not detected.

---

## Key Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| Velopack | 0.0.1298 | Auto-update framework |
| Hardcodet.NotifyIcon.Wpf | 2.0.1 | System tray icon control |
| System.Security.Cryptography.ProtectedData | 8.0.0 | DPAPI token encryption |

## Key Integration
- **Depends on**: Nexus server API (`/api/v1/auth/login`, `/api/v1/transcripts`, `/api/v1/app/releases`, `/api/v1/ai-config/manifest`, `/api/v1/ai-config/file`)
- **Used by**: Nexus web dashboard (consumes uploaded transcripts)

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
├── AiConfigManifest.cs              # Version, GeneratedAt, ManagedRoots[], Files[] — manifest DTO from /api/v1/ai-config/manifest
└── AiConfigManifestFile.cs          # Path, Sha, Size — single file entry in manifest

src/Nexus.Sync/Models/
├── TranscriptFile.cs                # FilePath, ProjectSlug, FileSize — discovered .jsonl file
├── SubagentInfo.cs                  # AgentId, SubagentType, ToolUseId — detected subagent invocation
├── SyncState.cs                     # Files dict<string, FileState> — upload tracking; FileState: Size, Timestamp, Status
├── SyncResult.cs                    # Uploaded, Skipped, ParseSkipped, Errors, CompletedAt, Status — immutable result emitted by SyncEngine.SyncCompleted
├── AiConfigApplyResult.cs           # Success, Status (enum: applied, aborted, skipped_no_snapshot, skipped_no_install), Version?, Message?
└── AiConfigPaths.cs                 # Constants: ClaudeRoot, BackupsRoot, FingerprintPath; helpers: IsContainedIn(), ContainsReparsePoint()
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
├── INexusApiClient.cs               # Interface — GetAiConfigManifest, DownloadAiConfigFile; enables test seams
├── NexusApiClient.cs                # API client — Login, UploadTranscript, RevokeToken, GetAiConfigManifest, DownloadAiConfigFile; implements INexusApiClient
├── ClaudeCodeInstallProbe.cs        # Static probe — File.Exists at %LOCALAPPDATA%\Programs\claude\claude.exe for IsInstalled(); Process.GetProcessesByName() for IsRunning()
├── LogService.cs                    # Thread-safe debug.log writer — Write(), Error()
└── ActivityLogService.cs            # In-memory + JSONL activity log — Log(), GetRecent(), Flush()

src/Nexus.Sync/Services/
├── SyncEngine.cs                    # Main sync orchestrator — RunSync(), OnAuthFailed, event surface (IsRunning, LastSync, SyncStarted, SyncCompleted)
├── AiConfigApplyService.cs          # AI config apply orchestrator — ApplyAsync(manifest) with four-phase flow: pre-flight gates → wipe → write → finalize; persists fingerprint marker
├── ManifestFingerprint.cs           # Static helper — Compute(manifest) → sha256_hex of sorted "{path}|{sha}" pairs; trigger input for re-apply decision
├── StateManager.cs                  # Sync state persistence — HasChanged(), MarkUploaded(), MarkIgnored(), Save()
├── TranscriptScanner.cs             # Discovers .jsonl files in ~/.claude/projects/*/ — Scan()
├── TranscriptParser.cs              # Extracts metadata from first 30 lines — Parse() → TranscriptMetadata
└── SubagentMapper.cs                # Detects subagent Task invocations — MapSubagents() → List<SubagentInfo>
```

### NexusApiClient
- `Configure(baseUrl)` — validates `baseUrl` scheme (HTTPS required; `http://localhost` and `http://127.0.0.1` allowed for dev)
- `Login(nexusUrl, email, password)` — POST `/api/v1/auth/login`, 30s timeout, rate-limit aware (429)
- `UploadTranscript(projectHashId?, sessionId, type, content, ...)` — POST `/api/v1/transcripts`, 300s timeout, nullable projectHashId for unassigned
- `RevokeToken()` — POST `/api/v1/auth/logout`, fire-and-forget
- `GetAiConfigManifest()` — GET `/api/v1/ai-config/manifest`, 30s timeout, 404 = "no snapshot", returns `ApiResult<AiConfigManifest>`; catches `OperationCanceledException` (timeout) and `JsonException` (malformed response)
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

---

## Persistence Files

```
%AppData%\Nexus/
├── config.json                      # App configuration (DPAPI-encrypted token)
├── sync-state.json                  # File upload tracking (path → size/status)
├── ai-config-fingerprint            # Single-line text file: sha256 fingerprint of last-applied manifest files[]; machine-local (does NOT roam); drives re-apply trigger
├── debug.log                        # Timestamped debug/error log
└── activity.log                     # JSONL activity entries (flushed on sync complete + exit)

%USERPROFILE%/.claude/
└── [other config files]             # Agents, skills, conventions, hooks, settings.json — managed and synced from server
```

---

## AI Config Sync

Keeps the dev's `~/.claude/` (agents, skills, conventions, hooks) in sync with the PM's server-side snapshot via wipe-and-replace.

### Policy

**Files under server-managed roots are overwritten on every apply. Do not edit them — your changes will be lost. Place personal work under `~/.claude/projects/` or outside the managed roots.**

The server declares which directories it manages via the manifest's `managed_roots` field (e.g., `agents/`, `conventions/`, `skills/`). Any file under those roots will be deleted and rewritten from the server's snapshot. This is by design — managed roots are the server's responsibility, not yours. Personal work must live elsewhere (e.g., `~/.claude/projects/` is explicitly outside managed roots, or create a top-level `~/.claude/my-hooks/` for local-only files).

### Triggers

- **On login**: after Velopack update check completes
- **Manual button**: "Check AI Config" in MainWindow
- **4-hour timer**: piggy-backs the Velopack update timer; re-runs every 4 hours if the app stays open
- **Fingerprint-driven**: triggers only if `ManifestFingerprint.Compute(manifest)` differs from the stored `ai-config-fingerprint` marker; catches both version bumps and server-side selection changes

### Apply Pipeline (Four Phases)

**Phase 1 — Pre-flight gates** (abort if any fails; log reason):
- Is any symlink or junction under `~/.claude/managed_roots`? Refuse apply; reparse points are not compatible with wipe.
- Is `claude.exe` running? Refuse apply with message "Close Claude Code and retry."
- Does the manifest declare `managed_roots`? (missing field treated as empty array with warning logged; one-release fallback for server-side rollout safety)
- Validate each `managed_roots` entry: relative path, no `..`, no absolute, no `:`, no control chars, no embedded `\`, ≤ 260 chars.
- Validate each file path in `files[]`: must fall within at least one `managed_roots` entry; same character/traversal rules.

**Phase 2 — Wipe** (idempotent; skip if path doesn't exist):
- For each entry in `manifest.managed_roots`:
  - If it resolves to a directory: recursively delete all contents.
  - If it resolves to a file: delete it.

**Phase 3 — Write** (abort if any fails):
- For each file in `manifest.files`:
  - Download to a temp file (streamed via `ResponseHeadersRead`).
  - Verify SHA-256 (corruption detection; not a trust anchor).
  - Create parent directories and move temp file to final location.

**Phase 4 — Finalize**:
- Persist new fingerprint marker at `%LOCALAPPDATA%\Nexus\ai-config-fingerprint`.
- Log activity entry: applied manifest vN with M files across K managed roots.

### Failure Handling

No backups, no rollback, no atomicity guarantee beyond "either it worked or it didn't." If any phase throws:
- Exception is caught at the `TriggerAiConfigCheck` boundary (outer try/catch in `App.xaml.cs`).
- Failure is logged to activity log and debug.log.
- Dev clicks "Check AI Config" to retry — the next apply re-wipes and re-downloads from scratch (idempotent).

### Manifest Schema

```json
{
  "version": "string",
  "generated_at": "ISO-8601",
  "managed_roots": ["agents/", "conventions/", "skills/", "hooks/", "registries/", "commands/", "output-styles/"],
  "files": [
    { "path": "agents/dev/AGENT.md", "sha": "...", "size": 1234 },
    ...
  ]
}
```

`managed_roots` entries can be directories (relative to `~/.claude/`) or single-file entries (e.g., `"CLAUDE.md"` for a file at `~/.claude/CLAUDE.md`).

### Trust Model

The channel is **server-trust**: HTTPS + Sanctum bearer token. A compromised server (or leaked admin token) can write arbitrary content to any path under `managed_roots`, including hooks (which execute) and agent prompts (which steer Claude Code). This is a known gap for production use. **Planned closure**: offline signing of the manifest with Ed25519; see `project_docs/improvements.md` "AI Config Manifest Signature" for details. Current status: trusted ops only, not production-grade.

### Requirements

Claude Code must be installed at `%LOCALAPPDATA%\Programs\claude\claude.exe` (standard installer path). If not detected, apply is deferred with "close Claude Code and retry" message. The probe runs once per trigger (no caching across checks).

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

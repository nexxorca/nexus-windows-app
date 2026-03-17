# Nexus Windows App - Structure

> Last updated: 2026-03-17 | Project: `nexus-windows-app`

## Purpose
Native WPF tray application that synchronizes Claude Code session transcripts from `~/.claude/projects/` to the Nexus server via API, with Velopack auto-update support.

---

## Solution Structure (3 projects)

```
src/
├── Nexus.App/                       # WPF executable — tray icon, windows, update checks
├── Nexus.Core/                      # Shared library — API client, config, logging
└── Nexus.Sync/                      # Sync engine — scanning, parsing, upload orchestration
```

- **Framework**: .NET 8.0 (`net8.0-windows`), self-contained win-x64
- **Shutdown**: `OnExplicitShutdown` — app lives in tray, no main window

---

## Models (6)

```
src/Nexus.Core/Models/
├── AppConfig.cs                     # NexusUrl, AuthToken (DPAPI encrypted), UserHashId, UserName, SyncIntervalSeconds, LaunchOnStartup
└── ApiResult.cs                     # Success, StatusCode, Message, ErrorDetail; IsAuthError (401), IsValidationError (422)

src/Nexus.Sync/Models/
├── TranscriptFile.cs                # FilePath, ProjectSlug, FileSize — discovered .jsonl file
├── SubagentInfo.cs                  # AgentId, SubagentType, ToolUseId — detected subagent invocation
└── SyncState.cs                     # Files dict<string, FileState> — upload tracking; FileState: Size, Timestamp, Status
```

### AppConfig Details
- Storage: `%AppData%\Nexus\config.json`
- Token encryption: DPAPI (`DataProtectionScope.CurrentUser`), stored as Base64
- Methods: `Load()`, `Save()`, `SetLoginData()`, `ClearLoginData()`, `DecryptedToken`
- Legacy import: reads `.claude/nexus.env` on first load

---

## Services (8)

```
src/Nexus.Core/Services/
├── NexusApiClient.cs                # API client — Login, UploadTranscript, RevokeToken
├── LogService.cs                    # Thread-safe debug.log writer — Write(), Error()
└── ActivityLogService.cs            # In-memory + JSONL activity log — Log(), GetRecent(), Flush()

src/Nexus.Sync/Services/
├── SyncEngine.cs                    # Main sync orchestrator — RunSync(), LastSyncHadErrors, OnAuthFailed
├── StateManager.cs                  # Sync state persistence — HasChanged(), MarkUploaded(), MarkIgnored(), Save()
├── TranscriptScanner.cs             # Discovers .jsonl files in ~/.claude/projects/*/ — Scan()
├── TranscriptParser.cs              # Extracts metadata from first 30 lines — Parse() → TranscriptMetadata
└── SubagentMapper.cs                # Detects subagent Task invocations — MapSubagents() → List<SubagentInfo>
```

### NexusApiClient
- `Login(nexusUrl, email, password)` — POST `/api/v1/auth/login`, 30s timeout, rate-limit aware (429)
- `UploadTranscript(projectHashId?, sessionId, type, content, ...)` — POST `/api/v1/transcripts`, 300s timeout, nullable projectHashId for unassigned
- `RevokeToken()` — POST `/api/v1/auth/logout`, fire-and-forget

### SyncEngine
- Scans → parses → uploads transcripts and subagents each cycle
- Defers files modified within last 5 minutes (active session detection)
- Retry: 2 attempts per upload, 5s delay, re-reads file between attempts
- Max file size: 30MB (server validation limit)
- Stops on auth failure (401), continues on other errors

### TranscriptParser
- Reads `CLAUDE.md` from transcript's `cwd` to extract `NEXUS_PROJECT_HASH_ID`
- XAMPP fallback: tries `c:\xampp7\htdocs\` if `c:\xampp\htdocs\` path not found
- Filters out: `file-history-snapshot`, `queue-operation` entry types

### SubagentMapper
- Valid types: `dev`, `tech-lead`, `tester`, `test-creation-dev`, `code-reviewer`, `database-specialist`, `security-auditor`, `doc-writer`
- Skipped types: `Explore`, `Plan`, `claude-code-guide`, `Bash`, `general-purpose`, `statusline-setup`

---

## Windows / Views (3)

```
src/Nexus.App/
├── LoginWindow.xaml                 # Nexus URL, email, password — emits LoginSucceeded event
├── SettingsWindow.xaml              # Sync interval config, launch-on-startup toggle, version display (bottom-left)
└── ActivityLogWindow.xaml           # Recent activity (200 entries), filter by type, timestamp/type/description/status columns
```

## App Entry Point

```
src/Nexus.App/
├── App.xaml.cs                      # Single-instance (Mutex), wires services, manages tray + sync + update timers
├── TrayIconManager.cs               # System tray icon, context menu, sync timer, tooltip updates
└── StartupManager.cs                # Static helper — registers/unregisters app in HKCU\...\Run for Windows startup
```

### TrayIconManager Menu
- Sync Now | Activity Log | Settings | Check for Updates | Logout | Exit

### Update Checks
- On startup (if logged in) + after login + every 4 hours via `DispatcherTimer`
- Velopack `UpdateManager` fetches `{baseUrl}/api/v1/app/releases` → `releases.win.json`

---

## Persistence Files

```
%AppData%\Nexus/
├── config.json                      # App configuration (DPAPI-encrypted token)
├── sync-state.json                  # File upload tracking (path → size/status)
├── debug.log                        # Timestamped debug/error log
└── activity.log                     # JSONL activity entries (flushed on sync complete + exit)
```

---

## Key Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| Velopack | 0.0.1298 | Auto-update framework |
| Hardcodet.NotifyIcon.Wpf | 2.0.1 | System tray icon control |
| System.Security.Cryptography.ProtectedData | 8.0.0 | DPAPI token encryption |

## Key Integration
- **Depends on**: Nexus server API (`/api/v1/auth/login`, `/api/v1/transcripts`, `/api/v1/app/releases`)
- **Used by**: Nexus web dashboard (consumes uploaded transcripts)

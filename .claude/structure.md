# Nexus Windows App - Structure

> Last updated: 2026-02-21 | Solution: `Nexus.sln`

## Purpose
A C# WPF system tray application (.NET 8) that syncs Claude Code transcripts from local JSONL files to the Nexus web portal API. Runs as a background service with periodic scanning and uploading of agent session transcripts.

---

## Solution Structure (3 Projects)

```
src/
├── Nexus.App/                    # WPF tray application (Windows desktop UI)
├── Nexus.Core/                   # Shared core services and models
└── Nexus.Sync/                   # Transcript scanning and sync engine
```

### Project Dependencies
- **Nexus.App** references: Nexus.Core, Nexus.Sync
- **Nexus.Sync** references: Nexus.Core
- **Nexus.Core** has no internal dependencies

---

## Nexus.Core - Shared Models & Services

### Models (2)

```
src/Nexus.Core/Models/
├── AppConfig.cs                  # Application configuration (URL, auth token, sync interval, user info)
└── ApiResult.cs                  # HTTP response wrapper (success, status, message, error detail)
```

#### AppConfig
- Properties: `NexusUrl`, `AuthToken`, `UserHashId`, `UserName`, `SyncIntervalSeconds`
- Computed: `IsLoggedIn`, `IsValid`
- Methods: `Load()`, `Save()`, `SetLoginData()`, `ClearLoginData()`, `ImportFromLegacyEnv()`
- Persists to: `%AppData%\Nexus\config.json`
- Supports legacy import from `~\.claude\nexus.env`

#### ApiResult
- Properties: `Success`, `StatusCode`, `Message`, `ErrorDetail`
- Computed: `IsAuthError` (401), `IsValidationError` (422), `IsServerError` (500+)
- Factory methods: `Ok()`, `Fail(statusCode, message, detail)`

### Services (2)

```
src/Nexus.Core/Services/
├── LogService.cs                 # Thread-safe debug logging (30-second timestamps)
└── ActivityLogService.cs         # In-memory activity event log (max 500 entries) with JSON persistence
```

#### LogService
- Single output file: `%AppData%\Nexus\debug.log`
- Methods: `Write()`, `Error(message, exception)`
- Thread-safe with lock

#### ActivityLogService
- In-memory list (max 500 entries) of `ActivityLogEntry` objects
- Properties: `Type`, `Description`, `Status`, `Timestamp`
- Methods: `Log(type, description, status)`, `GetRecent(count, typeFilter)`, `Flush()`
- Persists to: `%AppData%\Nexus\activity.log` (JSON lines format)
- Common types: `sync_start`, `sync_complete`, `file_upload`, `api_error`, `login`, `logout`, `app_start`, `app_stop`

### HTTP Client (1)

```
src/Nexus.Core/Services/
└── NexusApiClient.cs             # HTTP client for Nexus API v1 endpoints
```

#### NexusApiClient
- Constructor: `NexusApiClient(LogService)`
- Configuration: `Configure(baseUrl, authToken)` — sets Bearer token
- Endpoints:
  - `Login(nexusUrl, email, password)` → POST `/api/v1/auth/login` → returns `token` and `user.hash_id`, `user.first_name`
  - `UploadTranscript(projectHashId, sessionId, type, content, subagentType, parentTranscriptId)` → POST `/api/v1/transcripts`
- Timeout: 30 seconds per request
- Returns: `ApiResult` for all operations

---

## Nexus.Sync - Transcript Scanning & Sync Engine

### Models (3)

```
src/Nexus.Sync/Models/
├── TranscriptFile.cs             # File metadata (path, project slug, size)
├── SubagentInfo.cs               # Subagent mapping (agent ID, subagent type, tool use ID)
└── SyncState.cs                  # Persisted upload state (files dict → file states)
```

#### TranscriptFile
- Properties: `FilePath`, `ProjectSlug`, `FileSize`

#### SubagentInfo
- Properties: `AgentId`, `SubagentType`, `ToolUseId`

#### SyncState
- Root: `Dictionary<FilePath, FileState>`
- FileState: `Size`, `Timestamp` (ISO 8601), `Status` ("uploaded" or "ignored")

### Services (5)

```
src/Nexus.Sync/Services/
├── StateManager.cs               # Persists and queries upload state
├── TranscriptScanner.cs          # Scans ~/.claude/projects/ for JSONL files
├── TranscriptParser.cs           # Parses JSONL metadata (session ID, project hash)
├── SubagentMapper.cs             # Extracts subagent task calls from transcript content
└── SyncEngine.cs                 # Orchestrates entire sync pipeline
```

#### StateManager
- Persists to: `%AppData%\Nexus\sync-state.json`
- Methods: `HasChanged(filePath, size)`, `MarkUploaded()`, `MarkIgnored()`, `Save()`
- Returns: true if file size differs from last recorded state

#### TranscriptScanner
- Scans: `~\.claude\projects\` (recursive directory walk)
- File pattern: `*.jsonl` (excludes files starting with `acompact-`)
- Returns: `List<TranscriptFile>` with full paths and sizes

#### TranscriptParser
- Input: single JSONL file path
- Output: `TranscriptMetadata?` (nullable)
- Reads first 30 lines only
- Extracts: `sessionId`, `cwd`, `ProjectHashId`
- Skips: `file-history-snapshot` and `queue-operation` entry types
- Looks for real entries: `user` or `assistant` messages
- Resolves project hash: reads CLAUDE.md in project directory and regex-matches `NEXUS_PROJECT_HASH_ID=<hash>`
- Fallback: if cwd doesn't exist on current PHP version, tries alternate XAMPP path (`c:\xampp` ↔ `c:\xampp7`)
- Skip reasons: non-transcript files, missing CLAUDE.md, no hash ID, directory not found

#### SubagentMapper
- Input: full file content string
- Output: `List<SubagentInfo>` (filtered and validated)
- Parses JSONL looking for Task tool use blocks in assistant messages
- Extracts: `subagent_type` from tool input, matches to `tool_use_id`
- In user tool_result blocks: extracts `agentId` from result content (regex: `agentId:\s*([a-f0-9]+)`)
- Valid subagent types: `dev`, `tech-lead`, `tester`, `test-creation-dev`, `code-reviewer`, `database-specialist`, `security-auditor`, `doc-writer`
- Skip types: `Explore`, `Plan`, `claude-code-guide`, `Bash`, `general-purpose`, `statusline-setup`

#### SyncEngine
- Orchestrates full sync: scan → parse → upload → state persistence
- Constructor: wires all dependencies (config, API, state, scanner, parser, mapper, logs)
- Main method: `RunSync()` (async) — returns void, logs activity
- Process:
  1. Scan for JSONL files
  2. For each file: check if size changed (using StateManager)
  3. Parse metadata (session ID, project hash)
  4. Skip if: non-transcript, no project hash, parse failed
  5. Read file content and POST to `/api/v1/transcripts` as "main" type
  6. If successful: extract subagents and upload each separately as "subagent" type
  7. On 401 auth error: stop immediately and log
  8. Mark uploaded files in state and persist
- Counters: uploadCount, skipCount, parseSkipCount, errorCount
- Property: `LastSyncHadErrors` — tracks if last cycle had errors (for tray icon status)

---

## Nexus.App - WPF Tray Application

### Windows (3)

```
src/Nexus.App/Views/
├── LoginWindow.xaml.cs           # Authentication UI (URL, email, password)
├── ActivityLogWindow.xaml.cs     # Real-time activity viewer (filterable)
└── SettingsWindow.xaml.cs        # Sync interval configuration
```

#### LoginWindow
- Inputs: Nexus URL, email, password
- Pre-fills URL if previously configured
- Calls: `NexusApiClient.Login()` → parses response for token, user hash, first name
- On success: saves config and triggers `LoginSucceeded` event
- Error display: shows inline error message with retry

#### ActivityLogWindow
- Displays: recent activity log entries (up to 200)
- Filters: "All", "Sync", "Uploads", "Errors"
- Methods: `LoadEvents()`, `GetSelectedFilter()`
- Controls: ComboBox filter, ListBox display, Refresh/Close buttons

#### SettingsWindow
- Input: sync interval (seconds)
- Loads current interval from AppConfig
- On save: updates config and logs configuration change event

### Managers (1)

```
src/Nexus.App/
└── TrayIconManager.cs            # System tray icon, context menu, sync timer
```

#### TrayIconManager
- Uses: `Hardcodet.NotifyIcon.Wpf` (NuGet) for taskbar integration
- Icon: `Resources\nexus.ico`
- Timer: periodic sync (interval from AppConfig)
- Context menu:
  - Header (shows username if logged in, disabled)
  - Sync Now (manual trigger)
  - Activity Log (window)
  - Settings (window)
  - Logout (if logged in)
  - Exit
- Tooltip updates: shows login status, last sync time (human-readable: "2m ago", "1h ago"), error status
- Methods: `StartSync()`, `StopSync()`, `UpdateUserName()`, `Dispose()`
- Internal: `RunSync()` (async), `UpdateTooltip()`, `FormatTimeAgo()`, `BuildContextMenu()`, `ShowActivityLog()`, `ShowSettings()`

### App Lifecycle (1)

```
src/Nexus.App/
└── App.xaml.cs                   # WPF application bootstrap & DI
```

#### App
- Startup: single-instance mutex check (`NexusDesktopApp`)
- On startup:
  1. Load AppConfig from file or legacy env
  2. Wire all services (LogService, ActivityLogService, NexusApiClient)
  3. Construct SyncEngine with all dependencies
  4. Initialize TrayIconManager
  5. If logged in: configure API and start sync
  6. If not logged in: show login window
- Event handlers: `LoginSucceeded` → configure API and start sync
- Methods: `ShowLoginWindow()`, `Logout()` (user-initiated)
- On exit: log stop event, flush activity, dispose tray manager and mutex

### Project Configuration

```
Nexus.App.csproj
```
- Framework: `net8.0-windows`
- Type: `WinExe` (no console)
- WPF enabled: `<UseWPF>true</UseWPF>`
- Icon: `Resources\nexus.ico`
- NuGet: `Hardcodet.NotifyIcon.Wpf` (v2.0.1)
- References: Nexus.Core, Nexus.Sync
- Features: nullable enabled, implicit usings

---

## NuGet Dependencies

| Package | Version | Project | Purpose |
|---------|---------|---------|---------|
| Hardcodet.NotifyIcon.Wpf | 2.0.1 | Nexus.App | System tray icon integration for WPF |

---

## File Paths (Runtime)

| Path | Purpose |
|------|---------|
| `%AppData%\Nexus\config.json` | User configuration (URL, token, user info, sync interval) |
| `%AppData%\Nexus\debug.log` | Debug log (all operations with timestamps) |
| `%AppData%\Nexus\activity.log` | Activity log (JSON lines, flushed on sync complete) |
| `%AppData%\Nexus\sync-state.json` | Uploaded file state (size, timestamp, status) |
| `~\.claude\projects\` | Source root (scanned for JSONL transcripts) |
| `~\.claude\projects\{project-slug}\*.jsonl` | Transcript files (main and subagent) |
| `~\.claude\projects\{project-slug}\{agentId}.jsonl` | Subagent transcript files (named by agent hash) |
| `~\{project-cwd}\CLAUDE.md` | Project hash extraction (regex: `NEXUS_PROJECT_HASH_ID=<hash>`) |

---

## Key Integration

### API Endpoints Consumed
- `POST /api/v1/auth/login` — User authentication (returns token, user hash, first name)
- `POST /api/v1/transcripts` — Upload main and subagent transcripts (project hash, session ID, type, content, optional subagent type and parent session ID)

### External Services
- **Nexus Web Portal** — Authentication and transcript storage API
- **Windows Task Scheduler** — Can be configured to auto-start tray app at login

### Configuration Sources
- CLAUDE.md files in project directories (NEXUS_PROJECT_HASH_ID extraction)
- Legacy environment file at `~\.claude\nexus.env` (NEXUS_URL import on first run)

### Subagent Architecture
- Main transcript uploaded as `type: main`
- Subagents extracted via Task tool use pattern in transcript
- Subagent files identified by agent hash (matches result content)
- Uploaded as `type: subagent` with `subagent_type` and `parent_transcript_id` (main session ID)

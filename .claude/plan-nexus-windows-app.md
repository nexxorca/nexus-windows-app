# Nexus Windows App (Nexus Desktop) — Implementation Plan

## Context

The current transcript sync system runs as a Node.js script (`sync.js`) triggered every 60 seconds by Windows Task Scheduler. While functional, it has several limitations:

- **Requires Node.js runtime** on every developer machine
- **No UI** — developers can't see sync status, trigger manual syncs, or troubleshoot issues
- **Task Scheduler friction** — hard to set up on new machines, no feedback if it breaks
- **Manual user ID config** — `NEXUS_USER_ID` must be set manually in `nexus.env`
- **File size deduplication only** — could miss edge cases where content changes but size doesn't
- **No activity visibility** — no way to see what the app is doing or has done

**Solution**: A standalone C# Windows application (system tray app) that replaces sync.js with a native, zero-dependency `.exe`.

**Phased approach**:
- **Phase A** (now): Replace sync.js — make it work, keep it simple
- **Phase B** (later): Make it solid — incremental uploads, better UX, tests, installer
- **Phase C** (maybe): Harden — security hardening, DI/interfaces, accessibility, only if it becomes client-facing

---

## Architecture / Approach

### Technology Stack

- **.NET 8** (LTS) — self-contained single-file publish, no runtime required on target machines
- **WPF** — UI framework for tray icon, settings window, activity log viewer
- **Hardcodet.NotifyIcon.Wpf** — WPF-native system tray icon support
- **System.Text.Json** — JSONL parsing (built-in, fast)
- **HttpClient** — API communication
- **DispatcherTimer** — periodic sync (same 60s approach as sync.js)

### Solution Structure

```
C:\xampp\htdocs\nexus-windows-app\
├── .gitignore
├── Nexus.sln
├── src/
│   ├── Nexus.Core/                    # Shared: config, API client, logging
│   │   ├── Models/
│   │   │   ├── AppConfig.cs           # Configuration model + loader
│   │   │   └── ApiResult.cs           # Structured API response model
│   │   ├── Services/
│   │   │   ├── NexusApiClient.cs      # HTTP client for Nexus API
│   │   │   ├── LogService.cs          # Technical debug logging
│   │   │   └── ActivityLogService.cs  # Structured activity log for UI
│   │   └── Nexus.Core.csproj
│   ├── Nexus.Sync/                    # Module: transcript sync engine
│   │   ├── Models/
│   │   │   ├── TranscriptFile.cs      # Represents a discovered transcript
│   │   │   ├── SubagentInfo.cs        # agentId + type mapping
│   │   │   └── SyncState.cs           # Serializable state model
│   │   ├── Services/
│   │   │   ├── TranscriptScanner.cs   # Finds .jsonl files (skips acompact-*)
│   │   │   ├── TranscriptParser.cs    # Extracts sessionId, cwd, project hash
│   │   │   ├── SubagentMapper.cs      # Maps agentId → subagent_type
│   │   │   ├── SyncEngine.cs          # Orchestrates sync cycle
│   │   │   └── StateManager.cs        # Tracks uploaded files (file-size dedup)
│   │   └── Nexus.Sync.csproj
│   └── Nexus.App/                     # WPF tray application
│       ├── App.xaml / App.xaml.cs     # Startup, mutex
│       ├── TrayIconManager.cs         # TaskbarIcon, context menu, timer
│       ├── Views/
│       │   ├── SettingsWindow.xaml     # Config UI
│       │   └── ActivityLogWindow.xaml  # Activity log viewer
│       ├── Resources/
│       │   └── nexus.ico              # Tray icon
│       └── Nexus.App.csproj
└── tests/                             # Phase B
```

### Modular Design

- **Nexus.Core** — shared infrastructure (config, API client, logging, activity log)
- **Nexus.Sync** — transcript sync engine (first module)
- **Nexus.App** — WPF shell that hosts tray icon and wires everything together

### Authentication Model

The app authenticates with the Nexus API using **user login** (email + password). On first launch, the user logs in via a LoginWindow. The Nexus API validates credentials and returns a **Laravel Sanctum personal access token** + user info (hash_id, name). This token is stored locally and sent as `Authorization: Bearer {token}` on all API calls.

The server identifies the user directly from the token via `$request->user()` — no separate user identification fields needed in the payload.

**Note**: Other Nexus API routes (sessions, instructions, activities) still use the existing `AuthenticateAgent` middleware. Only the transcript endpoint switches to Sanctum user auth.

### Configuration Storage

Settings stored in `%APPDATA%\Nexus\config.json`:

```json
{
  "nexusUrl": "https://nexus.nexxor.ca",
  "authToken": "1|abc123def456...",
  "userHashId": "a1b2c3d4e5",
  "userName": "Erik",
  "syncIntervalSeconds": 60,
  "lookbackMinutes": 10
}
```

- `authToken` + `userHashId` + `userName` — set on login, cleared on logout
- `nexusUrl` + sync settings — persist across logout/login

State file at `%APPDATA%\Nexus\sync-state.json` — file-size based dedup (same approach as sync.js).

Activity log at `%APPDATA%\Nexus\activity.log` — structured JSON events.

Debug log at `%APPDATA%\Nexus\debug.log` — technical logging.

---

## Phase A: Replace sync.js

**Goal**: Working native .exe that does exactly what sync.js does, plus a tray icon, settings UI, activity log, and Windows username detection. No more, no less.

**Design decisions for Phase A**:
- **User login** — email/password against Nexus API via Laravel Sanctum. No agent API tokens.
- **Timer-only sync** — DispatcherTimer every N seconds, same as sync.js's Task Scheduler. No FileSystemWatcher yet.
- **File-size dedup** — same approach as sync.js. Upload full file content, skip if size unchanged.
- **No DI container** — concrete classes with constructor injection. Simple `new` in App.xaml.cs.
- **No interfaces** — concrete types only. Refactor to interfaces in Phase B when tests are added.
- **No incremental uploads** — full file upload each time (server handles re-processing via `transcript_lines_processed`).
- **File locking** — use `FileShare.ReadWrite` when reading .jsonl files (Claude may be writing simultaneously).
- **Concurrent sync guard** — stop timer during sync, restart after. Prevents overlapping cycles.

### Steps

#### A.1 — Project Setup

| Step | Description | Agent | Files |
|------|-------------|-------|-------|
| A.1.1 | Initialize git repo, .gitignore, connect remote | dev | `.gitignore` |
| A.1.2 | Create solution and project structure (Core, Sync, App) | dev | `Nexus.sln`, `.csproj` files |
| A.1.3 | Remove existing bare WPF scaffold at `Nexus/` | dev | — |

#### A.2 — Core Services

| Step | Description | Agent | Files |
|------|-------------|-------|-------|
| A.2.1 | Build AppConfig — load/save `config.json`, legacy `nexus.env` import | dev | `Nexus.Core/Models/AppConfig.cs` |
| A.2.2 | Build ApiResult model | dev | `Nexus.Core/Models/ApiResult.cs` |
| A.2.3 | Build NexusApiClient — Bearer auth (Sanctum token), upload, login, structured error handling | dev | `Nexus.Core/Services/NexusApiClient.cs` |
| A.2.4 | Build LogService — debug logging to file | dev | `Nexus.Core/Services/LogService.cs` |
| A.2.5 | Build ActivityLogService — structured event log (in-memory + file) | dev | `Nexus.Core/Services/ActivityLogService.cs` |

#### A.3 — Sync Engine

| Step | Description | Agent | Files |
|------|-------------|-------|-------|
| A.3.1 | Build SyncState model and StateManager (file-size dedup) | dev | `Nexus.Sync/Models/SyncState.cs`, `Nexus.Sync/Services/StateManager.cs` |
| A.3.2 | Build TranscriptFile model | dev | `Nexus.Sync/Models/TranscriptFile.cs` |
| A.3.3 | Build TranscriptScanner — find .jsonl, skip acompact-* | dev | `Nexus.Sync/Services/TranscriptScanner.cs` |
| A.3.4 | Build TranscriptParser — sessionId, cwd, project hash | dev | `Nexus.Sync/Services/TranscriptParser.cs` |
| A.3.5 | Build SubagentMapper — agentId → subagent_type | dev | `Nexus.Sync/Models/SubagentInfo.cs`, `Nexus.Sync/Services/SubagentMapper.cs` |
| A.3.6 | Build SyncEngine — orchestrate full sync cycle | dev | `Nexus.Sync/Services/SyncEngine.cs` |

#### A.4 — WPF Tray App + UI

| Step | Description | Agent | Files |
|------|-------------|-------|-------|
| A.4.1 | Create WPF app with mutex (single instance) | dev | `Nexus.App/App.xaml`, `Nexus.App/App.xaml.cs` |
| A.4.2 | Build TrayIconManager — icon, context menu, timer, status | dev | `Nexus.App/TrayIconManager.cs` |
| A.4.3 | Tray icon states: idle (normal) / error (red) | dev | `Nexus.App/TrayIconManager.cs` |
| A.4.4 | Tray tooltip — status + last sync time + logged-in user | dev | `Nexus.App/TrayIconManager.cs` |
| A.4.5 | Create tray icon resource | manual | `Nexus.App/Resources/nexus.ico` |
| A.4.6 | Build LoginWindow — email, password, Nexus URL, login button | dev | `Nexus.App/Views/LoginWindow.xaml` |
| A.4.7 | Build SettingsWindow — sync interval, lookback, Save/Cancel | dev | `Nexus.App/Views/SettingsWindow.xaml` |
| A.4.8 | Build ActivityLogWindow — event list with type filter | dev | `Nexus.App/Views/ActivityLogWindow.xaml` |

#### A.5 — Server-Side Changes

All changes in the **Nexus Laravel project** at `c:\xampp\htdocs\nexus\`.

| Step | Description | Agent | Files |
|------|-------------|-------|-------|
| A.5.1 | Install Laravel Sanctum, publish config/migration, run migrate | dev | `config/sanctum.php`, migration |
| A.5.2 | Create login API endpoint (`POST /api/v1/auth/login`) | dev | `AuthController.php`, `routes/api.php` |
| A.5.3 | Change transcript route middleware from `AuthenticateAgent` to `auth:sanctum` | dev | `routes/api.php` |
| A.5.4 | Update StoreTranscriptRequest — remove `user_id`, bump content to 30MB | dev | `StoreTranscriptRequest.php` |
| A.5.5 | Update TranscriptController — use `$request->user()->id` for user identification | dev | `TranscriptController.php` |
| A.5.6 | Migration: widen `transcript_id` to VARCHAR(100) | database-specialist | migration file |

#### A.6 — Rollout

| Step | Description | Agent |
|------|-------------|-------|
| A.6.1 | Build single-file .exe (`dotnet publish`) | dev |
| A.6.2 | Test with live transcripts on dev machine | tester |
| A.6.3 | Disable sync.js Task Scheduler | manual |
| A.6.4 | Copy .exe to developer machines, configure | manual |
| A.6.5 | Remove sync.js and nexus-watcher directory | manual |

---

### Phase A — Detailed Steps

#### A.2.1 AppConfig

```csharp
public class AppConfig {
    public string NexusUrl { get; set; } = "";
    public string? AuthToken { get; set; }
    public string? UserHashId { get; set; }
    public string? UserName { get; set; }
    public int SyncIntervalSeconds { get; set; } = 60;
    public int LookbackMinutes { get; set; } = 10;

    public bool IsLoggedIn => ! string.IsNullOrEmpty(AuthToken);
    public bool IsValid => ! string.IsNullOrEmpty(NexusUrl) && IsLoggedIn;

    public static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Nexus"
    );

    public static AppConfig Load() { ... }
    public void Save() { ... }

    public void SetLoginData( string authToken, string userHashId, string userName ) {
        AuthToken = authToken;
        UserHashId = userHashId;
        UserName = userName;
        Save();
    }

    public void ClearLoginData() {
        AuthToken = null;
        UserHashId = null;
        UserName = null;
        Save();
    }
}
```

#### A.2.2 ApiResult

```csharp
public class ApiResult {
    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public string? Message { get; set; }
    public string? ErrorDetail { get; set; }

    public bool IsAuthError => StatusCode == 401;
    public bool IsValidationError => StatusCode == 422;
    public bool IsServerError => StatusCode >= 500;
}
```

#### A.2.3 NexusApiClient

- Singleton `HttpClient` (created once in constructor, reused)
- Bearer token from `AppConfig.AuthToken` (Sanctum token)
- 30 second timeout
- Returns `ApiResult` (non-throwing)
- Catches `TaskCanceledException` (timeout) and `HttpRequestException` (unreachable)
- `Login()` method for authentication (called from LoginWindow)

```csharp
public class NexusApiClient {
    private readonly HttpClient _http;
    private readonly LogService _log;

    public void SetAuthToken( string token ) { ... }

    public async Task<ApiResult> Login(
        string nexusUrl, string email, string password
    ) { ... }
    // Returns ApiResult with user info (hash_id, name) in Message on success

    public async Task<ApiResult> UploadTranscript(
        string projectHashId, string sessionId, string type,
        string content, string? subagentType = null,
        string? parentTranscriptId = null
    ) { ... }
}
```

#### A.2.5 ActivityLogService

In-memory list + periodic file flush. Simple for Phase A.

- Event types: `app_start`, `app_stop`, `login`, `logout`, `sync_start`, `sync_complete`, `file_upload`, `file_skip`, `subagent_found`, `api_error`, `config_change`
- Keeps last 500 events in memory
- Writes to `activity.log` on flush (called after each sync cycle)
- `GetRecent()` returns events for UI

#### A.3.1 StateManager (File-Size Dedup)

Same approach as sync.js — track file size per absolute path.

```csharp
public class SyncState {
    public Dictionary<string, FileState> Files { get; set; } = new();
}

public class FileState {
    public long Size { get; set; }
    public string Timestamp { get; set; } = "";
}
```

- `HasChanged(filePath)` — compares current file size to stored size
- `MarkUploaded(filePath, size)` — updates state entry
- Load/save from `sync-state.json`

#### A.3.3 TranscriptScanner

- Scans `~/.claude/projects/` for `.jsonl` files in project slug directories
- Filters by modification time (last N minutes)
- **Skips** files starting with `acompact-`
- Returns `List<TranscriptFile>` with path and project slug

#### A.3.4 TranscriptParser

- Reads first ~30 lines (using `FileShare.ReadWrite` to avoid locking)
- Extracts `sessionId` and `cwd` from JSONL entries
- Reads `{cwd}/CLAUDE.md` for `NEXUS_PROJECT_HASH_ID=([a-f0-9]+)`
- Returns null if metadata extraction fails (file skipped)

#### A.3.5 SubagentMapper

Two-pass algorithm (same as sync.js):

**Pass 1**: `assistant` entries with `tool_use` blocks where `name === 'Task'` → map `block.id` → `block.input.subagent_type`

**Pass 2**: `user` entries with `tool_result` blocks → extract `agentId` from content string → cross-reference with Pass 1

**Valid agent codes**: `dev`, `tech-lead`, `tester`, `test-creation-dev`, `code-reviewer`, `database-specialist`, `security-auditor`, `doc-writer`

**Skip types**: `Explore`, `Plan`, `claude-code-guide`, `Bash`, `general-purpose`, `statusline-setup`

#### A.3.6 SyncEngine

Replicates sync.js flow:

1. Load state
2. Scan for recently modified .jsonl files
3. For each main transcript:
   a. Parse metadata (sessionId, cwd, projectHashId)
   b. Skip if metadata missing
   c. Check state — skip if file size unchanged
   d. Read file content (with `FileShare.ReadWrite`)
   e. Upload via NexusApiClient
   f. On auth error → log, stop cycle
   g. On other error → log, continue to next file
   h. On success → update state, log activity
   i. Build subagent map from content
   j. Find subagent files, upload each (same dedup + error handling)
4. Save state
5. Log activity (sync_complete with counts)

**API payload — main transcript**:
```json
{
  "project_hash_id": "3cd3645f...",
  "session_id": "07474c40-ccf0-4f3f-...",
  "type": "main",
  "content": "[FULL FILE CONTENT]"
}
```

**API payload — subagent**:
```json
{
  "project_hash_id": "3cd3645f...",
  "session_id": "07474c40-ccf0-4f3f-...-a91d633",
  "type": "subagent",
  "subagent_type": "dev",
  "parent_transcript_id": "07474c40-ccf0-4f3f-...",
  "content": "[FULL FILE CONTENT]"
}
```

Note: API field `session_id` maps to DB column `agent_sessions.transcript_id`. User identification comes from the Sanctum Bearer token (`$request->user()`), not the payload.

#### A.4.1 WPF App with Mutex

```csharp
private Mutex? _mutex;

protected override void OnStartup( StartupEventArgs e ) {
    _mutex = new Mutex(true, "NexusDesktopApp", out bool isNew);
    if ( ! isNew ) {
        MessageBox.Show("Nexus Desktop is already running.");
        Shutdown();
        return;
    }

    base.OnStartup(e);

    var config = AppConfig.Load();
    // Wire services (simple new, no DI)
    // Init TrayIconManager

    if ( ! config.IsLoggedIn ) {
        // Show LoginWindow — sync won't start until logged in
        ShowLoginWindow();
    } else {
        // Start sync timer
        StartSync();
    }
}
```

`App.xaml` sets `ShutdownMode="OnExplicitShutdown"`.

#### A.4.2 TrayIconManager

Context menu:
```
Nexus Desktop (Erik)
─────────────
Sync Now
Activity Log
Settings
─────────────
Logout
Exit
```

- Shows logged-in user name in header
- `DispatcherTimer` fires every N seconds
- Stops timer before sync, restarts after (prevents overlap)
- Updates icon state after each sync (idle/error)
- Updates tooltip after each sync
- **Logout**: clears auth token, stops sync, shows LoginWindow

#### A.4.4 Tray Tooltip

```
Nexus Desktop — Idle
Last sync: 30s ago
```

Or on error:
```
Nexus Desktop — Error
Last sync: 2m ago (failed)
```

#### A.4.6 LoginWindow

Shown on first launch or after logout:
- Nexus URL (TextBox) — pre-filled from config if previously set
- Email (TextBox)
- Password (PasswordBox)
- [Login] button
- Error label for failed attempts (invalid credentials, unreachable server)
- On success: stores auth token + user info in config, starts sync, closes window

```csharp
// Login flow:
// 1. Call NexusApiClient.Login(url, email, password)
// 2. On success → config.SetLoginData(token, hashId, name)
// 3. Start sync timer
// 4. Close LoginWindow
```

#### A.4.7 SettingsWindow

Simple WPF window:
- Sync Interval (TextBox, seconds)
- Lookback Window (TextBox, minutes)
- [Save] [Cancel]

Saves to `config.json`. Logs `config_change` to activity log.

#### A.4.8 ActivityLogWindow

Simple WPF window with `ListView`:
- Columns: Time, Type, Description, Status
- ComboBox filter: All / Sync / Uploads / Errors
- Shows events from `ActivityLogService.GetRecent()`
- Refreshes when opened (no auto-refresh — avoids flicker issues)
- [Refresh] [Close]

#### A.5.2 Login API Endpoint

```php
// POST /api/v1/auth/login (no auth middleware)
// Request: { email, password }
// Response (200): { token, user: { hash_id, first_name } }
// Response (401): { message: 'Invalid credentials.' }
// Response (429): { message: 'Too many attempts.' } (rate limited)

public function login( Request $request ) {
    $request->validate([
        'email' => 'required|email',
        'password' => 'required|string',
    ]);

    $user = User::where('email', $request->email)->first();

    if ( ! $user || ! Hash::check($request->password, $user->password) ) {
        return response()->json(['message' => 'Invalid credentials.'], 401);
    }

    $token = $user->createToken('nexus-desktop')->plainTextToken;

    return response()->json([
        'token' => $token,
        'user' => [
            'hash_id' => $user->hash_id,
            'first_name' => $user->first_name,
        ],
    ]);
}
```

#### A.5.4 StoreTranscriptRequest Changes

```php
// Remove:
'user_id' => 'nullable|integer|exists:users,id',

// Change:
'content' => 'required|string|max:31457280',  // 30MB
```

#### A.5.5 TranscriptController Changes

```php
// User comes from Sanctum auth — no payload fields needed
$userId = $request->user()->id;
```

---

### Phase A — Verification

1. **Build** — `dotnet build Nexus.sln` compiles without errors
2. **Login** — Launch app, login with valid Nexus credentials, verify token stored in config
3. **Login failure** — Try invalid credentials, verify error message shown
4. **Manual sync** — Click "Sync Now", verify transcripts upload (check Nexus DB)
5. **User association** — Verify uploaded transcripts are linked to the logged-in user in DB
6. **Subagent detection** — Verify subagent sessions appear with correct type and parent linkage
7. **State persistence** — Stop and restart app, verify it doesn't re-upload already-synced transcripts
8. **Activity log** — Open Activity Log, verify events show sync cycles, uploads, errors
9. **Error handling** — Revoke Sanctum token in DB, verify tray icon turns red, activity log shows auth error
10. **Settings** — Change sync interval, save, verify timer restarts with new interval
11. **Logout** — Click Logout, verify token cleared, LoginWindow shown, sync stopped
12. **Single instance** — Launch twice, verify second instance exits
13. **Tooltip** — Hover tray icon, verify status + last sync time + user name shown

---

### Phase A — Files

#### Created (C# Project)

| File | Purpose |
|------|---------|
| `.gitignore` | Git ignore rules for .NET/WPF |
| `Nexus.sln` | Solution file |
| `src/Nexus.Core/Nexus.Core.csproj` | Shared core class library |
| `src/Nexus.Core/Models/AppConfig.cs` | Configuration model + loader |
| `src/Nexus.Core/Models/ApiResult.cs` | Structured API response model |
| `src/Nexus.Core/Services/NexusApiClient.cs` | HTTP client for Nexus API |
| `src/Nexus.Core/Services/LogService.cs` | Technical debug logging |
| `src/Nexus.Core/Services/ActivityLogService.cs` | Structured activity event log |
| `src/Nexus.Sync/Nexus.Sync.csproj` | Sync module class library |
| `src/Nexus.Sync/Models/SyncState.cs` | Upload state tracking (file-size) |
| `src/Nexus.Sync/Models/TranscriptFile.cs` | Discovered transcript file model |
| `src/Nexus.Sync/Models/SubagentInfo.cs` | Subagent mapping model |
| `src/Nexus.Sync/Services/TranscriptScanner.cs` | Finds .jsonl files, skips acompact-* |
| `src/Nexus.Sync/Services/TranscriptParser.cs` | Extracts sessionId, cwd, project hash |
| `src/Nexus.Sync/Services/SubagentMapper.cs` | Maps agentId to subagent_type |
| `src/Nexus.Sync/Services/SyncEngine.cs` | Orchestrates sync cycle |
| `src/Nexus.Sync/Services/StateManager.cs` | Manages sync-state.json |
| `src/Nexus.App/Nexus.App.csproj` | WPF tray app project |
| `src/Nexus.App/App.xaml` | Application startup |
| `src/Nexus.App/App.xaml.cs` | Mutex, service wiring, init |
| `src/Nexus.App/TrayIconManager.cs` | TaskbarIcon, menu, timer |
| `src/Nexus.App/Views/LoginWindow.xaml` | Login UI |
| `src/Nexus.App/Views/SettingsWindow.xaml` | Settings UI |
| `src/Nexus.App/Views/ActivityLogWindow.xaml` | Activity log viewer |
| `src/Nexus.App/Resources/nexus.ico` | Tray icon |

#### Modified (Nexus Laravel App)

| File | Changes |
|------|---------|
| `composer.json` | Add `laravel/sanctum` |
| `config/sanctum.php` | Sanctum configuration |
| Migration: Sanctum `personal_access_tokens` table | Standard Sanctum migration |
| Migration: `widen_transcript_id_on_agent_sessions_table` | VARCHAR(50) → VARCHAR(100) |
| `app/Models/User.php` | Add `HasApiTokens` trait |
| `app/Http/Controllers/Api/V1/AuthController.php` | New — login endpoint |
| `routes/api.php` | Add login route, change transcript middleware to `auth:sanctum` |
| `app/Http/Requests/Api/V1/StoreTranscriptRequest.php` | Remove `user_id`, bump content to 30MB |
| `app/Http/Controllers/Api/V1/TranscriptController.php` | Use `$request->user()->id` |

---

## Phase B: Make it Solid (after Phase A is running)

**Goal**: Fix real issues that surface from daily use. Add the improvements that actually matter.

**Trigger**: Phase A has been running for a few weeks, basic issues identified.

### Key Items

| Item | Description |
|------|-------------|
| **Incremental uploads** | Track lines uploaded per file, send only delta. Replaces file-size dedup. |
| **FileSystemWatcher** | Real-time detection supplement to timer. With debounce (2s) and buffer increase (64KB). |
| **Atomic state writes** | Write to `.tmp` then `File.Move()`. Prevents corruption on crash. (A6) |
| **State per-file save** | Save state after each upload, not end of cycle. (A2) |
| **Graceful shutdown** | Wait for ongoing sync before exit. (A3) |
| **Threading** | `Dispatcher.BeginInvoke()` for FileSystemWatcher, `SemaphoreSlim` for sync guard. (A5) |
| **Single-pass SubagentMapper** | Merge two passes into one. (A11) |
| **Unit tests** | `Nexus.Sync.Tests` + `Nexus.Core.Tests`. (A9) |
| **Setup wizard** | First-run wizard (URL + token + test connection). |
| **Installer** | Inno Setup `.exe` installer. |
| **Auto-start** | Registry `HKCU\...\Run` key. Default off. (S7) |
| **Notification policy** | Errors-only default. Configurable level. (U1) |
| **Pause Sync toggle** | Tray menu toggle for offline work. (U12) |
| **Event-driven Activity Log** | Replace manual refresh with live updates. (U2) |
| **Error recovery UX** | Balloon on auth failure, "Sync Now" always available. (U3) |
| **Left-click opens Activity Log** | Quick status check. (U5) |
| **Settings improvements** | Field grouping, Test Connection feedback, Cancel button with unsaved detection. (U6, U7, U10) |
| **Config validation** | URL format, token format, inline errors. (A12) |
| **Config version tracking** | `configVersion` field for future migrations. (A14) |
| **Log rotation** | Cap debug.log at 5MB, rotate on startup. |
| **Tray menu improvements** | Dynamic status text, Open Logs Folder, About dialog. (U14, U15) |
| **ActivityLogService performance** | In-memory cache with async flush. (A10) |
| **Encoding handling** | `StreamReader.ReadLine()` with BOM detection. (A8) |
| **FileSystemWatcher buffer** | Increase to 64KB. (A13) |

---

## Phase C: Harden (only if client-facing)

**Goal**: Production-grade security and engineering. Only pursue if the app is deployed beyond NEXXOR internal use.

**Trigger**: Decision to deploy to client environments.

### Key Items

| Item | Description |
|------|-------------|
| **DPAPI token encryption** | Encrypt API token at rest using `ProtectedData.Protect()`. (S1) |
| **HTTPS enforcement** | Require HTTPS for non-localhost URLs. (S3) |
| **Remove/harden SSL bypass** | Remove `ignoreSslErrors` or replace with cert pinning. (S2) |
| **Path traversal protection** | Validate paths, detect symlinks, file size limit. (S4) |
| **JSON parsing limits** | MaxDepth, per-line size limit. (S5) |
| **File ACLs** | Restrict config directory permissions. (S9) |
| **Dependency injection** | `ServiceCollection`, interfaces for all services, `IHttpClientFactory`. (A1, A4) |
| **Full test coverage** | Mock-based tests enabled by DI. |
| **Accessibility** | Icons + color for colorblind users. (U8) |
| **Dark mode** | Auto-detect Windows theme. (U16) |
| **Agent-to-user binding** | Server validates token owner matches user. (S11) |
| **Mutex hardening** | Path-based hash, proper cleanup. (S8) |
| **Username spoofing mitigation** | Include MachineName, document limitations. (S6) |
| **Streaming uploads** | `Utf8JsonWriter` for large payloads. (S10) |
| **Code signing** | Sign .exe to avoid SmartScreen/AV flags. |

---

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Large binary size (~60-80MB for self-contained) | Acceptable for internal tool. Trimming available if needed. |
| Claude Code transcript format changes | Defensive parsing with try/catch per line. Log warnings for unknown entries. |
| Migration from sync.js — initial re-upload | Server handles duplicate session_ids via UNIQUE constraint. Safe. |
| Sanctum token expiry / revocation | Tokens are long-lived by default (no expiry). Revoke on logout or via Nexus admin. |
| Mixed API auth (Sanctum + Agent tokens) | Only transcript route uses Sanctum. Other routes keep `AuthenticateAgent`. Document in improvements.md. |
| File locking with Claude Code | Open with `FileShare.ReadWrite`. May miss latest line — next cycle catches it. |
| HttpClient socket exhaustion (Phase A) | Single HttpClient instance, reused. Proper factory in Phase B. |
| App crash during sync | Re-uploads on restart are safe (server handles duplicates). Fixed properly in Phase B (per-file state save). |

---

## Notes

- The C# app lives in a **separate repository** (`nexus-windows-app` on GitHub) at `c:\xampp\htdocs\nexus-windows-app\`
- The app communicates with Nexus via `POST /api/v1/transcripts` — same endpoint as sync.js
- API field `session_id` maps to DB column `agent_sessions.transcript_id`
- **Security**: User identified via Sanctum token (`$request->user()`), never exposes integer PKs in API payload
- `acompact-*.jsonl` files are explicitly skipped by the scanner
- The existing WPF scaffold at `nexus-windows-app\Nexus\` will be removed in step A.1.3
- Phase A intentionally keeps things simple — no DI, no incremental uploads, no FileSystemWatcher. These are Phase B improvements.
- sync.js currently uses `http` module (not `https`) but config points to `https://nexus.nexxor.ca` — C# HttpClient handles HTTPS natively
- **Auth migration**: Only the transcript endpoint moves to Sanctum user auth. Other API routes (sessions, instructions, activities) keep `AuthenticateAgent` middleware. See `improvements.md` for future unification plans.

---

## Pre-Launch Reassessment #1

Full security, architecture, and UI/UX review conducted before implementation. Items tagged with target phase.

**Status legend**: `PENDING` — not yet reviewed | `ACCEPTED` — will be incorporated | `REJECTED` — not incorporating (with reason) | `DEFERRED` — moved to Phase B or C

---

### Security Findings

| # | Severity | Finding | Phase | Status |
|---|----------|---------|-------|--------|
| S1 | Critical | **Auth token stored as plaintext** — Use Windows DPAPI to encrypt Sanctum token. | C | `DEFERRED` — internal tool, acceptable risk for now |
| S2 | Critical | **`ignoreSslErrors` allows full SSL bypass** — Remove or replace with cert pinning. | C | `DEFERRED` — option removed from Phase A entirely (not needed) |
| S3 | High | **No HTTPS enforcement** — Require HTTPS for non-localhost URLs. | C | `DEFERRED` — internal network, devs know to use HTTPS |
| S4 | High | **FileSystemWatcher path traversal** — Validate paths, detect symlinks. | C | `DEFERRED` — no FileSystemWatcher in Phase A; add protection in Phase B/C |
| S5 | High | **JSON deserialization without limits** — Set MaxDepth, skip oversized lines. | C | `DEFERRED` — trusted input (Claude-generated files) |
| S6 | Medium | ~~**Windows username spoofable**~~ — No longer applicable, replaced by user login. | — | `RESOLVED` |
| S7 | Medium | **Auto-start defaults to `true`** — Default to `false`. | B | `DEFERRED` — no auto-start in Phase A |
| S8 | Medium | **Named mutex predictable** — Use path-based hash. | C | `DEFERRED` — acceptable for internal tool |
| S9 | Medium | **No file ACLs on config/state** — Set restrictive permissions. | C | `DEFERRED` — single-user dev machines |
| S10 | Low | **30MB payload memory spike** — Consider streaming serialization. | C | `DEFERRED` |
| S11 | Low | ~~**No agent-to-user binding**~~ — No longer applicable, transcript endpoint uses Sanctum user auth. | — | `RESOLVED` |

---

### Architecture & Dev Findings

| # | Severity | Finding | Phase | Status |
|---|----------|---------|-------|--------|
| A1 | Critical | **No dependency injection** — Add DI container + interfaces. | C | `DEFERRED` — overkill for Phase A, add when tests need mocking |
| A2 | Critical | **State saved at end of cycle** — Save after each upload. | B | `DEFERRED` — server handles re-uploads safely, fix in Phase B |
| A3 | Critical | **No graceful shutdown** — Wait for ongoing sync before exit. | B | `DEFERRED` — sync cycle is short, low risk in Phase A |
| A4 | High | **HttpClient lifecycle** — Use IHttpClientFactory. | C | `DEFERRED` — singleton HttpClient in Phase A is adequate |
| A5 | High | **Threading issues** — Dispatcher, debounce, SemaphoreSlim. | B | `DEFERRED` — Phase A uses timer-only with stop/start guard |
| A6 | High | **state.json corruption risk** — Atomic write via temp file. | B | `DEFERRED` — low crash frequency, fix in Phase B |
| A7 | High | **File locking with Claude Code** — Use `FileShare.ReadWrite`. | A | `ACCEPTED` — app will crash without this |
| A8 | High | **Line-count encoding issues** — Use `StreamReader.ReadLine()`. | B | `DEFERRED` — only relevant for incremental uploads |
| A9 | High | **No Nexus.Core.Tests** — Add test project. | B | `DEFERRED` |
| A10 | High | **ActivityLogService performance** — In-memory cache + async flush. | B | `DEFERRED` — simple file append adequate for Phase A |
| A11 | Medium | **SubagentMapper two-pass** — Merge into single pass. | B | `DEFERRED` — two-pass works (sync.js does same) |
| A12 | Medium | **Config validation** — Validate URL/token format. | B | `DEFERRED` |
| A13 | Medium | **FileSystemWatcher buffer** — Increase to 64KB. | B | `DEFERRED` — no FileSystemWatcher in Phase A |
| A14 | Low | **No config version tracking** — Add `configVersion` field. | B | `DEFERRED` |

---

### UI/UX Findings

| # | Severity | Finding | Phase | Status |
|---|----------|---------|-------|--------|
| U1 | Critical | **Balloon notification spam** — Default to errors only. | B | `DEFERRED` — no balloon notifications in Phase A |
| U2 | Critical | **Activity Log auto-refresh flicker** — Use event-driven updates. | B | `DEFERRED` — Phase A uses manual Refresh button |
| U3 | Critical | **No error recovery path** — Auto-retry transient, manual for auth. | B | `DEFERRED` — Phase A retries on next timer cycle, shows red icon |
| U4 | High | **No tray icon tooltip** — Add dynamic tooltip. | A | `ACCEPTED` — easy win, big UX value |
| U5 | High | **Left-click behavior undefined** — Left-click opens Activity Log. | B | `DEFERRED` |
| U6 | High | **Test Connection no feedback** — Add spinner/success/failure states. | B | `DEFERRED` — not in Phase A settings |
| U7 | High | **No Cancel button in Settings** — Add Cancel + Save. | A | `ACCEPTED` — basic UI, should have this |
| U8 | High | **Color-only status indicators** — Use icons + color. | C | `DEFERRED` |
| U9 | High | **Token acquisition unclear** — Better instructions, "Skip" option. | B | `DEFERRED` — Phase A is manually configured |
| U10 | Medium | **Settings fields not grouped** — Use GroupBox controls. | B | `DEFERRED` |
| U11 | Medium | **No search in Activity Log** — Add search box. | B | `DEFERRED` |
| U12 | Medium | **No "Pause Sync" toggle** — Add to tray menu. | B | `DEFERRED` |
| U13 | Medium | **Setup wizard too minimal** — Change to 4 steps. | B | `DEFERRED` — no wizard in Phase A |
| U14 | Medium | **Tray menu missing items** — Add Pause, Logs Folder, About. | B | `DEFERRED` |
| U15 | Low | **No "About" dialog** — Add to tray menu. | B | `DEFERRED` |
| U16 | Low | **No dark mode** — Auto-detect Windows theme. | C | `DEFERRED` |

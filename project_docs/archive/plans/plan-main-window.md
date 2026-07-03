<!-- plan-main-window.md | c:\xampp\htdocs\nexus-windows-app\project_docs\plans\plan-main-window.md -->

> Author: Erik | Agent: tech-lead | Created: 2026-05-28 | Status: Completed

# Main Window Dashboard — Implementation Plan

## Context

Today the app is tray-only. All interaction goes through the right-click context menu (Sync Now, Activity Log, Settings, Check for Updates, Logout, Exit). There is no main window. Users have no surface to see live state (logged-in user, last sync time, sync status, version) and the existing dialogs (`LoginWindow`, `SettingsWindow`, `ActivityLogWindow`) are launched ad-hoc.

This plan adds a `MainWindow` dashboard. It mirrors the tray menu actions as buttons, surfaces live state, opens via a new "Open Nexus" tray menu entry, and hides to tray on close. The tray menu and Exit behavior are unchanged.

---

## Architecture / Approach

`SyncEngine` becomes the event source for the sync lifecycle. `TrayIconManager` and `MainWindow` both subscribe; neither tracks state independently.

Today `TrayIconManager` owns `_isSyncing`, `_lastSyncTime`, `_lastSyncFailed` locally because it is the only consumer. Adding a second consumer (the dashboard) without moving state to the engine would duplicate tracking and make correctness depend on which UI surface is active. Move state once, subscribe twice.

```
SyncEngine.RunSync()
  ├── fires SyncStarted          → TrayIconManager.UpdateTooltip()
  │                                MainWindow.RefreshState()
  ├── ... work ...
  └── fires SyncCompleted(result)→ TrayIconManager.UpdateTooltip()
                                   MainWindow.RefreshState()
```

The dispatch timer stays in `TrayIconManager` (timer plumbing, not domain). Re-entrancy guard moves into `SyncEngine` (an engine invariant, not a tray concern).

`MainWindow` is a single instance owned by `App`. Close intercepts `Closing`, sets `e.Cancel = true`, calls `Hide()`. Re-opens via `Show()` + `Activate()`. Real exit only via tray "Exit" → `App.Shutdown()`.

The "Open Nexus" menu item appears only when `_config.IsLoggedIn` (mirrors the existing pattern for "Logout").

---

## Decisions / Rejected Options

| Option | Reason rejected | Ruling date |
|--------|-----------------|-------------|
| Consolidate `LoginWindow` into `MainWindow` | PM: keep login separate | 2026-05-28 |
| Auto-show `MainWindow` on startup | PM: open via menu only | 2026-05-28 |
| Double-click tray opens `MainWindow` | PM: right-click → "Open Nexus" only | 2026-05-28 |
| "Exit" button in `MainWindow` | PM: Exit is tray-only | 2026-05-28 |
| Poll `ActivityLogService` for sync state (instead of engine events) | Fragile (parses display strings), no "running" state, lag | 2026-05-28 |
| Extract `IShellActions` interface so `MainWindow` doesn't hold `App` | App is small and self-contained; one consumer (MainWindow) doesn't justify the indirection. Coupling is acknowledged — keep `App this` in constructor. | 2026-05-28 |
| Use `Interlocked.CompareExchange` on `IsRunning` | All entry points are UI-thread (`DispatcherTimer.Tick`, tray menu clicks, MainWindow button clicks). Check-then-set is benign under that invariant. Constraint documented in Step 1.3. | 2026-05-28 |

---

## Plan Overview

1. **SyncEngine event surface** — `SyncResult`, events, `IsRunning`, `LastSync`, internal re-entrancy guard.
2. **TrayIconManager refactor** — drop local state, subscribe to engine events, add "Open Nexus" menu item.
3. **MainWindow** — dashboard XAML + code-behind, subscribe to engine events, hide-on-close.
4. **App lifecycle wiring** — own `MainWindow` instance, expose `ShowMainWindow()`, handle login/logout.
5. **Docs & version** — update `structure.md`, `CHANGELOG.md`, bump version.
6. **Close-out** — verification checklist.

---

## Steps

### Step 1: SyncEngine event surface

Add the event/property surface that `TrayIconManager` and `MainWindow` will subscribe to. Move re-entrancy guard from `TrayIconManager` into `SyncEngine`.

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 1.1 | Create `SyncResult` record (Uploaded, Skipped, ParseSkipped, Errors, CompletedAt, Status) | dev | CREATE | `src/Nexus.Sync/Models/SyncResult.cs` | Done |
| 1.2 | Add `IsRunning`, `LastSync`, `event Action? SyncStarted`, `event Action<SyncResult>? SyncCompleted` to SyncEngine; emit at start/end of `RunSync()` per the **ordering invariant** in 1.2 detailed sub-step; package local counters into a `SyncResult` | dev | MODIFY | `src/Nexus.Sync/Services/SyncEngine.cs` | Done |
| 1.3 | Move re-entrancy guard from TrayIconManager into SyncEngine; engine sets `IsRunning` true after guard, false in `finally` **before firing `SyncCompleted`**. Annotate `RunSync()` with `// must be called on the UI dispatcher — IsRunning is not Interlocked` | dev | MODIFY | `src/Nexus.Sync/Services/SyncEngine.cs` | Done |

### Step 2: TrayIconManager refactor

Drop local sync-state fields, subscribe to engine events, add the "Open Nexus" menu entry.

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 2.1 | Remove `_lastSyncTime`, `_lastSyncFailed`, `_isSyncing`; **delete the `RunSync()` wrapper** but **preserve the `_config.IsLoggedIn` check** by inlining it into the timer `Tick` lambda (see 2.1 detailed sub-step); subscribe to `_syncEngine.SyncStarted` / `SyncCompleted` and call `UpdateTooltip()` from handlers via the UI dispatcher; read state via `_syncEngine.IsRunning` and `_syncEngine.LastSync` | dev | MODIFY | `src/Nexus.App/TrayIconManager.cs` | Done |
| 2.2 | Add "Open Nexus" menu item at top of context menu (above "Sync Now"); visible only when `_config.IsLoggedIn`; click handler → `_app.ShowMainWindow()` | dev | MODIFY | `src/Nexus.App/TrayIconManager.cs` | Done |
| 2.3 | Update "Exit" click handler to close MainWindow first: `_app.PrepareForShutdown(); _app.Shutdown();` — single shutdown entry point (see 4.2) | dev | MODIFY | `src/Nexus.App/TrayIconManager.cs` | Done |

### Step 3: MainWindow

Create the dashboard. Mirrors tray menu actions (minus Exit). State panel reads from `SyncEngine` + `AppConfig`.

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 3.1 | Create `MainWindow.xaml` — top: state panel (user, last sync, sync status, version); bottom: action buttons (Sync Now, Activity Log, Settings, Check for Updates, Logout). Controls use Hungarian prefixes per C# conventions. | dev | CREATE | `src/Nexus.App/MainWindow.xaml` | Done |
| 3.2 | Create `MainWindow.xaml.cs` — constructor receives dependencies (config, syncEngine, activity, app); subscribe to SyncEngine events, marshalling UI updates via `Application.Current.Dispatcher`; **each handler short-circuits with `if ( ! IsVisible ) return;`** to avoid repainting a hidden singleton; wire button click handlers to App methods (shared with tray); expose `RefreshState()` to re-render user/version/state on demand | dev | CREATE | `src/Nexus.App/MainWindow.xaml.cs` | Done |
| 3.3 | Add `AllowClose()` + `_allowClose` flag; override `OnClosing` to cancel and `Hide()` unless `_allowClose` is true; unsubscribe from engine events in `OnClosed` (real-close path only — triggered from tray "Exit" via `App.PrepareForShutdown`) | dev | MODIFY | `src/Nexus.App/MainWindow.xaml.cs` | Done |

### Step 4: App lifecycle wiring

Hold the single `MainWindow` instance, expose `ShowMainWindow()`, integrate with login/logout/exit.

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 4.1 | Add `_mainWindow` field; add `ShowMainWindow()` method (lazy-creates on first call; `Show()` + `Activate()`; restores from minimized; calls `_mainWindow.RefreshState()` so re-show after relogin renders fresh user/state). Never opens on startup. | dev | MODIFY | `src/Nexus.App/App.xaml.cs` | Done |
| 4.2 | Extract shared UI action handlers (`TriggerSync`, `ShowActivityLogWindow`, `ShowSettingsWindow`; `TriggerUpdateCheck` and `Logout` already exist) so both tray menu and MainWindow buttons call the same paths. In `Logout()`, after `_trayManager?.StopSync()` and before `await _api.RevokeToken()`: call `_mainWindow?.Hide();` **then** `_mainWindow?.RefreshState();` — repaints the singleton from cleared state so it never holds the prior user's labels even momentarily. | dev | MODIFY | `src/Nexus.App/App.xaml.cs`, `src/Nexus.App/TrayIconManager.cs` | Done |
| 4.3 | Add `PrepareForShutdown()` method on App — sets `_mainWindow?.AllowClose()` then `_mainWindow?.Close()`. Tray "Exit" calls `App.PrepareForShutdown()` then `App.Shutdown()`. Replaces the previous approach of closing from `OnExit` (too late under `OnExplicitShutdown`). | dev | MODIFY | `src/Nexus.App/App.xaml.cs` | Done |

### Step 5: Docs & version

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 5.1 | Add `MainWindow` to structure.md (Windows/Views section); document SyncEngine event surface (`IsRunning`, `LastSync`, `SyncStarted`, `SyncCompleted`, `SyncResult`) | doc-writer | MODIFY | `project_docs/structure.md` | Done |
| 5.2 | CHANGELOG entry for new version (minor bump per versioning-conventions.md: 1.0.11 → 1.1.0); update `<Version>` in csproj | dev | MODIFY | `CHANGELOG.md`, `src/Nexus.App/Nexus.App.csproj` | Done |

### Step 6 (final — always last): Close-out

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 6.1 | Propose close-out checklist per Rule 21 (chat only — not written to plan) | tech-lead | RUN | — | Done |

---

## Detailed Sub-steps

### 1.1 SyncResult model

**File**: `src/Nexus.Sync/Models/SyncResult.cs`

Immutable DTO:

```csharp
public record SyncResult(
    int Uploaded,
    int Skipped,
    int ParseSkipped,
    int Errors,
    DateTime CompletedAt,
    string Status   // "ok" | "error" — matches existing activity log convention
);
```

### 1.2 SyncEngine event emission

**Ordering invariant.** Subscribers (`UpdateTooltip`, `RefreshState`) read `_syncEngine.IsRunning` and `_syncEngine.LastSync` inside their handlers, so the sequence must be exact:

1. Guard: `if ( IsRunning ) return;`
2. `IsRunning = true;`
3. `_activity.Log("sync_start", ...)`
4. `SyncStarted?.Invoke();` ← subscribers see `IsRunning == true`
5. ... work (scan / parse / upload) ...
6. `_activity.Flush();`
7. Build `result = new SyncResult( uploadCount, skipCount, parseSkipCount, errorCount, DateTime.Now, status );`
8. `LastSync = result;`
9. `IsRunning = false;` ← MUST be false before SyncCompleted fires
10. `SyncCompleted?.Invoke(result);` ← subscribers see `IsRunning == false` and updated `LastSync`

Steps 6–10 run inside the `finally` so the engine resets `IsRunning` and signals completion even on exception. If `IsRunning = false` is set AFTER `SyncCompleted.Invoke()`, handlers render "Syncing…" briefly on a completed cycle.

### 1.3 Re-entrancy guard relocation

Move `if ( _isSyncing ) return;` from `TrayIconManager.RunSync()` to top of `SyncEngine.RunSync()` as `if ( IsRunning ) return;`. Set `IsRunning = true` immediately after the guard; reset in `finally` per the ordering in 1.2. Delete `_isSyncing` from `TrayIconManager`.

**Thread-safety constraint.** `IsRunning` is a plain `bool`, not `Interlocked`. This is safe because every entry point is UI-dispatcher: `DispatcherTimer.Tick`, tray menu clicks, and MainWindow button clicks. Annotate the method:

```csharp
// Must be called on the UI dispatcher. IsRunning is plain check-then-set, not Interlocked.
// All current callers (DispatcherTimer.Tick, tray menu, MainWindow buttons) run on the UI thread.
public async Task RunSync() { ... }
```

If a future caller dispatches `RunSync()` from a worker thread, switch the guard to `Interlocked.CompareExchange` first.

### 2.1 TrayIconManager subscription

Capture the UI dispatcher once at construction and marshal event handlers through it — `SyncEngine` events may fire on a worker thread (`RunSync` awaits HTTP I/O), so `Dispatcher.CurrentDispatcher` evaluated inside the lambda would return the wrong dispatcher:

```csharp
var ui = Application.Current.Dispatcher;
_syncEngine.SyncStarted   += () => ui.Invoke(UpdateTooltip);
_syncEngine.SyncCompleted += _  => ui.Invoke(UpdateTooltip);
```

`UpdateTooltip()` reads `_syncEngine.IsRunning`, `_syncEngine.LastSync?.CompletedAt`, `_syncEngine.LastSync?.Status`. Unsubscribe in `Dispose()`.

Delete the `RunSync()` wrapper entirely. Re-wire the timer — **the `_config.IsLoggedIn` guard previously in the wrapper ([TrayIconManager.cs:119](TrayIconManager.cs#L119)) must be preserved** in the Tick lambda:

```csharp
_timer.Tick += async ( s, e ) => {
    if ( ! _config.IsLoggedIn ) return;
    await _syncEngine.RunSync();
};
```

The engine's `IsRunning` guard handles re-entry; the old `_isSyncing` field + timer stop/start dance are unnecessary. The login check stays at the Tick layer (defense-in-depth alongside `_trayManager?.StopSync()` on logout) rather than being pushed into `SyncEngine`, which would require adding an `AppConfig` dependency to the engine.

### 3.1 MainWindow layout

Fixed-ish window (~500x400), non-resizable initially. Layout:

1. Top panel: `lblUserName`, `lblLastSync`, `lblStatus`, `lblVersion`
2. Button stack: `btnSyncNow`, `btnActivityLog`, `btnSettings`, `btnCheckUpdates`, `btnLogout`

No new styling system — match the look of existing dialogs.

### 3.3 Hide-on-close

```csharp
private bool _allowClose;

public void AllowClose() => _allowClose = true;

protected override void OnClosing( CancelEventArgs e ) {
    if ( ! _allowClose ) {
        e.Cancel = true;
        Hide();
        return;
    }
    base.OnClosing(e);
}
```

The real close is driven from `App.PrepareForShutdown()` (see 4.3), which is invoked from the tray "Exit" handler *before* `App.Shutdown()`. Closing from `App.OnExit` would be too late under `ShutdownMode.OnExplicitShutdown` — by then the message pump is unwinding.

### 4.1 ShowMainWindow

```csharp
public void ShowMainWindow() {
    if ( ! _config.IsLoggedIn ) return;
    _mainWindow ??= new MainWindow(_config, _syncEngine, _activity, this);
    _mainWindow.RefreshState();
    _mainWindow.Show();
    if ( _mainWindow.WindowState == WindowState.Minimized ) _mainWindow.WindowState = WindowState.Normal;
    _mainWindow.Activate();
}
```

`RefreshState()` ensures the singleton window re-renders user/version/state every time it's shown — handles the relogin-with-different-user case.

### 4.3 PrepareForShutdown

```csharp
public void PrepareForShutdown() {
    _mainWindow?.AllowClose();
    _mainWindow?.Close();
}
```

Tray "Exit" click handler becomes:

```csharp
exit.Click += ( s, e ) => {
    _app.PrepareForShutdown();
    _app.Shutdown();
};
```

---

## File Summary

### Created Files

| File | Purpose |
|------|---------|
| `src/Nexus.Sync/Models/SyncResult.cs` | DTO carrying sync cycle counts + timestamp + status |
| `src/Nexus.App/MainWindow.xaml` | Dashboard layout |
| `src/Nexus.App/MainWindow.xaml.cs` | Dashboard code-behind — subscribes to SyncEngine events |

### Modified Files

| File | Changes |
|------|---------|
| `src/Nexus.Sync/Services/SyncEngine.cs` | Add `IsRunning`, `LastSync`, `SyncStarted`, `SyncCompleted` events; emit at start/end; own re-entrancy guard |
| `src/Nexus.App/TrayIconManager.cs` | Remove local sync-state fields; subscribe to engine events; add "Open Nexus" menu item; remove local re-entrancy guard |
| `src/Nexus.App/App.xaml.cs` | Add `_mainWindow` field + `ShowMainWindow()`; hide MainWindow on logout; close MainWindow on exit |
| `project_docs/structure.md` | Add MainWindow + SyncEngine event surface |
| `CHANGELOG.md` | New version entry (1.1.0) |
| `src/Nexus.App/Nexus.App.csproj` | Version bump to 1.1.0 |

---

## Verification

1. Launch app → no main window shown on startup; tray icon appears.
2. Right-click tray → "Open Nexus" item visible (only when logged in).
3. Click "Open Nexus" → MainWindow opens with current state: user name, last sync (or "never"), status (Idle/Error), version.
4. Click "Sync Now" in MainWindow → status changes to "Syncing…" then back to "Idle" or "Error" with updated timestamp. Tray tooltip also updates simultaneously (same event source).
5. Click X (close) on MainWindow → window hides; tray icon remains; right-click → "Open Nexus" reopens the same instance with current state.
6. Wait for automatic sync cycle (or trigger via tray "Sync Now") → if MainWindow is open, state refreshes without user action.
7. Click "Logout" from MainWindow → LoginWindow appears, MainWindow hides, tray menu drops "Open Nexus" + "Logout".
8. Re-login as a **different** user → tray menu shows "Open Nexus" again; MainWindow does NOT auto-show; opening it shows the NEW user's name and a fresh state (not the prior user's cached display).
9. Right-click tray → "Exit" → process terminates cleanly; no orphaned `MainWindow` process state; `OnExit` runs `_activity.Flush()` successfully.
10. Re-open app → no auto-show of MainWindow.

---

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Event subscription leak — MainWindow held alive after window hidden | MainWindow is a singleton kept by `App` for the app's lifetime — holding subscriptions is intended, not a leak. Unsubscribe only on real close (`App.OnExit`). Handlers short-circuit with `if ( ! IsVisible ) return;` to skip UI repaints while hidden (Step 3.2). |
| `SyncCompleted` fired from non-UI thread → cross-thread WPF update | Capture `Application.Current.Dispatcher` once at subscription site (constructor — guaranteed UI thread) and marshal via that captured reference. Do NOT use `Dispatcher.CurrentDispatcher` inside the lambda — it evaluates at call time and would return the worker thread's dispatcher. |
| Closing `MainWindow` from `App.OnExit` is too late under `OnExplicitShutdown` mode | Drive real close from `App.PrepareForShutdown()` invoked by tray "Exit" *before* `App.Shutdown()`. |
| `MainWindow` singleton shows stale user info after a different user logs in | `App.ShowMainWindow()` calls `_mainWindow.RefreshState()` before `Show()`. `Logout()` also calls `RefreshState()` immediately after `Hide()` so the singleton is never "logged-out-but-showing-previous-user" even while hidden (Step 4.2). |
| `ShowMainWindow()` called when logged out → invalid state | Guard `if ( ! _config.IsLoggedIn ) return;` in `ShowMainWindow()`. Menu item also hidden in that state. |
| Re-entrancy guard move breaks tray "Sync Now" rapid-click protection | Guard semantics preserved — `if ( IsRunning ) return` is functionally identical, just relocated. |
| Login guard from old `TrayIconManager.RunSync()` wrapper lost when wrapper is deleted | Preserved in the timer Tick lambda (`if ( ! _config.IsLoggedIn ) return;` before `await _syncEngine.RunSync()`) — see Step 2.1 detailed sub-step. Not pushed into `SyncEngine` to avoid coupling the engine to `AppConfig`. |
| `IsRunning` check-then-set not thread-safe | All entry points are UI-dispatcher (`DispatcherTimer.Tick`, tray menu, MainWindow buttons). `RunSync()` carries an explicit "must be called on the UI dispatcher" comment (Step 1.3). Future worker-thread callers must switch to `Interlocked.CompareExchange`. |
| Event ordering: subscribers see wrong `IsRunning`/`LastSync` snapshot | Step 1.2 pins the exact sequence — `IsRunning = true` before `SyncStarted`; `LastSync` updated and `IsRunning = false` before `SyncCompleted`. |
| Shared handlers between tray + MainWindow drift over time | Single source: action methods live in `App`. Tray and MainWindow both call them — no duplication. |

---

## Notes

- "Open Nexus" intentionally does NOT bind to tray double-click. PM decision: menu-only.
- `MainWindow` does NOT include "Exit". Exit remains tray-only per PM decision.
- Login flow is unchanged — `LoginWindow` stays a separate popup launched from `App`.
- This is WPF/XAML — owned by the `dev` agent. `ui-ux` is Blade/SCSS only.
- Version bump is minor (1.0.11 → 1.1.0): new user-visible feature without breaking changes.

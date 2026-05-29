<!-- plan-main-window-review.md | c:\xampp\htdocs\nexus-windows-app\project_docs\assessments\plan-main-window-review.md -->

> Author: Erik | Agent: tech-lead | Created: 2026-05-28 | Status: Active

# Plan Main Window — Plan Review (2026-05-28)

**File:** `project_docs/plans/plan-main-window.md`
**Status:** All findings resolved — plan updated 2026-05-28
**Methodology:** LOW tier, single-pass (architect + code-reviewer fact-checker), 2026-05-28

LOW tier — single-pass plan review. Findings should be treated as hypotheses except where a `VERIFIED:` source citation is given.

## High Issues (1)

| # | Issue | Location | Confidence | Status |
|---|-------|----------|------------|--------|
| 1 | `TrayIconManager.RunSync()` line 119 contains `if ( ! _config.IsLoggedIn ) return;` in addition to the `_isSyncing` re-entrancy guard. Step 2.1 deletes the entire `RunSync()` wrapper and re-wires the timer Tick directly to `_syncEngine.RunSync()` — the login check is not moved into `SyncEngine`, not added to the Tick lambda, and not mentioned in the Risks table. If the wrapper is deleted as written, the timer will trigger sync attempts while logged out. Fix: either add the guard to `SyncEngine.RunSync()` (alongside the new `IsRunning` guard) or keep it in the Tick lambda. [TrayIconManager.cs:119](../../src/Nexus.App/TrayIconManager.cs#L119) | Step 2.1 | Single (verified) | FIXED |

## Low / Style Issues (5)

| # | Issue | Location | Confidence | Status |
|---|-------|----------|------------|--------|
| 2 | Event-fire ordering relative to `IsRunning` is implied but not pinned. Subscribers (`UpdateTooltip`, `RefreshState`) read `_syncEngine.IsRunning` inside the handler, so `IsRunning` must be `true` at the moment `SyncStarted` fires and `false` at the moment `SyncCompleted` fires. If the `finally` block sets `IsRunning = false` AFTER `SyncCompleted.Invoke()`, handlers will momentarily render "Syncing…" on a completed cycle. Pin the order explicitly in Step 1.2/1.3: guard → `IsRunning = true` → fire `SyncStarted` → work → build result + `LastSync = result` → `IsRunning = false` → fire `SyncCompleted(result)` (all inside `finally`). | Step 1.2, Step 1.3 | Single | FIXED |
| 3 | `IsRunning` check-then-set has no `Interlocked`/`lock`. All current callers (`DispatcherTimer.Tick`, tray "Sync Now", MainWindow "Sync Now") are UI-thread, so the race is benign today. The plan does not document this UI-thread constraint. If a future caller invokes `RunSync()` from a worker thread, two cycles could enter concurrently. Either use `Interlocked.CompareExchange` or annotate `RunSync()` with "must be called on the UI dispatcher". | Step 1.3 | Single | FIXED |
| 4 | After first creation, `MainWindow` keeps event subscriptions for app lifetime and processes every `SyncStarted`/`SyncCompleted` event — even while hidden post-close-to-tray. Not a correctness bug (Risks row 1 explicitly accepts the singleton), but wasted dispatcher work on every sync cycle while the dashboard is closed. Cheap fix: `if ( ! IsVisible ) return;` short-circuit at the top of each MainWindow handler. | Step 3.2 | Single | FIXED |
| 5 | `Logout()` calls `_mainWindow?.Hide()` but does not refresh state. The singleton retains the prior user's labels and rendered state until the next `ShowMainWindow()` calls `RefreshState()`. Works as long as no MainWindow field caches data that `RefreshState()` doesn't repaint. Either call `RefreshState()` on `Hide()` in the logout path (after `ClearLoginData()`) so the window is never "logged-out-but-showing-previous-user" even momentarily, or document explicitly that `RefreshState()` MUST repaint every user-derived field. | Step 4.2 | Single | FIXED |
| 6 | `MainWindow` constructor receives `App this` (`new MainWindow(_config, _syncEngine, _activity, this)`) so the child window holds a reference back to the parent App to invoke `TriggerSync`, `ShowActivityLogWindow`, etc. Backwards dependency — child knows shell. Pragmatic for a small WPF app but couples MainWindow to App's full surface and makes it harder to test in isolation. A small `IShellActions` interface exposing only the four button handlers would isolate the coupling. Flag-only — fine to accept as-is given app size. | Step 3.2, Step 4.1 | Single | FIXED |

## Summary

All 6 findings applied to the plan on 2026-05-28:

1. **High #1** → Step 2.1 detailed sub-step now preserves `if ( ! _config.IsLoggedIn ) return;` in the timer Tick lambda; added a Risks row covering the migration.
2. **Low #2** → Step 1.2 detailed sub-step now pins the exact 10-step ordering (`IsRunning = true` before `SyncStarted`; `LastSync` set and `IsRunning = false` before `SyncCompleted`).
3. **Low #3** → Step 1.3 carries the `// must be called on the UI dispatcher` annotation; added a Risks row and a Rejected Options row for `Interlocked.CompareExchange`.
4. **Low #4** → Step 3.2 specifies `if ( ! IsVisible ) return;` at the top of each MainWindow event handler; Risks row updated.
5. **Low #5** → Step 4.2 now calls `_mainWindow?.RefreshState()` immediately after `_mainWindow?.Hide()` in `Logout()`; Risks row updated.
6. **Low #6** → Added a Rejected Options row documenting that the `App` coupling in `MainWindow`'s constructor is acknowledged and accepted at this scale.

## What's Good

- Plan correctly identifies and addresses the dispatcher capture pitfall (Risks row 2, Step 2.1 code example uses captured `ui` rather than `Dispatcher.CurrentDispatcher` inside the lambda).
- `PrepareForShutdown()` correctly avoids the `OnExit`-too-late issue under `ShutdownMode.OnExplicitShutdown` (verified in [App.xaml:4](../../src/Nexus.App/App.xaml#L4)).
- Decisions / Rejected Options table is thorough — login consolidation, auto-show, double-click, in-window Exit, and ActivityLogService polling are all explicitly rejected with reasons.
- `SyncResult` is placed in `Nexus.Sync/Models/` keeping the engine layer free of WPF dependencies; events use `Action`/`Action<SyncResult>` rather than `EventHandler` — clean cross-project boundary.
- Verification checklist (step 8 in particular — re-login as a different user) catches the singleton-stale-user scenario that Risks row 4 also addresses.
- File path claims, existing-field claims (`_trayManager`, `_api`, `_config`, `_syncEngine`, `_activity`, `Logout()` ordering, `OnExit` `_activity.Flush()`), and the `_config.IsLoggedIn` pattern for "Logout" all check out against the source.

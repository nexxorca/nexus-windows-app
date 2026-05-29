<!-- plan-ai-config-sync.md | c:\xampp\htdocs\nexus-windows-app\project_docs\plans\plan-ai-config-sync.md -->

> Author: Erik | Agent: tech-lead | Created: 2026-05-29 | Status: Proposed

# AI Config Sync (Client) — Implementation Plan

## Context

PM is the single source of truth for `~/.claude/` (agents, skills, hooks, conventions, registries, root markdown files, `settings.json`). The cross-project distribution pipeline is documented in `c:/xampp/htdocs/nexus/project_docs/plans/plan-ai-config-distribution.md` — it owns the source-side mirror skill (`/sync-claude`), the API endpoints (`/api/v1/ai-config/manifest`, `/api/v1/ai-config/file`), and the snapshot-manifest format.

This plan covers only Phase 3 of that pipeline: the Nexus Windows App pulls the manifest, downloads files, verifies SHAs, backs up the dev's prior `~/.claude/`, atomically applies, rolls back on failure, and persists a version marker. The server-side (nexus) and source-side (`/sync-claude`) are out of scope here.

Phase 2 of the upstream plan is Done — the API exists and is tested. Phase 1 (manifest writer in `/sync-claude`) is still TODO; until it runs, `/api/v1/ai-config/manifest` returns 404, which this client treats as "no snapshot yet" (no popup, no error). End-to-end requires both Phase 1 and this plan to be done.

---

## Architecture / Approach

```
Trigger (login | manual button | 4h timer)
   ↓
ClaudeCodeInstallProbe.IsInstalled()  — single file-existence check
   ↓ (block + alert if not installed)
NexusApiClient.GetAiConfigManifest()  — Sanctum-auth Bearer
   ↓ (404 = "no snapshot" — silent return; other errors = toast + return)
Compare manifest.version vs %LOCALAPPDATA%\Nexus\ai-config-version
   ↓ (match → no-op)
AiConfigUpdatePromptWindow  — "Close Claude Code. Apply now / Later"
   ↓ (Apply)
AiConfigApplyService.ApplyAsync(manifest):
   1. Acquire in-process lock                                                                  
   2. Pre-apply backup (cap-on-take retention ≤ 5) into ~/.claude/backups/{ts}/
   3. Download every manifest file → %TEMP%\nexus-ai-config-{ts}\
   4. SHA-256 verify every file                                            → abort if any fail
   5. Write phase:  ~/.claude/{path}.tmp  (fsynced)                        → abort if any fail
   6. Rename phase: File.Move({path}.tmp → {path}, overwrite: true)
        track success list; on failure, ROLLBACK from backup → abort
   7. Perfect-fit delete (anything in ~/.claude not in manifest ∪ exclusion)
   8. Persist version marker
   9. Drop lock
   ↓
Toast result. Refresh MainWindow state.
```

Three invariants:
1. The dev's `~/.claude/` is always either fully on version N or fully on version N+1. Never half-applied (rollback restores from the pre-apply backup if the rename loop fails partway).
2. The version marker (`%LOCALAPPDATA%\Nexus\ai-config-version`) advances only on full success. Any abort or rollback leaves it on the prior version, so the next trigger retries from scratch.
3. The pull-side exclusion list protects dev-local state (`projects/`, `settings.local.json`, credentials, caches, `backups/` itself) from perfect-fit delete.

`AiConfigApplyService` lives in `Nexus.Sync` alongside the existing transcript `SyncEngine` — same project, separate domain.

---

## Decisions / Rejected Options

**Accepted risks (re-runnable feature):** the apply is fully idempotent — any failure leaves `~/.claude/` either on N (intact) or in a recoverable state cleanable by the next trigger. The following failure modes are NOT specially handled; the dev simply re-triggers sync:
- Power loss / hard kill mid-rename (orphan `.tmp` cleanup + next-trigger re-apply handles it).
- Tray Exit during apply (same).
- Velopack restart mid-apply (same).
- Total pipeline timeout / sleep-resume mid-download (same).
- Backup mid-copy failure (same; apply aborts, partial backup dir left for inspection).
- Rollback failure on newly-added manifest paths missing from backup (rollback may leave a stray new file; next-trigger re-apply overwrites or perfect-fit-deletes it).
- Manifest path traversal — trusted: `/sync-claude` is PM-controlled, server validates; client does not re-validate.
- Perfect-fit delete vs unknown future Claude runtime files — exclusion list is updated reactively if Claude Code introduces a new dev-local path.

| Option | Reason rejected | Ruling date |
|--------|-----------------|-------------|
| Separate `DispatcherTimer` for the 4h AI-config check | Piggy-back the existing Velopack 4h timer in `App.xaml.cs` — one tick, two checks | 2026-05-29 |
| Two consent shapes (popup for login/timer, inline for manual) | Useless duplication — single popup for all three triggers | 2026-05-29 |
| Multi-method Claude install probe (executable path + registry + `claude --version` spawn) | Most efficient = single `File.Exists` at the standard installer path; spawning a process is overhead. Devs install via the official installer; non-standard installs require a plan update. | 2026-05-29 |
| Abort-and-leave-half-applied on rename failure (next trigger re-applies everything) | Half-applied state is observable by Claude Code between abort and next trigger. Roll back from the pre-apply backup so the dev's `~/.claude/` is always either fully on version N or fully on version N+1. | 2026-05-29 |
| Active-process probe before apply (check if Claude Code is running) | Replaced by user-facing "close Claude Code first" popup on every trigger (carried from upstream plan) | 2026-05-29 |
| Restore-from-backup UX in the app | `~/.claude/backups/` is the documented manual escape hatch (carried from upstream plan) | 2026-05-29 |
| New `Nexus.AiConfig` project for the apply service | Single new domain doesn't justify a project; reuse `Nexus.Sync` | 2026-05-29 |

---

## Plan Overview

1. **Claude Code install probe** — single file-existence check, cached per session; block + alert on miss.
2. **API client extensions** — `NexusApiClient.GetAiConfigManifest()` + `DownloadAiConfigFile()` over existing Sanctum bearer.
3. **Domain models + path rules** — `AiConfigManifest`, `AiConfigManifestFile`, `AiConfigApplyResult`; `AiConfigPaths` constants (root, backups, exclusion list, version marker).
4. **Apply service** — backup, download, SHA-verify, two-phase write/rename, rollback-on-failure, perfect-fit delete, version persist.
5. **UI + triggers** — `AiConfigUpdatePromptWindow` popup, MainWindow button, login hook, piggy-backed 4h timer.
6. **Docs + version** — structure.md, README, CHANGELOG, csproj minor bump.
7. **Close-out** — verification checklist.

---

## Steps

### Step 1: Claude Code install probe

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 1.1 | `ClaudeCodeInstallProbe` — static helper with `bool IsInstalled()`. Probe path: `Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData), "Programs", "claude", "claude.exe")`. Single `File.Exists` call — NOT cached; re-checked on every trigger. (`File.Exists` is sub-millisecond; caching adds invalidation complexity for a non-existent perf gain.) | dev | CREATE | `src/Nexus.Core/Services/ClaudeCodeInstallProbe.cs` | Done |
| 1.2 | When AI-config trigger fires and probe returns false: log to ActivityLog, surface non-blocking toast/alert on MainWindow ("Claude Code not detected — install required for AI config sync"). Do NOT show the update popup. Do NOT block login or transcript sync. | dev | MODIFY | `src/Nexus.App/App.xaml.cs` | Done |

### Step 2: API client extensions

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 2.1 | Add `Task<ApiResult<AiConfigManifest>> GetAiConfigManifest()` to `NexusApiClient` — `GET /api/v1/ai-config/manifest`. Use per-call `CancellationTokenSource(TimeSpan.FromSeconds(30))` (do NOT change the shared `HttpClient.Timeout` — follows the existing `Login()` pattern). 200 → deserialize with `JsonSerializerOptions { PropertyNameCaseInsensitive = true }` (the server emits snake_case keys: `version`, `generated_at`, `files[].path/sha/size`). 404 → return `Success=false, StatusCode=404` (caller treats as "no snapshot"). Other errors → `ApiResult.Fail`. | dev | MODIFY | `src/Nexus.Core/Services/NexusApiClient.cs` | Done |
| 2.2 | Add `Task<ApiResult> DownloadAiConfigFile(string path, Stream destination)` to `NexusApiClient` — `GET /api/v1/ai-config/file?path={path}`. Use per-call `CancellationTokenSource(TimeSpan.FromSeconds(300))` (do NOT change the shared `HttpClient.Timeout` — follows the existing `UploadTranscript()` pattern). Streamed copy via `HttpCompletionOption.ResponseHeadersRead` + `CopyToAsync(destination)`. Do NOT buffer entire body in memory. | dev | MODIFY | `src/Nexus.Core/Services/NexusApiClient.cs` | Done |
| 2.3 | Generic `ApiResult<T>` variant if not already present (current `ApiResult` only carries `Message` string) | dev | MODIFY | `src/Nexus.Core/Models/ApiResult.cs` | Done |

### Step 3: Domain models + path rules

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 3.1 | `AiConfigManifest` record — `string Version, DateTime GeneratedAt, IReadOnlyList<AiConfigManifestFile> Files`. Matches the JSON shape from `/api/v1/ai-config/manifest`. **File moved to `Nexus.Core/Models/` — `NexusApiClient` lives in `Nexus.Core` and `Nexus.Core` cannot reference `Nexus.Sync` (circular dep).** | dev | CREATE | `src/Nexus.Core/Models/AiConfigManifest.cs` | Done |
| 3.2 | `AiConfigManifestFile` record — `string Path, string Sha, long Size`. **File moved to `Nexus.Core/Models/` — same circular dependency reason as 3.1.** | dev | CREATE | `src/Nexus.Core/Models/AiConfigManifestFile.cs` | Done |
| 3.3 | `AiConfigApplyResult` record — `bool Success, string Status ("applied"\|"rolled_back"\|"aborted"\|"skipped_no_snapshot"\|"skipped_no_install"), string? Version, string? Message` | dev | CREATE | `src/Nexus.Sync/Models/AiConfigApplyResult.cs` | Done |
| 3.4 | `AiConfigPaths` static helper — `ClaudeRoot` (`%USERPROFILE%/.claude/`), `BackupsRoot` (`%USERPROFILE%/.claude/backups/`), `VersionMarkerPath` (`%LOCALAPPDATA%/Nexus/ai-config-version` — machine-local to match `~/.claude/` semantics; does NOT roam), `ExclusionList` (the constants from Detailed Sub-step 3.4), `IsExcluded(string relativePath)` predicate. All paths use forward slashes for manifest comparison; convert at filesystem boundary. Lives under `Models/` because it is pure constants + a predicate, not a service. | dev | CREATE | `src/Nexus.Sync/Models/AiConfigPaths.cs` | Done |

### Step 4: Apply service

`AiConfigApplyService` orchestrates the full apply pipeline. Detailed sequencing in 4.2's detailed sub-step.

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 4.1 | Class shell + in-process lock — `private readonly object _lock = new(); private bool _running;`. Public `Task<AiConfigApplyResult> ApplyAsync(AiConfigManifest)` enters with `lock(_lock)` check-then-set (read+write of `_running` must be inside the SAME `lock` block); returns `Status="aborted", Message="already running"` if held. Cleared in `finally`. Concurrent triggers (login + manual + timer) see the lock and skip. **Scope note:** the in-process lock protects only this process; the single-instance `Mutex` protects against a second `Nexus.App` instance. Neither protects against Claude Code (or any external tool) racing reads/writes on `~/.claude/` — the "close Claude Code first" popup IS the entire cross-process serialization story. | dev | CREATE | `src/Nexus.Sync/Services/AiConfigApplyService.cs` | Done |
| 4.2 | Apply pipeline — backup → download-to-temp → SHA-verify-all → write `.tmp` → rename loop with success tracking → on rename failure invoke rollback (4.3) → on full success perfect-fit-delete + version persist. All steps log to ActivityLog. Detailed sequencing in 4.2 sub-step below. | dev | MODIFY | `src/Nexus.Sync/Services/AiConfigApplyService.cs` | Done |
| 4.3 | Rollback-on-failure — reverse-walk the success-list, for each `path` already renamed: copy `backups/{ts}/{path}` to `~/.claude/{path}.rollback.tmp` (fsync), then `File.Move(..rollback.tmp → path, overwrite: true)`. If any rollback step itself fails (rare — backup file also locked), STOP rollback and surface critical alert with backup path. Delete remaining unprocessed `.tmp` files. | dev | MODIFY | `src/Nexus.Sync/Services/AiConfigApplyService.cs` | Done |
| 4.4 | Pre-apply backup helper — cap-on-take: list subdirs of `backups/`, sort by name (ISO format sorts chronologically), delete oldest until 4 remain. Then `Directory.CreateDirectory(backups/YYYY-MM-DD_HHmmss)`. Recursive copy of `~/.claude/` excluding `IsExcluded(path)`. | dev | MODIFY | `src/Nexus.Sync/Services/AiConfigApplyService.cs` | Done |
| 4.5 | Perfect-fit delete — walk `~/.claude/`, for every relative path not in `manifest.Files` and not `IsExcluded(...)`: delete. Prune empty directories bottom-up. | dev | MODIFY | `src/Nexus.Sync/Services/AiConfigApplyService.cs` | Done |
| 4.6 | Version marker — read/write text file at `AiConfigPaths.VersionMarkerPath`. Compare in the trigger handler (`App.xaml.cs`), not in the apply service. Written ONLY after 4.5 succeeds. | dev | MODIFY | `src/Nexus.Sync/Services/AiConfigApplyService.cs` | Done |
| 4.7 | Orphan `.tmp` cleanup — on app startup (in `App.OnStartup` before any sync triggers), walk `~/.claude/` and delete any `*.tmp` or `*.rollback.tmp` files. Guards against process kill mid-apply. | dev | MODIFY | `src/Nexus.App/App.xaml.cs` | Done |

### Step 5: UI + triggers

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 5.1 | `AiConfigUpdatePromptWindow.xaml` — modal dialog, title "AI Config Update Available", body "vX → vY. Close Claude Code sessions before applying — files in `~/.claude/` will be replaced." Buttons: `btnApplyNow`, `btnLater`. Returns `bool? DialogResult`. Window attributes: `WindowStartupLocation="CenterScreen"`, `ShowInTaskbar="True"`, `Topmost="True"` (so it surfaces over other apps; tray-app has no parent window during timer/login moments). Code-behind calls `Activate()` after `Show`/`ShowDialog` invocation to force foreground. Controls use Hungarian prefixes per C# conventions. | dev | CREATE | `src/Nexus.App/AiConfigUpdatePromptWindow.xaml` | Done |
| 5.2 | `AiConfigUpdatePromptWindow.xaml.cs` — constructor takes `(string currentVersion, string newVersion)`. Button handlers set `DialogResult` and close. | dev | CREATE | `src/Nexus.App/AiConfigUpdatePromptWindow.xaml.cs` | Done |
| 5.3 | `App.TriggerAiConfigCheck()` shared entry — used by all three triggers. Probe install → return if not installed. Call `GetAiConfigManifest()`. 404 → return silently. Compare against marker → return if equal. Show popup. On Apply → call `AiConfigApplyService.ApplyAsync()`, log result, refresh `MainWindow`. | dev | MODIFY | `src/Nexus.App/App.xaml.cs` | Done |
| 5.4 | Login hook — in `OnLoginSucceeded`, after the existing `TriggerUpdateCheck()` call (let the Velopack popup, if any, surface and be dismissed first), `await TriggerAiConfigCheck()`. Sequencing prevents two modal dialogs from competing at login. The existing `OnLoginSucceeded` is a synchronous handler — convert to `async void` (event-handler exception) to allow awaiting. | dev | MODIFY | `src/Nexus.App/App.xaml.cs` | Done |
| 5.5 | 4h timer piggy-back — the existing Tick lambda `(_, _) => TriggerUpdateCheck()` is an expression-bodied lambda; convert it to a block lambda that runs Velopack first then `TriggerAiConfigCheck()` via `Dispatcher.InvokeAsync` (Tick fires on the UI thread, so direct call works too, but the AI-config method must stay on the dispatcher because it shows `ShowDialog()`). Single timer, two checks, sequenced. | dev | MODIFY | `src/Nexus.App/App.xaml.cs` | Done |
| 5.6 | MainWindow button — add `btnCheckAiConfig` ("Check AI Config") between `btnCheckUpdates` and `btnLogout`. Click handler calls `_app.TriggerAiConfigCheck()`. | dev | MODIFY | `src/Nexus.App/MainWindow.xaml`, `src/Nexus.App/MainWindow.xaml.cs` | Done |
| 5.7 | Wire `AiConfigApplyService` into `App.xaml.cs` constructor alongside `SyncEngine`. | dev | MODIFY | `src/Nexus.App/App.xaml.cs` | Done |

### Step 6: Docs + version

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 6.1 | Add `AiConfigApplyService`, `AiConfigPaths`, `ClaudeCodeInstallProbe`, `AiConfigUpdatePromptWindow`, the three Models, and the `%LOCALAPPDATA%/Nexus/ai-config-version` marker to structure.md | doc-writer | MODIFY | `project_docs/structure.md` | Done |
| 6.2 | README — new "AI Config Sync" section documenting the feature, the popup, the trigger schedule (login + manual + 4h), the version marker, and the manual escape hatch (`~/.claude/backups/{ts}/`) | doc-writer | MODIFY | `README.md` | Done |
| 6.3 | CHANGELOG entry (MINOR bump per versioning-conventions.md — exact target version coordinated with `plan-modernize-ui.md`; whichever ships first takes 1.2.0). Update `<Version>` in csproj at execution time. | dev | MODIFY | `CHANGELOG.md`, `src/Nexus.App/Nexus.App.csproj` | Done |

### Step 7 (final — always last): Close-out

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 7.1 | Propose close-out checklist per Rule 21 (chat only — not written to plan) | tech-lead | RUN | — | Done |

---

## Detailed Sub-steps

### 3.4 Path rules

`AiConfigPaths.ExclusionList` — hard-coded constant, matched against the relative path under `~/.claude/`. Same list as the upstream plan:

```
projects/, settings.local.json, .credentials*, mcp-needs-auth-cache.json,
backups/, cache/, sessions/, plans/, plugins/, telemetry/, todos/,
*.log, shell-snapshots/, statsig/, ide/, paste-cache/, debug/,
file-history/, session-env/, *.tmp, *.rollback.tmp
```

`*.tmp` and `*.rollback.tmp` are in the exclusion list so the orphan cleanup (4.7) and the rename intermediates never get caught by perfect-fit delete.

Match semantics: prefix match for directory entries (`projects/` matches anything under it), exact match for file entries, `fnmatch`-style glob for `*` patterns.

### 4.2 Apply pipeline — strict sequence

```
1. acquire lock (else return aborted "already running")
2. backupDir = AiConfigPaths.BackupsRoot / now-utc-iso
3. backup pass:
     a. enforce cap-on-take (delete oldest backups until 4 remain)
     b. recursive copy ~/.claude → backupDir, skip IsExcluded(...)
4. tempDir = %TEMP%/nexus-ai-config-{ts}
5. for each f in manifest.Files:
     download via NexusApiClient.DownloadAiConfigFile(f.Path, fileStream(tempDir/f.Path))
     on any HTTP failure → ABORT (no writes yet, no rollback needed)
6. for each f in manifest.Files:
     compute SHA-256 of tempDir/f.Path
     if != f.Sha → ABORT (no writes yet)
7. write phase — for each f in manifest.Files:
     copy tempDir/f.Path to ~/.claude/{f.Path}.tmp, fsync
     on any IO failure → delete already-written .tmp files, ABORT
8. rename phase — successList = []
     for each f in manifest.Files (ordered):
       try File.Move(~/.claude/{f.Path}.tmp, ~/.claude/{f.Path}, overwrite: true)
       on success → successList.Add(f.Path)
       on failure → invoke ROLLBACK (Step 4.3) over successList,
                    delete remaining .tmp files, return rolled_back
9. perfect-fit delete (Step 4.5)
10. write version marker (Step 4.6)
11. return applied
12. finally: drop lock, delete tempDir
```

The version marker advances ONLY at step 10. Any abort or rollback leaves it on the prior version, so the next trigger retries from scratch.

### 4.3 Rollback semantics

On rename failure mid-loop:

1. `successList` holds the relative paths already renamed (now on new content).
2. Reverse-iterate `successList`. For each `relPath`:
   - `src = backupDir / relPath`
   - `tmp = ~/.claude/{relPath}.rollback.tmp`
   - `File.Copy(src, tmp); fsync(tmp); File.Move(tmp, ~/.claude/{relPath}, overwrite: true)`
3. If step 2 ever throws: STOP rollback. Log critical. Toast: "Apply failed AND rollback failed. Backup at {backupDir}. Restore manually." Do not continue iterating — partial-rollback is worse than the half-applied state we tried to fix.
4. Delete any unprocessed `.tmp` files in `~/.claude/`.
5. Leave version marker unchanged.
6. Return `AiConfigApplyResult { Status = "rolled_back", Message = "<rename failure cause> — restored previous version. Close Claude Code and retry." }`.

The "rollback also fails" branch is intentionally non-recoverable: the dev gets an explicit alert with the backup path. Auto-retrying a rollback that just failed risks compounding the corruption.

### 5.3 TriggerAiConfigCheck flow

`TriggerAiConfigCheck` MUST be invoked on the UI dispatcher (it calls `ShowDialog()` and touches `MainWindow` UI controls via `RefreshState()`). Callers running off-thread (the 4h timer's `Task.Run` continuation) must marshal via `Dispatcher.Invoke` / `Dispatcher.InvokeAsync`. The `_aiConfigCheckInFlight` flag below short-circuits concurrent triggers BEFORE the popup is shown — without it, login + manual + timer could each stack their own modal dialog (the apply-service's in-process lock only catches duplicates AFTER both popups are dismissed).

```csharp
private bool _aiConfigCheckInFlight;

public async Task TriggerAiConfigCheck() {
    if ( _aiConfigCheckInFlight ) return;          // popup-stacking guard
    if ( ! _config.IsLoggedIn ) return;
    if ( ! ClaudeCodeInstallProbe.IsInstalled() ) {
        _activity.Log("ai_config_check", "Claude Code not installed — sync skipped", "warning");
        return;
    }
    _aiConfigCheckInFlight = true;
    try {
        var result = await _api.GetAiConfigManifest();
        if ( ! result.Success ) {
            if ( result.StatusCode == 404 ) return;  // no snapshot published yet
            _activity.Log("ai_config_check", $"manifest fetch failed: {result.Message}", "error");
            return;
        }
        var manifest = result.Data!;
        var current = File.Exists(AiConfigPaths.VersionMarkerPath)
            ? File.ReadAllText(AiConfigPaths.VersionMarkerPath).Trim()
            : "";
        if ( current == manifest.Version ) return;
        var prompt = new AiConfigUpdatePromptWindow(current, manifest.Version);
        if ( prompt.ShowDialog() != true ) return;
        var apply = await _applyService.ApplyAsync(manifest);
        _activity.Log("ai_config_apply", $"{apply.Status}: {apply.Message}", apply.Success ? "ok" : "error");
        _mainWindow?.RefreshState();
    } finally {
        _aiConfigCheckInFlight = false;
    }
}
```

All three triggers (login, manual button, 4h timer) call this single method. The `_aiConfigCheckInFlight` flag prevents popup stacking; the apply-service's in-process lock is a second line of defense for any case the flag misses.

---

## File Summary

### Created Files

| File | Purpose |
|------|---------|
| `src/Nexus.Core/Services/ClaudeCodeInstallProbe.cs` | Cached `File.Exists` probe at the standard installer path |
| `src/Nexus.Core/Models/AiConfigManifest.cs` | Manifest DTO (placed in Core — referenced by NexusApiClient) |
| `src/Nexus.Core/Models/AiConfigManifestFile.cs` | Manifest entry DTO (placed in Core — same reason) |
| `src/Nexus.Sync/Models/AiConfigApplyResult.cs` | Apply outcome DTO |
| `src/Nexus.Sync/Models/AiConfigPaths.cs` | Root, backups, marker, exclusion list, `IsExcluded()` |
| `src/Nexus.Sync/Services/AiConfigApplyService.cs` | Orchestrator — backup, download, verify, write, rename, rollback, finalize |
| `src/Nexus.App/AiConfigUpdatePromptWindow.xaml` | Shared popup |
| `src/Nexus.App/AiConfigUpdatePromptWindow.xaml.cs` | Popup code-behind |

### Modified Files

| File | Changes |
|------|---------|
| `src/Nexus.Core/Models/ApiResult.cs` | Add generic `ApiResult<T>` carrying typed `Data` |
| `src/Nexus.Core/Services/NexusApiClient.cs` | `GetAiConfigManifest()` + `DownloadAiConfigFile(path, stream)` (streamed) |
| `src/Nexus.App/App.xaml.cs` | Wire `AiConfigApplyService`; add `TriggerAiConfigCheck()`; orphan `.tmp` cleanup at startup; login hook; piggy-back 4h timer |
| `src/Nexus.App/MainWindow.xaml` | Add `btnCheckAiConfig` button |
| `src/Nexus.App/MainWindow.xaml.cs` | Wire click handler → `_app.TriggerAiConfigCheck()` |
| `project_docs/structure.md` | Document new services, models, popup window, version marker |
| `README.md` | New "AI Config Sync" section + escape hatch |
| `CHANGELOG.md` | MINOR feature entry (target version: 1.2.0 unless `plan-modernize-ui.md` ships first) |
| `src/Nexus.App/Nexus.App.csproj` | Version bump |

---

## Verification

1. Launch app on a machine without Claude Code installed → login + transcript sync continue working; AI config trigger logs "not installed" warning, no popup.
2. Install Claude Code at the standard path; restart app, log in → after login, manifest fetched; if version differs from marker, popup appears. Click Later → popup dismisses; no retry until next 4h tick. Click Apply → progress, then toast "applied vX".
3. Verify `~/.claude/` mirrors the snapshot exactly — every manifest file present, every non-manifest non-excluded file deleted, exclusion items (e.g., `projects/`, `settings.local.json`) untouched.
4. Verify `~/.claude/backups/{ts}/` contains the pre-apply state.
5. Trigger 6 more applies in a row (e.g., manual button after each push from PM). Confirm only 5 most recent backup directories remain.
6. Click manual button while another apply is running (login-time + timer fired simultaneously). Confirm the second call returns `aborted: already running` and does not interfere.
7. Simulate SHA mismatch (toggle a test flag or have the test harness truncate one download). Confirm: apply aborts before any local write to `~/.claude/`, version marker unchanged, AND the pre-apply backup directory exists post-abort (backup happens in Step 3 — before download/verify — so it is taken regardless of whether the apply ultimately succeeds).
8. Simulate rename failure mid-loop (test seam — throw on the Nth `File.Move`). Confirm rollback restores all previously renamed files from the backup, `.tmp` files are cleaned, version marker unchanged, toast shows "rolled_back".
9. Simulate rollback failure (throw during the rollback restore). Confirm critical alert with backup path, no further automatic retry.
10. Kill the app process mid-write (Task Manager during the `.tmp` write phase). Restart app → orphan `.tmp` cleanup deletes them; next trigger re-applies cleanly.
11. PM removes an item from `~/.claude/` source, re-runs `/sync-claude` + `/push-dev`. Dev triggers manual sync → confirm item is deleted from `~/.claude/`. Confirm `settings.local.json`, `projects/`, and other exclusion items remain.
12. Manifest endpoint returns 404 (no snapshot ever published) → no popup, no error toast, no log entry at error severity.
13. Token revoked server-side; trigger fires → manifest fetch returns 401; logged as error; no popup; no apply.
14. Confirm `%LOCALAPPDATA%\Nexus\ai-config-version` advances only on successful applies, never on aborts or rollbacks.

---

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Apply overwrites `~/.claude/` while Claude Code is actively reading it | Popup warns dev to close Claude Code on every trigger. SHA-verify-all before any write. Two-phase write (`.tmp` + atomic `File.Move`) keeps each individual file atomic. Rollback-on-failure restores prior state from backup if rename loop fails partway. |
| Rename failure leaves `~/.claude/` half-applied | Rollback (4.3) reverse-walks the success list and restores each affected file from the pre-apply backup. End-state guarantee: always fully on version N or fully on version N+1. |
| Rollback itself fails (backup file also locked) | Stop further rollback to avoid compounding corruption. Critical alert + backup path. Manual recovery is the documented escape hatch. |
| Orphan `.tmp` from process kill mid-write | Startup cleanup walks `~/.claude/` and deletes `*.tmp` / `*.rollback.tmp` before any trigger fires. Exclusion list keeps perfect-fit delete from touching them either. |
| Overlapping triggers (login + manual + timer) | In-process lock in `AiConfigApplyService` rejects concurrent calls. Single-instance Mutex (already in `App.xaml.cs`) prevents a second app instance. |
| Manifest fetched but disk free space insufficient for backup + download + write | Accepted: feature is re-runnable. Disk-full errors surface as IO exceptions during backup/write phases, abort the apply with a toast, leave `~/.claude/` on version N. Dev frees space and re-triggers. No preflight check — manifest is ~10MB of markdown; preflight is premature complexity. |
| Backup retention bug accidentally deletes the most recent backup | Cap-on-take always deletes oldest first by ISO-name sort; new backup is created AFTER pruning. Post-create count is exactly 5 max. |
| Empty manifest wipes everything outside exclusion list | Source-side `/sync-claude` empty-manifest guard requires explicit `--allow-empty` flag (upstream plan Step 1.1). Client trusts the manifest. |
| `settings.json` overwrite removes dev's personal hooks/permissions | Intentional — PM is single source of truth for settings. Backup-before-apply gives recovery via manual file copy. |
| Probe path drift if Claude Code installer changes | Pinned to `%LOCALAPPDATA%\Programs\claude\claude.exe`. Deviation = plan update. |
| `~/.claude/backups/` namespace collides with future Claude Code use of the same path | Documented; revisit if Claude Code adopts `backups/`. Currently no known conflict. |
| 401 mid-pass mid-download leaves `temp/` files | `temp/` is deleted in `finally`. No effect on `~/.claude/`. Next trigger restarts from scratch. |

---

## Notes

- **Cross-project context:** This plan is Phase 3 of `c:/xampp/htdocs/nexus/project_docs/plans/plan-ai-config-distribution.md`. Phases 1, 2, 4 are owned by that plan. Phase 2 is Done; Phase 1 is TODO and must be done by PM before this client sees a real manifest.
- **Supersedes upstream Step 6.5 wording:** The upstream plan's Step 6.5 still describes the older "abort: leave remaining `.tmp` files in place" semantics for rename failures. This plan's Step 4.3 (rollback-on-failure restoring from the pre-apply backup) is the authoritative client behavior — see the Decisions table row "Abort-and-leave-half-applied on rename failure" for the rationale. Update upstream Step 6.5 wording during a follow-up sync.
- **Version coordination:** Both this plan and `plan-modernize-ui.md` target a MINOR bump from 1.1.0. Whichever ships first becomes 1.2.0; the other becomes 1.3.0. Resolve at execution time.
- **No new database, no morph map allocation.** Pure client-side feature.
- **Out of scope (parked):** `keybindings.json` distribution, in-app restore-from-backup UX, dev self-service file selection, active-process probe before apply, multi-installer-path probe.

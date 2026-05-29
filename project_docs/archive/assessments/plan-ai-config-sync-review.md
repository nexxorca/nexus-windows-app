<!-- plan-ai-config-sync-review.md | c:/xampp/htdocs/nexus-windows-app/project_docs/assessments/plan-ai-config-sync-review.md -->

> Author: Erik | Agent: tech-lead | Created: 2026-05-29 | Status: Complete (all findings FIXED or WONTFIX)

# AI Config Sync (Client) — Plan Review (2026-05-29)

**Plan File:** `project_docs/plans/plan-ai-config-sync.md`
**Upstream Plan (context):** `c:/xampp/htdocs/nexus/project_docs/plans/plan-ai-config-distribution.md` (this plan is Phase 3 of that pipeline)
**Status:** Proposed — issues below should be resolved before execution.
**Methodology:** MED tier, A/B comparison, 2026-05-29. Two architect passes (A + B) + two code-reviewer fact-checker passes (A + B), independent runs, then merged. Agreement rate ≈ 25% (6 of 24 findings flagged by both A and B; the rest are A-only or B-only, expected for a complex multi-trigger pipeline where each agent angled at different failure modes). No formal simplicity-gate pass — all recommended fixes are plan-text changes (specifications, added steps, validation hooks), not new code abstractions.

---

## High Issues (8)

| # | Issue | Location | Confidence | Status |
|---|-------|----------|------------|--------|
| 1 | `AiConfigVersionStore.Read()` is called in the sub-step 5.3 code sample, but no Step creates this class. Step 4.6 says version persistence lives in `AiConfigApplyService.cs` and the path is `AiConfigPaths.VersionMarkerPath`. Compile blocker as-written. Fix: either add an `AiConfigVersionStore` static helper to Step 3 or 4, or rewrite the sample to read `AiConfigPaths.VersionMarkerPath` directly. | Sub-step 5.3, Step 4.6 | Both agree (FC pair) | FIXED — sample now reads `AiConfigPaths.VersionMarkerPath` inline via `File.Exists`/`File.ReadAllText` |
| 2 | **Stacked-popup race.** The in-process lock lives in `AiConfigApplyService.ApplyAsync`, NOT in `TriggerAiConfigCheck`. The popup (`prompt.ShowDialog()`) is shown BEFORE `ApplyAsync` is called. While a modal popup is open, WPF's nested message loop still allows `DispatcherTimer.Tick` to fire AND the manual MainWindow button to be clicked — each opens another `AiConfigUpdatePromptWindow`. The lock catches the second `ApplyAsync` after both popups are dismissed, but the dev sees stacked modal dialogs. Fix: add a `_promptOpen` / `_triggerInFlight` gate at the top of `TriggerAiConfigCheck` so concurrent calls short-circuit before the popup. | Sub-step 5.3, Steps 5.4/5.5/5.6 | Both agree (Arch pair) | FIXED — sub-step 5.3 now has `_aiConfigCheckInFlight` flag short-circuiting before the popup |
| 3 | **Power-loss / hard-kill mid-rename violates invariant #1.** | Architecture invariant #1, Sub-step 4.2 step 8 | A-only | WONTFIX — accepted; feature is re-runnable, orphan `.tmp` cleanup + next-trigger re-apply handles it. Documented in plan's Accepted Risks block. |
| 4 | **Modal popup + tray Exit + `OnExplicitShutdown`.** | Architecture, Step 5.1 | A-only | WONTFIX — accepted; same re-runnable rationale as #3. Documented in Accepted Risks block. |
| 5 | **Write-phase cleanup may itself fail.** | Sub-step 4.2 step 7 | B-only | WONTFIX — accepted; orphan `.tmp` cleanup (Step 4.7) + exclusion list cover any leftover. Documented in Accepted Risks block. |
| 6 | **Rollback leaves stale file at the old path for newly-added manifest entries.** | Step 4.3, Sub-step 4.2 step 8 | B-only | WONTFIX — accepted; next-trigger re-apply overwrites or perfect-fit-deletes any stray file. Documented in Accepted Risks block. |
| 7 | **No client-side manifest path validation.** | Steps 4.2/4.5, new pre-step needed | B-only | WONTFIX — accepted; `/sync-claude` is PM-controlled and server validates. Documented in Accepted Risks block. |
| 8 | **HttpClient timeout mismatch with shared-client pattern.** `NexusApiClient` constructs a single `HttpClient` with `Timeout = Timeout.InfiniteTimeSpan` (NexusApiClient.cs:19-21). Per-call timeouts are implemented via `CancellationTokenSource` (Login: 30s CTS, UploadTranscript: 300s CTS). The plan's "30s timeout" / "300s timeout" wording in Steps 2.1/2.2 is achievable via per-call CTS following the existing pattern — but a dev reading the plan literally might add a second `HttpClient` or change the global `Timeout`, breaking all other calls. Fix: plan should explicitly say "use `CancellationTokenSource(TimeSpan.FromSeconds(30))` and `(300)` per-call, following the existing Login/UploadTranscript pattern". | Steps 2.1, 2.2 | B-only (FC pair) | FIXED — Steps 2.1/2.2 now pin CTS pattern + reference existing `Login()` / `UploadTranscript()` calls |

## Medium Issues (11)

| # | Issue | Location | Confidence | Status |
|---|-------|----------|------------|--------|
| 9 | **Manifest JSON field casing.** Server returns the manifest by `json_decode($contents, true)` then `response()->json($decoded)` — i.e., pass-through of whatever `/sync-claude` wrote. Upstream plan's example manifest uses snake_case (`generated_at`, `files[].path`/`sha`/`size`). The plan's C# records (`GeneratedAt`, `Sha`, `Size`) are PascalCase WITHOUT `[JsonPropertyName]` attributes. `NexusApiClient._jsonOptions = new()` (NexusApiClient.cs:15) — no `PropertyNameCaseInsensitive = true`. Result: `GeneratedAt`, `Sha`, `Size`, `Path` silently deserialize to null/0 on the actual snake_case payload. Fix: either set `PropertyNameCaseInsensitive = true` on a JsonSerializerOptions used for ai-config calls, OR add `[JsonPropertyName("generated_at")]` / `("path")` / `("sha")` / `("size")` attributes to the records in Step 3. | Steps 3.1, 3.2 | Both agree (FC pair) | FIXED — Step 2.1 now pins `JsonSerializerOptions { PropertyNameCaseInsensitive = true }` for ai-config deserialization |
| 10 | **UI-thread requirement for `TriggerAiConfigCheck` clashes with timer's `Task.Run` pattern.** | Sub-step 5.3, Step 5.5 | A-only | FIXED — sub-step 5.3 pins UI-dispatcher requirement; Step 5.5 converts Tick to block lambda; Step 5.4 converts `OnLoginSucceeded` to `async void` |
| 11 | **In-process lock provides zero cross-process protection.** | Step 4.1, Risks table | A-only | FIXED — Step 4.1 now includes a "Scope note" stating the popup is the cross-process serialization story |
| 12 | **Backup recursive-copy mid-way failure is unspecified.** | Step 4.4, Sub-step 4.2 step 3 | A-only | WONTFIX — accepted in Accepted Risks block; partial backup left for inspection, apply aborts |
| 13 | **Login hook may surface two modal dialogs concurrently.** | Step 5.4 | A-only | FIXED — Step 5.4 now sequences (awaits Velopack popup first, then AI-config popup); popup-stacking flag (#2) is the second line of defense |
| 14 | **Perfect-fit delete is "unknown unknowns"-unsafe.** | Step 4.5, Sub-step 3.4 | B-only | WONTFIX — accepted in Accepted Risks block; exclusion list is updated reactively as Claude Code evolves |
| 15 | **Modal popup has no `Owner` and no foreground-activation hints.** | Step 5.1, Sub-step 5.3 | B-only | FIXED — Step 5.1 now specifies `CenterScreen`, `Topmost=true`, `ShowInTaskbar=true`, `Activate()` |
| 16 | **Install probe staleness.** | Step 1.1, Sub-step 5.3 | B-only | FIXED — Step 1.1 now re-probes every trigger (caching removed) |
| 17 | **No total-pipeline timeout / no sleep-resume detection.** | Step 2.2, Step 4.2 | B-only | WONTFIX — accepted in Accepted Risks block; sleep-resume + hung apply surface as next-trigger retry |
| 18 | **Velopack restart can interrupt an in-progress apply.** | Step 5.5, Risks table | B-only | WONTFIX — accepted in Accepted Risks block; orphan-`.tmp` cleanup + next-trigger re-apply handles it |
| 19 | **Version marker in `%APPDATA%` (roaming) can desync across machines.** | Sub-step 3.4 | B-only | FIXED — marker location changed to `%LOCALAPPDATA%/Nexus/ai-config-version` (machine-local) |

## Low Issues (5)

| # | Issue | Location | Confidence | Status |
|---|-------|----------|------------|--------|
| 20 | Verification step 7 contains an unresolved authoring question: `"no backup taken? — backup IS taken (Step 3 runs before Step 4); confirm backup directory exists post-abort"`. Reads as an in-line self-Q&A left in the document. Clean to a normal assertion. | Verification step 7 | A-only | FIXED — Verification step 7 rewritten as a clean assertion |
| 21 | **Disk free-space preflight has no Step.** | Steps 4.1–4.7, Risks table | Both agree (Arch pair + FC-B) | FIXED — preflight dropped entirely; Risks row rewritten as accepted (re-runnable on disk-full IO exception) |
| 22 | `AiConfigPaths` is a pure constants helper with `IsExcluded()` predicate. | Step 3.4 | B-only | FIXED — Step 3.4 and File Summary moved to `src/Nexus.Sync/Models/AiConfigPaths.cs` |
| 23 | **Backup-vs-perfect-fit asymmetry for `IsExcluded` paths.** | Steps 4.4, 4.5 | B-only | WONTFIX — accepted; trusted manifest writer (PM-controlled) won't emit excluded paths |
| 24 | **Upstream plan Step 6.5 wording is stale.** Upstream `plan-ai-config-distribution.md` Step 6.5 still describes "abort: leave remaining `.tmp` files in place" semantics — predates the rollback-on-failure decision recorded in this plan's Decisions table. Not a defect in THIS plan, but a cross-plan inconsistency. Fix: update the upstream plan's Step 6.5 language during a follow-up sync, OR add a note here that this plan supersedes that wording for the client. | (cross-plan) | A-only (FC-A) | FIXED — Notes section now states this plan supersedes upstream Step 6.5 wording (upstream still needs follow-up sync) |

---

## Verified Non-Issues (codebase facts the plan got right)

These were verified against the actual source files and DO NOT need to change. Documented so the dev doesn't re-check them.

1. `ApiResult` has `Success`, `StatusCode`, `Message`, `IsAuthError`, `IsValidationError` exactly as the plan's code samples assume — `ApiResult.cs:1-26`.
2. `ApiResult<T>` generic does NOT yet exist — Step 2.3 correctly adds it.
3. `AppConfig.IsLoggedIn => ! string.IsNullOrEmpty(AuthToken)` — `AppConfig.cs:15`. Plan's `_config.IsLoggedIn` guard works.
4. `ActivityLogService.Log(string type, string description, string status = "ok")` exists with the exact signature the plan uses — `ActivityLogService.cs:29`.
5. `MainWindow.RefreshState()` exists — `MainWindow.xaml.cs:37`.
6. `LoginWindow.LoginSucceeded` event exists; `App.OnLoginSucceeded` subscribes — `LoginWindow.xaml.cs:15`, `App.xaml.cs:76,80-86`.
7. `btnCheckUpdates` and `btnLogout` exist at MainWindow.xaml:50-51; positioning claim in Step 5.6 is accurate.
8. Current version is `1.1.0`; target `1.2.0` MINOR bump is correct — `Nexus.App.csproj:20`, `CHANGELOG.md:3`.
9. Phase 2 of upstream plan is `Done`; Phase 1 is `TODO` — `nexus/project_docs/plans/plan-ai-config-distribution.md:103-117`.
10. 4h `DispatcherTimer` exists in `App.xaml.cs:91`; `_config`/`_api`/`_activity`/`_mainWindow`/`_syncEngine` fields all exist (`App.xaml.cs:13-23`).
11. Server endpoints `/api/v1/ai-config/manifest` and `/api/v1/ai-config/file` exist behind `auth:sanctum` (`nexus/routes/api.php:37-38`, `AiConfigController.php`).
12. Cap-on-take backup math ("delete oldest until 4 remain, then create new" → exactly 5 max) is correct.
13. `%USERPROFILE%`/`SpecialFolder.UserProfile` and `%APPDATA%`/`SpecialFolder.ApplicationData` usage is consistent with `AppConfig.cs:20-23,92-97`.
14. Spaces-inside-parentheses style in code samples is consistent with the existing codebase (`App.xaml.cs:34`, `NexusApiClient.cs:27`), even though `conventions.md` doesn't formally codify it for C#.

---

## Summary

Of 24 findings: **14 FIXED, 10 WONTFIX (accepted as re-runnable risks).** Plan ready to execute.

**Fixed (14):** #1 (`AiConfigVersionStore` → inline read), #2 (`_aiConfigCheckInFlight` popup gate), #8 (CTS pattern), #9 (`PropertyNameCaseInsensitive`), #10 (UI-dispatcher pinned), #11 (lock scope note), #13 (login sequencing), #15 (popup window hygiene), #16 (install probe re-checks every trigger), #19 (marker → `%LOCALAPPDATA%`), #20 (Verification step 7 cleanup), #21 (preflight dropped), #22 (`AiConfigPaths` → Models/), #24 (upstream-supersede note).

**Accepted (10) — "feature is re-runnable" rationale:** #3 power-loss mid-rename, #4 tray Exit during apply, #5 write-cleanup contract, #6 rollback for new paths, #7 manifest path validation, #12 backup mid-failure, #14 perfect-fit unknown files, #17 pipeline timeout, #18 Velopack restart, #23 backup/perfect-fit asymmetry. All documented in the plan's new Accepted Risks block above the Decisions table.

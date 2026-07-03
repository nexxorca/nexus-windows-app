<!-- plan-ai-sync-hardening.md | project_docs/plans/plan-ai-sync-hardening.md -->

> Author: PM | Agent: tech-lead | Created: 2026-06-08 | Status: Proposed

# AI Config Sync — Hardening Plan (wipe-and-replace)

## Context

`/assessment --med ai-sync-feature` (2026-06-08) produced 51 findings against the AI Config Sync subsystem (1 Critical, 11 High, 22 Medium, 17 Low; verdict 5/10 — capped by the outstanding Critical). The feature was re-enabled this turn by removing the disabled-flag early-return in `App.xaml.cs` and has never run end-to-end against the live server.

Rather than harden the current surgical apply pipeline (atomic per-file rename + rollback + perfect-fit delete + exclusion list), the PM ruled to **replace it with wipe-and-replace**:

- The manifest declares the directories it owns (`managed_roots`).
- On apply: wipe contents of each managed root → download and write the per-user selected files (manifest is already per-user via Sanctum bearer).
- No backups, no rollback, no atomicity invariant beyond "either it worked or click apply again."
- Policy: files under managed roots are server-managed. Devs cannot customize them — any edit is overwritten on the next apply. Local work belongs outside the managed roots (e.g., `~/.claude/projects/`).

This kills the entire "surgical apply" class of bugs (HIGH findings #5, #7, #8, MEDIUM #19, #29, #30 in the assessment) by deleting the code surface rather than patching it. The remaining hardening surface — what this plan covers — is everything orthogonal to the apply mechanism: path containment, symlinks, Claude-Code-running probe, HTTPS, exception handling at the trigger boundary, and tests.

The remaining trust-model gap (server compromise = arbitrary writes) is tracked in `project_docs/improvements.md` under "AI Config Manifest Signature" and is not addressed here.

Version: 1.2.1 → **1.2.2** (patch — security + simplification, manifest schema gets a new `managed_roots` field).

## Architecture / Approach

**Manifest schema** (server-side change; coordinated with Nexus):

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

`managed_roots` is the wipe scope. Each entry is a directory relative to `~/.claude/`. Files at the root (`CLAUDE.md`, `settings.json`) can be `managed_roots: ["CLAUDE.md", "settings.json"]` entries too (treat as single-file wipe = delete that file if present).

**Apply flow** (replaces all of current `AiConfigApplyService.ApplyAsync`):

```
1. Pre-flight gates (any failure → abort, log, return):
     a. Has any reparse point under ~/.claude/managed_roots? → abort
     b. Is claude.exe running? → abort with "close Claude Code and retry"
     c. Validate manifest paths: each file's path is rooted within a managed_root, no `..`, no rooted absolute path, no `:`, no control chars, no embedded `\`. → abort on first violation
     d. Validate managed_roots entries themselves: relative, no `..`, no absolute. → abort

2. Wipe phase:
     For each entry in manifest.managed_roots:
         If entry resolves to a directory: delete its contents recursively.
         If entry resolves to a file: delete the file.
     (Skip if path doesn't exist; idempotent.)

3. Write phase:
     For each file in manifest.files:
         Download to a temp file. Verify SHA-256 (corruption check, not trust anchor).
         Move into final location with parent dir created.

4. Finalize:
     Persist new fingerprint marker.
     Activity log entry: applied vN with N files in K managed roots.
```

No backups. No rollback. If anything in steps 2-3 throws, the dispatcher's outer try/catch logs it and the dev clicks "Check AI Config" to retry — the next apply re-wipes and re-downloads from scratch.

## Decisions / Rejected Options

| Option | Reason rejected | Ruling date |
|---|---|---|
| Per-file atomic rename + rollback (current design) | PM ruled wipe-and-replace simpler. Rollback machinery serves "preserve dev customizations on bad apply" — but devs cannot customize, by policy. | 2026-06-08 |
| Backup ladder (`~/.claude/backups/` last 5) | No backups under wipe-and-replace. Recovery is "click apply again." | 2026-06-08 |
| Perfect-fit delete with mass-deletion threshold | Deleted along with the surgical apply code. Wipe scope is now declared by manifest. | 2026-06-08 |
| `~/.claude/SYNC_BROKEN` sentinel on rollback failure | No rollback to fail. | 2026-06-08 |
| Hardcoded `SyncedRoots` list in the desktop binary | PM ruled "make it simple" — manifest declares its own `managed_roots`. | 2026-06-08 |
| Wrap apply in `Task.Run` to move off UI dispatcher | Apply is already async-awaited; file ops are fast metadata operations. | 2026-06-08 |
| Diff/preview UI before apply | Not needed — devs cannot customize, so there's no surprise loss to warn about. The policy banner in `structure.md` + the popup wording is sufficient. | 2026-06-08 |
| Local 24h cooldown per fingerprint | Fingerprint marker already accomplishes this. | 2026-06-08 |

## Plan Overview

1. **Manifest schema bump** — coordinate `managed_roots` field with Nexus server side. Client DTO update.
2. **Rewrite apply pipeline** — replace surgical apply with wipe-and-replace; delete backup/rollback/perfect-fit code.
3. **Path containment + manifest validation** — every write path verified within `managed_roots`; reject traversal/control/absolute paths at parse time.
4. **Symlink / junction pre-flight** — abort apply if any reparse point under managed roots.
5. **Claude-Code-running probe** — abort apply if `claude.exe` is running.
6. **Trigger boundary exception handling** — outer try/catch in `TriggerAiConfigCheck`, `Logout()` async-void fix.
7. **HTTPS enforcement** — reject non-HTTPS URLs in `NexusApiClient.Configure` and `LoginWindow`.
8. **Test coverage** — `INexusApiClient` interface + integration tests for the new apply pipeline + unit tests for path validation + extended `ManifestFingerprintTests`.
9. **Docs + version + release** — policy banner in `structure.md`, CHANGELOG entry, csproj bump, `release.sh 1.2.2`.
10. **Close-out** — propose close-out checklist per Rule 21.

## Steps

### Step 1: Manifest schema bump

Closes: enables Steps 2-3.

| # | Description | Agent | Action | Files | Status |
|---|---|---|---|---|---|
| 1.1 | Coordinate with Nexus server team: add `managed_roots: string[]` field to `/api/v1/ai-config/manifest` response. Must precede the `files` array. PM confirms server delivery before desktop work resumes past this step. | manual | RUN | — | TODO |
| 1.2 | Add `ManagedRoots` to the `AiConfigManifest` record. Maintain JSON property name `managed_roots`. | dev | MODIFY | `src/Nexus.Core/Models/AiConfigManifest.cs` | Done |
| 1.3 | Manifest deserialization tolerates a missing `managed_roots` field for one release: if missing, treat as empty array AND log warning (server-side rollout safety net). Remove the fallback in 1.3.0. | dev | MODIFY | `src/Nexus.Core/Services/NexusApiClient.cs` | Done |

### Step 2: Rewrite apply pipeline (wipe-and-replace)

Closes: HIGH #5 (rollback invariant), HIGH #7 (backup ordering), HIGH #8 (perfect-fit guard), MEDIUM #19 (SRP), MEDIUM #29 (backup integrity), MEDIUM #30 (backup ACL). These findings disappear because the code surfaces they describe are deleted.

| # | Description | Agent | Action | Files | Status |
|---|---|---|---|---|---|
| 2.1 | Replace `AiConfigApplyService.ApplyAsync` body with the four-phase flow described in Architecture / Approach (pre-flight → wipe → write → finalize). Drop: `BackupClaudeRoot`, `EnforceBackupRetention`, `RollbackAsync`, `PerfectFitDelete`, the `successList` tracking, and the `~/.claude/backups/` retention logic entirely. | dev | MODIFY | `src/Nexus.Sync/Services/AiConfigApplyService.cs` | Done |
| 2.2 | Drop the exclusion list and related helpers — no longer needed under the inclusion model (`managed_roots` IS the scope). Keep `AiConfigPaths.ClaudeRoot`, `BackupsRoot` (for orphan cleanup of legacy backups on first 1.2.2 run), `FingerprintPath`. Delete `IsExcluded`, `ExclusionList`, `VersionMarkerPath`. | dev | MODIFY | `src/Nexus.Sync/Models/AiConfigPaths.cs` | Done |
| 2.3 | One-shot cleanup on 1.2.2 first run: delete `~/.claude/backups/` entirely (legacy backup tree from 1.2.0/1.2.1). Idempotent — skip if directory doesn't exist. Run in startup orphan-cleanup path (replaces the existing `*.rollback.tmp` cleanup since rollback no longer exists). | dev | MODIFY | `src/Nexus.App/App.xaml.cs` | Done |
| 2.4 | Update `AiConfigApplyResult` — drop the `rolled_back` status. Remaining statuses: `applied`, `aborted`, `skipped_no_snapshot`, `skipped_no_install`. Convert from string to enum `AiConfigApplyStatuses` (NEXXOR enum naming) since the surface is now small enough. | dev | MODIFY | `src/Nexus.Sync/Models/AiConfigApplyResult.cs` | Done |

### Step 3: Path containment + manifest validation

Closes: CRITICAL #1, HIGH #11, MEDIUM #31.

| # | Description | Agent | Action | Files | Status |
|---|---|---|---|---|---|
| 3.1 | Add static helper `AiConfigPaths.IsContainedIn(string rootFull, string targetFull)` returning true only when `targetFull` starts with `rootFull + Path.DirectorySeparatorChar` (OrdinalIgnoreCase). Both inputs must be already canonicalized via `Path.GetFullPath`. | dev | MODIFY | `src/Nexus.Sync/Models/AiConfigPaths.cs` | Done |
| 3.2 | Pre-flight manifest validation in `ApplyAsync`: validate `managed_roots` entries (no `..`, no absolute, no `:`, no control chars, no embedded `\`, ≤ 260 chars). Validate `files[].path` entries with the same rules AND verify each file path falls within at least one `managed_roots` entry. Abort on first violation with descriptive message. | dev | MODIFY | `src/Nexus.Sync/Services/AiConfigApplyService.cs` | Done |
| 3.3 | At every site that constructs a write/delete target via `Path.Combine(ClaudeRoot, ...)`, call `IsContainedIn` against `Path.GetFullPath(ClaudeRoot)` before any I/O. Sites: temp download write, final write, wipe enumeration, finalize. Abort whole apply on first violation. | dev | MODIFY | `src/Nexus.Sync/Services/AiConfigApplyService.cs` | Done |

### Step 4: Symlink / junction pre-flight

Closes: HIGH #3.

| # | Description | Agent | Action | Files | Status |
|---|---|---|---|---|---|
| 4.1 | Add static helper `AiConfigPaths.ContainsReparsePoint(string root)` that walks the tree and returns true on the first `FileAttributes.ReparsePoint`. Use `EnumerateFileSystemEntries(SearchOption.AllDirectories)`. | dev | MODIFY | `src/Nexus.Sync/Models/AiConfigPaths.cs` | Done |
| 4.2 | In `ApplyAsync` pre-flight: for each entry in `manifest.managed_roots`, resolve to a full path under `ClaudeRoot` and call `ContainsReparsePoint`. If any returns true, abort with `aborted` + message naming the offending root. | dev | MODIFY | `src/Nexus.Sync/Services/AiConfigApplyService.cs` | Done |

### Step 5: Claude-Code-running probe

Closes: HIGH #9.

| # | Description | Agent | Action | Files | Status |
|---|---|---|---|---|---|
| 5.1 | Add `ClaudeCodeInstallProbe.IsRunning()` returning true when `Process.GetProcessesByName("claude")` is non-empty. Dispose Process handles. | dev | MODIFY | `src/Nexus.Core/Services/ClaudeCodeInstallProbe.cs` | Done |
| 5.2 | In `TriggerAiConfigCheck`, after fingerprint comparison decides an apply is needed, call `IsRunning()` BEFORE showing the popup. If true, log activity entry "Claude Code is running — apply deferred. Close Claude Code and retry.", `RefreshState`, return. | dev | MODIFY | `src/Nexus.App/App.xaml.cs` | Done |

### Step 6: Trigger boundary exception handling

Closes: HIGH #6 (combined with MEDIUM #21 `Logout` async-void fix).

| # | Description | Agent | Action | Files | Status |
|---|---|---|---|---|---|
| 6.1 | Wrap the body of `TriggerAiConfigCheck` (everything inside the existing `try { ... } finally { ... }`) in an outer `try/catch (Exception ex)` that logs to `_activity` (entry type `ai_config_check`, status `error`) and `_log.Error`. Existing `finally` clearing `_aiConfigCheckInFlight` stays innermost. | dev | MODIFY | `src/Nexus.App/App.xaml.cs` | Done |
| 6.2 | Convert `Logout()` from `async void` to `async Task`. Update call sites at `TrayIconManager.cs:119` and `MainWindow.xaml.cs:65` to `_ = _app.Logout();` so the fire-and-forget intent is explicit. | dev | MODIFY | `src/Nexus.App/App.xaml.cs`, `src/Nexus.App/TrayIconManager.cs`, `src/Nexus.App/MainWindow.xaml.cs` | Done |

### Step 7: HTTPS enforcement

Closes: HIGH #4.

| # | Description | Agent | Action | Files | Status |
|---|---|---|---|---|---|
| 7.1 | In `NexusApiClient.Configure`, reject `baseUrl` if scheme is not `https`. Allow `http://localhost` and `http://127.0.0.1` for local dev. | dev | MODIFY | `src/Nexus.Core/Services/NexusApiClient.cs` | Done |
| 7.2 | In `LoginWindow.BtnLogin_Click`, before calling `Configure`, validate the URL field with the same rule. Surface the error inline in the existing error label. | dev | MODIFY | `src/Nexus.App/LoginWindow.xaml.cs` | Done |

### Step 8: Test coverage

Closes: HIGH #10, #11 (tests), #12 + Low test gaps.

| # | Description | Agent | Action | Files | Status |
|---|---|---|---|---|---|
| 8.1 | Extract `INexusApiClient` interface covering `GetAiConfigManifest` and `DownloadAiConfigFile`. `NexusApiClient` implements it. `AiConfigApplyService` field type becomes the interface. | dev | CREATE/MODIFY | `src/Nexus.Core/Services/INexusApiClient.cs` (new), `src/Nexus.Core/Services/NexusApiClient.cs`, `src/Nexus.Sync/Services/AiConfigApplyService.cs` | Done |
| 8.2 | Add `AiConfigApplyServiceTests.cs` against a temp `ClaudeRoot` with a fake `INexusApiClient`. Cover: happy-path wipe-and-write (manifest with files in 2 managed roots, fresh empty disk); idempotent wipe (managed root path doesn't exist); path-traversal manifest entry aborted at pre-flight (no I/O); symlink under a managed root aborted at pre-flight; non-managed-root file path aborted at pre-flight; mid-download network failure leaves disk in partial state (acknowledged — next apply re-wipes); empty `manifest.files` with non-empty `managed_roots` wipes to empty dirs. | test-creation-dev | CREATE | `tests/Nexus.Sync.Tests/AiConfigApplyServiceTests.cs` | Done |
| 8.3 | Add `AiConfigPathsTests.cs` — unit tests for `IsContainedIn` (positive/negative paths, case insensitivity), `ContainsReparsePoint` (with a temp junction), and manifest path validation predicates. | test-creation-dev | CREATE | `tests/Nexus.Sync.Tests/AiConfigPathsTests.cs` | Done |
| 8.4 | Add `NexusApiClientAiConfigTests.cs` using a fake `HttpMessageHandler`. Cover: 200 success deserialization including `managed_roots`; 200 success WITHOUT `managed_roots` (fallback to empty + warning per Step 1.3); 404 returns "no snapshot"; 401 returns auth-error; timeout returns failure; malformed JSON throws caught error; non-HTTPS URL rejected by Configure. | test-creation-dev | CREATE | `tests/Nexus.Sync.Tests/NexusApiClientAiConfigTests.cs` | Done |
| 8.5 | Extend `ManifestFingerprintTests.cs` — Unicode path, case sensitivity, single-file manifest, duplicate-path entries. Replace the brittle empty-manifest constant-pinning test with behavioral assertions. | test-creation-dev | MODIFY | `tests/Nexus.Sync.Tests/ManifestFingerprintTests.cs` | Done |
| 8.6 | Run `dotnet test` — all green before Step 9. | tester | RUN | — | Done |

### Step 9: Docs + version + release

Closes: LOW #46 (doc lag).

| # | Description | Agent | Action | Files | Status |
|---|---|---|---|---|---|
| 9.1 | Rewrite `project_docs/structure.md` AI Config Sync section to match wipe-and-replace. Remove all references to backups/rollback/perfect-fit/exclusion list. Add a **Policy banner** at the top of the section: "Files under server-managed roots are overwritten on every apply. Do not edit them — your changes will be lost. Place personal work under `~/.claude/projects/` or outside the managed roots." | doc-writer | MODIFY | `project_docs/structure.md` | Done |
| 9.2 | Add `CHANGELOG.md` entry for 1.2.2. Group: `Changed` (wipe-and-replace apply, manifest schema gains `managed_roots`), `Security` (path containment, symlink rejection, Claude-running probe, HTTPS enforcement), `Tests` (new test files), `Removed` (backup ladder, rollback, perfect-fit delete, exclusion list, version marker). | doc-writer | MODIFY | `CHANGELOG.md` | Done |
| 9.3 | Bump `Nexus.App.csproj` `<Version>` 1.2.1 → 1.2.2. | dev | MODIFY | `src/Nexus.App/Nexus.App.csproj` | Done |
| 9.4 | Manual: run `cd /c/xampp/htdocs/nexus-windows-app && bash release.sh 1.2.2` from a standalone Git Bash (not VSCode terminal). | manual | RUN | — | Done |

### Step 10 (final — always last): Close-out

| # | Description | Agent | Action | Files | Status |
|---|---|---|---|---|---|
| 10.1 | Propose close-out checklist per Rule 21 (chat only — not written to plan). | tech-lead | RUN | — | Done |

## File Summary

### Created Files

| File | Purpose |
|---|---|
| `src/Nexus.Core/Services/INexusApiClient.cs` | Interface enabling integration tests of `AiConfigApplyService` |
| `tests/Nexus.Sync.Tests/AiConfigApplyServiceTests.cs` | Integration tests for the wipe-and-replace apply pipeline |
| `tests/Nexus.Sync.Tests/AiConfigPathsTests.cs` | Unit tests for path containment, reparse-point detection, manifest path validation |
| `tests/Nexus.Sync.Tests/NexusApiClientAiConfigTests.cs` | Unit tests for manifest fetch + file download HTTP boundary |

### Modified Files

| File | Changes |
|---|---|
| `src/Nexus.Core/Models/AiConfigManifest.cs` | Add `ManagedRoots` field |
| `src/Nexus.Sync/Models/AiConfigPaths.cs` | Add `IsContainedIn`, `ContainsReparsePoint`. Drop `IsExcluded`, `ExclusionList`, `VersionMarkerPath` |
| `src/Nexus.Sync/Services/AiConfigApplyService.cs` | Replace `ApplyAsync` with wipe-and-replace; delete backup/rollback/perfect-fit code |
| `src/Nexus.Sync/Models/AiConfigApplyResult.cs` | Drop `rolled_back` status; convert to enum |
| `src/Nexus.Core/Services/ClaudeCodeInstallProbe.cs` | Add `IsRunning()` |
| `src/Nexus.Core/Services/NexusApiClient.cs` | HTTPS enforcement in `Configure`; implement `INexusApiClient`; handle missing `managed_roots` with warning fallback |
| `src/Nexus.App/App.xaml.cs` | Outer try/catch around `ApplyAsync`; `Logout()` returns Task; Claude-running probe before popup; one-shot legacy-backup cleanup on startup |
| `src/Nexus.App/LoginWindow.xaml.cs` | HTTPS validation on URL field |
| `src/Nexus.App/TrayIconManager.cs` | Update `Logout()` call site for Task signature |
| `src/Nexus.App/MainWindow.xaml.cs` | Update `Logout()` call site for Task signature |
| `tests/Nexus.Sync.Tests/ManifestFingerprintTests.cs` | Unicode, case, duplicate-path, single-file, behavioral empty test |
| `project_docs/structure.md` | Rewrite AI Config Sync section; policy banner |
| `CHANGELOG.md` | 1.2.2 entry |
| `src/Nexus.App/Nexus.App.csproj` | Version 1.2.1 → 1.2.2 |

## Verification

1. **Path containment** — synthetic manifest with one entry `path = "../Desktop/evil.exe"`. Run apply. Expect `aborted` + activity log entry naming the rejected path. No file created anywhere.
2. **Non-managed-root rejection** — synthetic manifest with `managed_roots = ["agents/"]` but a `files[]` entry under `conventions/`. Expect abort at pre-flight.
3. **Managed-root traversal rejection** — manifest with `managed_roots = ["../"]`. Expect abort at pre-flight.
4. **Symlink rejection** — create a junction at `~/.claude/agents/test-junction`. Trigger apply with `managed_roots: ["agents/"]`. Expect `aborted` + "reparse point detected" entry. Junction target untouched.
5. **Claude Code running** — open Claude Code, trigger apply. Expect no popup, "Claude Code is running" activity entry. Close Claude, re-trigger, normal popup flow.
6. **Wipe behavior** — set up `~/.claude/agents/dev/AGENT.md` and `~/.claude/agents/old-agent/AGENT.md` on disk. Trigger apply with manifest that only contains `agents/dev/AGENT.md`. Expect post-apply: `agents/dev/AGENT.md` present, `agents/old-agent/` gone.
7. **Empty selection wipes** — manifest with non-empty `managed_roots` but empty `files`. Expect those roots ending up as empty directories.
8. **Idempotent wipe** — managed root path doesn't exist on disk. Apply succeeds, creates the dir if files target it.
9. **Trigger exception handling** — force `GetAiConfigManifest` to throw. App does not crash; activity log records the failure; popup does not stack.
10. **HTTPS enforcement** — enter `http://example.com` at `LoginWindow`. Inline error, no API call. `http://localhost` accepted.
11. **Legacy backup cleanup** — pre-fill `~/.claude/backups/` with stub directories before installing 1.2.2. After first 1.2.2 launch, `~/.claude/backups/` is gone.
12. **Tests** — `dotnet test` passes 100%.
13. **End-to-end live** — once Nexus server ships the `managed_roots` schema and `/sync-claude` writes a real per-user manifest, manual trigger via MainWindow "Check AI Config" against the live server. Expect successful apply.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Server ships `managed_roots` field in a desktop release window where some clients are on 1.2.1 (feature disabled) and some on 1.2.2 (expects the field) | 1.2.1 has the feature disabled — no impact. 1.2.0 has it enabled and would NOT see the field; 1.2.0 will fall over with no `managed_roots`. Acceptable since 1.2.0 has been replaced by 1.2.1 already and devs are on auto-update. |
| Compromised server sets `managed_roots: ["projects/"]` or `["credentials/"]` and wipes machine-local state | Accept that the channel is server-trust today. Tracked in `improvements.md` (AI Config Manifest Signature). Document the limit in `structure.md`. |
| Dev customization policy not communicated; first apply wipes a dev's local edit | `structure.md` policy banner + CHANGELOG entry + popup text. The wipe IS the spec. |
| Mid-apply network failure leaves managed roots partially populated | Acknowledged — no atomicity guarantee. Next apply re-wipes and re-downloads from scratch. Surface in activity log on failure so dev knows to retry. |
| Manifest deserialization fails on existing 1.2.0/1.2.1 servers that don't yet ship `managed_roots` | Step 1.3 fallback — treat missing field as empty array + log warning. Empty `managed_roots` means apply downloads files but wipes nothing; same as a no-op for fresh installs, surfaces oddly for upgrades. Remove fallback in 1.3.0 once all servers updated. |

## Notes

- **Cross-project dependency**: Nexus server must ship the `managed_roots` field in the manifest response before desktop 1.2.2 can be tested end-to-end. Step 1.1 is the synchronization point.
- **Pre-execution git check** required per Rule 19 before Step 1.1.

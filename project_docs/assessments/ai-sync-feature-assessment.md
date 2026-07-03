# AI Config Sync Feature — Component Assessment (2026-06-08)

| Field   | Value                                                                                  |
|---------|----------------------------------------------------------------------------------------|
| Author  | PM                                                                                     |
| Agent   | tech-lead                                                                              |
| Created | 2026-06-08                                                                             |
| Status  | Active                                                                                 |
| Scope   | AI Config Sync subsystem (12 files, see below)                                          |
| Trigger | PM re-enabled the feature this turn by removing the `return;` at `App.xaml.cs:136`; no end-to-end run against live server has occurred |

## Methodology

MED tier, A/B comparison + merge, 2026-06-08. 7 specialist passes ran in parallel:

- Architect A and Architect B — independent design reviews
- Code-Reviewer A and Code-Reviewer B — independent C# quality/conventions reviews
- Security-Auditor A and Security-Auditor B — independent security audits (threat model: server compromise → arbitrary file write on dev machines)
- Tester — `dotnet test` suite + coverage analysis

Findings merged with cross-agent confidence markers (`Both agree`, `A-only`, `B-only`, `Multi-agent`). Simplicity gate: 6 recommendations simplified before write.

Test suite: 5 passed, 0 failed (44 ms). All tests in `ManifestFingerprintTests.cs`; rest of the subsystem has zero coverage.

## Files in scope (12)

```
src/Nexus.Core/Models/AiConfigManifest.cs
src/Nexus.Core/Models/AiConfigManifestFile.cs
src/Nexus.Sync/Models/AiConfigApplyResult.cs
src/Nexus.Sync/Models/AiConfigPaths.cs
src/Nexus.Sync/Services/AiConfigApplyService.cs
src/Nexus.Sync/Services/ManifestFingerprint.cs
src/Nexus.Core/Services/ClaudeCodeInstallProbe.cs
src/Nexus.App/AiConfigUpdatePromptWindow.xaml + .xaml.cs
src/Nexus.App/App.xaml.cs                (focus: TriggerAiConfigCheck + wiring)
src/Nexus.Core/Services/NexusApiClient.cs (focus: GetAiConfigManifest + DownloadAiConfigFile)
tests/Nexus.Sync.Tests/ManifestFingerprintTests.cs
```

## Critical Issues

| #   | Severity | Pattern           | Title                                                                                             | File                                                                 | Lines               | Status | Confidence  |
|-----|----------|-------------------|----------------------------------------------------------------------------------------------------|----------------------------------------------------------------------|---------------------|--------|-------------|
| 1   | CRITICAL | Path traversal    | `f.Path` from server is never validated; `Path.Combine` accepts `..` and absolute paths, enabling arbitrary file write outside `~/.claude/`. Includes temp dir (line 47), write target (76), rename target (101), rollback paths (185), perfect-fit delete (259-273). Fix: after `Path.Combine`, `Path.GetFullPath(target).StartsWith(Path.GetFullPath(ClaudeRoot) + Path.DirectorySeparatorChar, OrdinalIgnoreCase)` — abort whole apply on failure. | `src/Nexus.Sync/Services/AiConfigApplyService.cs`                    | 47, 76, 101, 185, 259 | FIXED   | Both agree (4 agents) |

## High Issues

| #   | Severity | Pattern              | Title                                                                                                                    | File                                                              | Lines        | Status | Confidence       |
|-----|----------|----------------------|---------------------------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------|--------------|--------|------------------|
| 2   | HIGH     | Trust model          | No manifest signature, no cert pinning. SHA-256 only verifies transport, not authorship — a compromised Nexus server (or anyone with manifest-write access) writes any payload, with matching SHA. Combined with #1, ships hooks/agents/settings.json to every dev = code-exec vector. Fix: embed Ed25519 pubkey in binary, require detached signature over manifest JSON; verify before any I/O. | `NexusApiClient.cs` + `AiConfigApplyService.cs`                   | 19-23, 62-70 | TODO   | Both agree (4 agents) |
| 3   | HIGH     | Symlink              | `Directory.GetFiles(claudeRoot, SearchOption.AllDirectories)` and `File.Move`/`File.Copy` follow Windows junctions and symlinks. A junction inside `~/.claude/` (e.g., dev linking `agents/` to a git checkout) means perfect-fit delete and rename traverse outside ClaudeRoot. Fix: reject apply if any `FileAttributes.ReparsePoint` is found under ClaudeRoot. | `src/Nexus.Sync/Services/AiConfigApplyService.cs`                 | 105, 185, 259, 276 | FIXED | B-only (sec-A) |
| 4   | HIGH     | TLS                  | `NexusApiClient.Configure(baseUrl, ...)` accepts `http://`. `LoginWindow` reads URL with no scheme validation. Bearer token sent in clear if user typoes or attacker downgrades. Fix: reject any URL not starting with `https://` (consider dev-only opt-in via env var). | `NexusApiClient.cs` + `LoginWindow.xaml.cs`                       | 25-34        | FIXED   | A-only (sec-A)   |
| 5   | HIGH     | Atomicity            | Rollback violates the "F or F'" invariant: newly-added manifest files are skipped during rollback (left on disk in F' state) while older files revert to F. Mixed-state result after partial rename failure. Fix: during rollback, delete `successList` entries that have no backup counterpart instead of skipping them. | `src/Nexus.Sync/Services/AiConfigApplyService.cs`                 | 167-207, 178-183 | FIXED | A-only (arch-A, code-B converges) |
| 6   | HIGH     | Failure mode         | Rollback rethrows on partial failure → exception escapes `ApplyAsync` → `TriggerAiConfigCheck` has no surrounding try/catch → silent on timer/button path or app crash on login path. Disk is in corrupted state, fingerprint marker stale, next 4h tick will re-apply onto the broken baseline. Fix: outer try/catch in `TriggerAiConfigCheck` logging to `_activity`/`_log`. | `App.xaml.cs` + `AiConfigApplyService.cs`                         | 134-163, 191-200 | FIXED | Both agree (arch-A, code-A) |
| 7   | HIGH     | Edge case            | Backup is taken AFTER `EnforceBackupRetention` prunes to 4. If `BackupClaudeRoot` throws (disk full, file locked, perm denied), the prune already deleted the once-oldest and the new backup is partial. Fix: take new backup first, prune oldest only after the new one lands. | `src/Nexus.Sync/Services/AiConfigApplyService.cs`                 | 33-38, 217   | FIXED   | B-only (arch-B) |
| 8   | HIGH     | Missing safeguard    | Perfect-fit delete silently destroys any user-local file under `~/.claude/` not in the manifest and not in the exclusion list (e.g., a `notes.md`, `scratch/`, custom agent). Exclusion list is enumerative, not exhaustive. Fix: collect planned deletion list, log every path, abort apply with a clear modal if deletion count exceeds a sanity threshold (e.g., >20% of existing files, or >N files). | `src/Nexus.Sync/Services/AiConfigApplyService.cs`                 | 250-295      | FIXED   | Both agree (4 agents) |
| 9   | HIGH     | Missing safeguard    | Claude Code running during apply: only the popup text warns. If user clicks Apply with Claude Code open, `File.Move` may fail mid-rename on a locked file, triggering rollback (see #6). Fix: before apply, `Process.GetProcessesByName("claude")` — refuse with a message naming the running process. | `App.xaml.cs` + `AiConfigApplyService.cs`                         | 134, 98-122  | TODO   | Both agree (arch-A, arch-B) |
| 10  | HIGH     | Test coverage        | `AiConfigApplyService` (the entire orchestrator — 300+ lines covering backup, download, verify, rename, rollback, perfect-fit, finalize) has zero tests. Fix: write integration tests against a temp ClaudeRoot with a fake `INexusApiClient` (would require introducing an interface, currently the field is the concrete `NexusApiClient` — see #19). | `src/Nexus.Sync/Services/AiConfigApplyService.cs`                 | 23-164       | FIXED   | Single (tester) |
| 11  | HIGH     | Test coverage        | `AiConfigPaths.IsExcluded` does not reject path-traversal inputs (`..`, absolute paths) and is untested for them. Compounds #1 since `BackupClaudeRoot`/`PerfectFitDelete` both rely on `IsExcluded` for safety. Fix: add `..` rejection to `IsExcluded`; add unit tests for traversal/case/exhaustive list. | `src/Nexus.Sync/Models/AiConfigPaths.cs`                          | 53-108       | FIXED   | Single (tester) |
| 12  | HIGH     | Test coverage        | `NexusApiClient.GetAiConfigManifest` and `DownloadAiConfigFile` have zero tests despite meaningful branching (404, 401, deserialization-null, timeout, `HttpRequestException`). Fix: tests with `HttpMessageHandler` fake. | `src/Nexus.Core/Services/NexusApiClient.cs`                       | 126-191      | FIXED   | Single (tester) |

## Medium Issues

| #   | Severity | Pattern           | Title                                                                                                                                                                                                                                                                                              | File                                                              | Lines              | Status | Confidence       |
|-----|----------|-------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------|--------------------|--------|------------------|
| 13  | MEDIUM   | Threading         | `_aiConfigCheckInFlight` is a plain `bool` with no `volatile`/`Interlocked`. Safe today only because all triggers run on the UI dispatcher; will silently break if any future caller is off-thread. The XML doc says "Must be invoked on the UI dispatcher" — load-bearing on a comment. Fix: `Interlocked.CompareExchange` on an `int`, or move the guard inside `ApplyAsync` (which already has a `lock`).                            | `App.xaml.cs`                                                     | 25, 135, 141, 161  | TODO   | Disputed → verified (arch-A HIGH, arch-B MEDIUM, code-B NOISE — kept MEDIUM as compromise) |
| 14  | MEDIUM   | UI thread         | `File.ReadAllText(FingerprintPath)` (App.xaml.cs:151) and `File.WriteAllText(FingerprintPath)` (AiConfigApplyService.cs:133) block the UI dispatcher. Fix: `await File.ReadAllTextAsync` / `WriteAllTextAsync`.                                                                                  | `App.xaml.cs` + `AiConfigApplyService.cs`                         | 151, 133           | TODO   | A-only (code-A)  |
| 15  | MEDIUM   | Failure mode      | `BackupClaudeRoot` has no per-file try/catch; partial backup failure escapes `ApplyAsync` entirely. With #6, this means an unhandled exception in the dispatcher path. Fix: catch in `BackupClaudeRoot`, return `aborted` with `Message = "Backup failed: ..."`.                                       | `src/Nexus.Sync/Services/AiConfigApplyService.cs`                 | 230-247            | FIXED   | A-only (arch-A)  |
| 16  | MEDIUM   | Edge case         | Case-sensitivity inconsistency: `ManifestFingerprint.Compute` uses `StringComparer.Ordinal`; `PerfectFitDelete` uses `StringComparer.OrdinalIgnoreCase`. Server emitting two paths that differ only in case (e.g., `README.md` / `readme.md`) collapses to one in the delete set — one survives on the case-insensitive FS, the other is silently dropped. Fix: assert no case-collisions at manifest parse; pick one comparer consistently.                                                              | `ManifestFingerprint.cs` + `AiConfigApplyService.cs`              | 12, 253-256        | FIXED   | Both agree (arch-A, arch-B) |
| 17  | MEDIUM   | Failure mode      | 401 mid-download is logged but not surfaced to caller. Next 4h trigger re-tries, burning a backup slot each time, while the user still has a stale token. Fix: detect 401 in `ApplyAsync` result; call `_syncEngine.OnAuthFailed()` from `TriggerAiConfigCheck`.                                       | `AiConfigApplyService.cs` + `App.xaml.cs`                         | 53-57, 134-163     | TODO   | B-only (arch-B)  |
| 18  | MEDIUM   | UI feedback       | Apply failures (`rolled_back`, `aborted`) surface only via Activity Log. No tray balloon, no modal, no toast. User who clicked Apply Now and walked away has no idea apply rolled back. Fix: tray balloon / modal on non-success.                                                                  | `App.xaml.cs`                                                     | 158                | FIXED   | B-only (arch-B)  |
| 19  | MEDIUM   | SRP               | `ApplyAsync` is 140+ lines doing backup, download, verify, write, rename, finalize, rollback inline. Violates conventions.md "one function does one thing". Refactor into named phase methods.                                                                                                  | `src/Nexus.Sync/Services/AiConfigApplyService.cs`                 | 23-164             | FIXED   | B-only (code-B)  |
| 20  | MEDIUM   | Type safety       | `AiConfigApplyResult.Status` is a string with 5 documented values. Convert to enum `AiConfigApplyStatuses` (NEXXOR enum naming) — caller in App.xaml.cs:158 only logs the string today, so the migration is safe.                                                                                  | `src/Nexus.Sync/Models/AiConfigApplyResult.cs`                    | 4                  | FIXED   | B-only (code-B)  |
| 21  | MEDIUM   | Async             | `Logout()` is `public async void` but called from non-event-handler sites (`TrayIconManager.cs:119`, `MainWindow.xaml.cs:65`). Exceptions after first `await` are unobservable and crash the process. Fix: `public async Task Logout()`, callers use `_ =`.                                                                                                                                                                              | `App.xaml.cs`                                                     | 198                | FIXED   | A-only (code-A)  |
| 22  | MEDIUM   | Cancellation      | Only `TaskCanceledException` is caught in `NexusApiClient` HTTP methods. In .NET 6+, an `HttpClient.Timeout` cancellation can surface as `OperationCanceledException` directly. Fix: catch `OperationCanceledException` (parent of `TaskCanceledException`). Verify before fix.                                                                                                                                                | `NexusApiClient.cs`                                               | 65, 117, 152, 184  | FIXED   | B-only (code-B); verify before fix |
| 23  | MEDIUM   | JSON config       | `GetAiConfigManifest` allocates `new JsonSerializerOptions` per call (loses reflection cache); `JsonException` is not caught (propagates to dispatcher). Fix: reuse a static options field; add `catch (JsonException)`.                                                                            | `NexusApiClient.cs`                                               | 144-149            | TODO   | B-only (code-B)  |
| 24  | MEDIUM   | Logging           | `$"{apply.Status}: {apply.Message}"` produces `"applied: "` on success (Message is null). No version, no file count. Fix: include `apply.Version` and the manifest file count in the success line.                                                                                                  | `App.xaml.cs`                                                     | 158                | TODO   | B-only (code-B)  |
| 25  | MEDIUM   | Logging           | SHA mismatch log doesn't include declared size from manifest. First triage question is "expected vs actual bytes". Fix: include `f.Size` and downloaded byte count.                                                                                                                                  | `AiConfigApplyService.cs`                                         | 67                 | FIXED   | B-only (code-B)  |
| 26  | MEDIUM   | Idempotency       | Download temp dir lives in `Path.GetTempPath()`, not under `~/.claude/`. `CleanupOrphanAiConfigTmpFiles` only scans `~/.claude/`. A hard process kill before the `finally` runs leaves the orphan in `%TEMP%` forever.                                                                                                                                                                                                                  | `App.xaml.cs` + `AiConfigApplyService.cs`                         | 211-230, 41        | FIXED   | B-only (code-B)  |
| 27  | MEDIUM   | Disk budget       | `AiConfigManifestFile.Size` is declared but never read. No disk-space pre-flight, no per-file size cap, no total-size cap. Malicious or runaway server can fill disk in one apply. Fix: pre-flight `Sum(f => f.Size)` vs available space; per-file cap (e.g., 10MB) and total cap (e.g., 100MB).        | `AiConfigApplyService.cs` + `AiConfigManifestFile.cs`             | 51-52, 6           | TODO   | Both agree (arch-A, sec-A) |
| 28  | MEDIUM   | Edge case         | OneDrive-redirected `%USERPROFILE%` + deep agent tree can cross 260-char path limit. csproj does not enable `longPathAware`. Backup paths nest one level deeper than `~/.claude/`. Fix: app manifest enabling long paths, or pre-flight length check with clear error.                                                                                                                                                            | `AiConfigPaths.cs` + `AiConfigApplyService.cs`                    | 8-9, 230           | TODO   | A-only (arch-A)  |
| 29  | MEDIUM   | Backup integrity  | `RollbackAsync` reads from `~/.claude/backups/{timestamp}/` with no SHA validation. Backup dir inherits default user-profile ACL — any user-context code can poison it. Fix: store a SHA manifest with each backup; verify before restore.                                                          | `AiConfigApplyService.cs`                                         | 167-207            | FIXED   | B-only (sec-B)   |
| 30  | MEDIUM   | Confidentiality   | Backups copy everything under `~/.claude/` not in exclusion list. `.credentials*` and `settings.local.json` ARE excluded — good. Other secret material (custom `.env`, API keys, MCP configs) is copied unencrypted with default ACLs. Fix: harden backup dir ACL to user-only; consider DPAPI-wrapped tarball instead of raw files.                                                                                                                                  | `AiConfigApplyService.cs` + `AiConfigPaths.cs`                    | 230-247, 28-51     | FIXED   | A-only (sec-A)   |
| 31  | MEDIUM   | Injection         | Manifest path is not checked for null bytes, control chars, NTFS Alternate Data Streams (`foo.md:hidden.exe`), or embedded backslashes (the `Replace('/', sep)` only handles forward slashes; an embedded `\` survives on Windows as a separator). Fix: reject manifest paths failing a strict whitelist (no `..`, no `:`, no control chars, no NTFS ADS, no embedded separators). | `AiConfigApplyService.cs`                                         | 47, 76             | FIXED   | A-only (sec-A)   |
| 32  | MEDIUM   | Manifest rollback | `manifest.Version` is informational. Server can serve `0.0.1` after `1.5.0` was applied and the fingerprint change triggers it. Combined with #8 (no preview), enables "downgrade to old vulnerable agent prompt" via the same channel. Fix: persist last-applied version; refuse strictly-lower, or surface "DOWNGRADE" in prompt. | `AiConfigApplyService.cs`                                         | 130-134            | TODO   | B-only (sec-B)   |
| 33  | MEDIUM   | Probe weakness    | `ClaudeCodeInstallProbe.IsInstalled` is a single `File.Exists` — a stub at the expected path passes. Probe is UX-only today, so impact bounded; flagging in case it's repurposed as a security gate. Fix: `FileVersionInfo.GetVersionInfo(path).CompanyName == "Anthropic"` or signed-binary check.                                                                                                                                              | `ClaudeCodeInstallProbe.cs`                                       | 3-12               | FIXED   | A-only (sec-A)   |
| 34  | MEDIUM   | Exception handling | `catch { /* best-effort */ }` in `DeleteTmpFiles` and `CleanupRollbackTmpFiles` swallows everything including `OutOfMemoryException`. Fix: narrow to `catch (IOException)` and `catch (UnauthorizedAccessException)`.                                                                              | `AiConfigApplyService.cs`                                         | 305-311, 321-322   | FIXED   | A-only (code-A)  |

## Low / Style Issues

| #   | Severity | Pattern             | Title                                                                                                                                                                                                              | File                                                       | Lines        | Status | Confidence  |
|-----|----------|---------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------|--------------|--------|-------------|
| 35  | LOW      | Magic value         | 4-hour timer, 30s/300s timeouts, 4 backup retention all inline. Extract to named `const` fields.                                                                                                                    | `App.xaml.cs`, `NexusApiClient.cs`, `AiConfigApplyService.cs` | 98, 39/103/130/165, 217 | TODO | Both agree |
| 36  | LOW      | Magic value         | `"*.rollback.tmp"` appears in 3 places (`App.xaml.cs:218`, `AiConfigApplyService.cs:318`, `AiConfigPaths.cs:50`). Promote to `AiConfigPaths.RollbackTmpPattern`.                                                     | multiple                                                   | -            | FIXED   | A-only (code-A) |
| 37  | LOW      | Disposal            | `HttpResponseMessage` not in `using` block in `GetAiConfigManifest` (NexusApiClient.cs:131) and `DownloadAiConfigFile` (NexusApiClient.cs:166). Minor leak. Fix: `using var response = ...`.                            | `NexusApiClient.cs`                                        | 131, 166     | TODO   | A-only (code-A) |
| 38  | LOW      | Async               | No `ConfigureAwait(false)` in pure-service `AiConfigApplyService`/`NexusApiClient`. Unnecessary dispatcher round-trips on every continuation. Fix: add `.ConfigureAwait(false)` in non-UI async methods.            | service-layer files                                        | -            | TODO   | A-only (code-A) |
| 39  | LOW      | Atomicity           | `File.WriteAllText(FingerprintPath, ...)` is not atomic. Crash between truncate and write leaves empty marker → re-apply on next launch. Fix: write `.tmp` + `File.Move(overwrite: true)`.                                                                                                                          | `AiConfigApplyService.cs`                                  | 133          | TODO   | B-only (arch-B) |
| 40  | LOW      | Edge case           | Backup dir name uses UTC seconds; two backups within the same second collide. `_running` lock makes this unreachable today but defensive. Fix: append milliseconds or counter.                                       | `AiConfigApplyService.cs`                                  | 29           | TODO   | A-only (arch-A) |
| 41  | LOW      | TempDir             | Temp dir name `nexus-ai-config-{yyyyMMddHHmmss}` is predictable to ~1s. Combined with %TEMP% being user-writable, raises race exploit window. Fix: `Guid.NewGuid():N` suffix.                                        | `AiConfigApplyService.cs`                                  | 41           | FIXED   | B-only (sec-B) |
| 42  | LOW      | Failure mode        | `DownloadAiConfigFile` has no retry. `SyncEngine` has 2-attempt retry for transcripts. Pattern parity: add 2-attempt retry + 5s delay.                                                                              | `NexusApiClient.cs`                                        | 161-191      | TODO   | A-only (arch-A) |
| 43  | LOW      | Schema              | `AiConfigManifest` / `AiConfigManifestFile` are positional records — missing fields throw `JsonException` with no graceful degradation. Acceptable today; revisit if manifest schema evolves.                       | `AiConfigManifest.cs`                                      | 3-7          | TODO   | A-only (arch-B) |
| 44  | LOW      | Dead code           | `VersionMarkerPath` constant is only read by the one-shot legacy-deletion step at `AiConfigApplyService.cs:137-144`. Once telemetry confirms all installs cleaned up, remove.                                       | `AiConfigPaths.cs`                                         | 14-26        | FIXED   | A-only (arch-A) |
| 45  | LOW      | Telemetry           | No server-side reporting of apply success/failure. PM can only learn of a bad snapshot from devs complaining. Out-of-scope for desktop fix alone; raise to server team.                                              | -                                                          | -            | TODO   | A-only (arch-A) |
| 46  | LOW      | Doc lag             | `project_docs/structure.md` lines 126 + 152 still say "AI Config Sync is currently disabled — `TriggerAiConfigCheck()` early-returns" but the `return;` was removed this turn. Update structure.md.                  | `project_docs/structure.md`                                | 126, 152     | FIXED   | B-only (arch-B) |
| 47  | LOW      | Test gap            | `ManifestFingerprintTests` does not cover: Unicode paths, case sensitivity (`Agents/` vs `agents/`), duplicate path entries, single-file manifest. Empty-manifest test pins implementation detail instead of behavior. | `ManifestFingerprintTests.cs`                              | -            | FIXED   | Multi (3 agents) |
| 48  | LOW      | Naming nit          | `lblBody` (Hungarian `lbl` prefix) applied to a `TextBlock`, not a `Label`. Convention table doesn't define a `TextBlock` prefix. Cosmetic.                                                                          | `AiConfigUpdatePromptWindow.xaml`                          | 16           | TODO   | A-only (code-A) |
| 49  | LOW      | Resource use        | `CleanupOrphanAiConfigTmpFiles` constructs a new `LogService` inside a per-file `catch` block. App already holds `_log`. Make non-static or pass in.                                                                | `App.xaml.cs`                                              | 211-231      | TODO   | A-only (code-A) |
| 50  | LOW      | Idempotency         | Newly-added files left on disk after rollback (no backup to restore from). Subsequent apply overwrites cleanly, so not a bug — but document.                                                                        | `AiConfigApplyService.cs`                                  | 178-183      | FIXED   | B-only (code-B) |
| 51  | LOW      | Migration           | First run after re-enable forces every 1.2.0 user through a no-op apply. **Decision: accept** (per simplicity gate — a local fingerprint recomputation requires more code than letting the no-op apply run). Document the one-time apply in CHANGELOG.                                                                                          | `App.xaml.cs`                                              | 150-154      | TODO   | A-only (arch-A) — simplified |

## Noise / No Action

| #   | Severity | Pattern    | Title                                                                                                                | Confidence |
|-----|----------|------------|----------------------------------------------------------------------------------------------------------------------|------------|
| 52  | NOISE    | Positive   | `AiConfigUpdatePromptWindow.xaml.cs` code-behind is appropriately thin (InitializeComponent + 2 click handlers).      | A-only     |
| 53  | NOISE    | Positive   | DPAPI auth-token handling reviewed end-to-end; no plaintext leakage in the AI config flow.                            | B-only     |
| 54  | NOISE    | Positive   | `ActivityLogService` writes to local disk only; not plumbed into any upload path. Verified.                            | B-only     |

## Priority Fix Order

### Must fix before next public release (blocking)

1. **#1 Path traversal** — single CRITICAL; one-line containment check.
2. **#11 IsExcluded path-traversal** — compounds #1; rejection + tests.
3. **#8 Perfect-fit delete sanity guard** — single bad manifest can wipe `~/.claude/`.
4. **#3 Symlink/junction rejection** — Windows junctions inside `~/.claude/` already in use at NEXXOR (e.g., dev linking `agents/` to a checkout).
5. **#9 Claude-Code-running probe** — popup string is not a control.
6. **#6 TriggerAiConfigCheck try/catch** — current behavior is "silent on timer, crash on login".
7. **#5 Rollback of newly-added files** — invariant fix is a few lines.
8. **#7 Backup-then-prune ordering** — one-line reorder.
9. **#4 HTTPS enforcement** — one-line scheme check.

### Must fix before treating the channel as production (high priority)

10. **#2 Manifest signature** — closes the stated threat model. Requires server changes (out-of-scope here, but block ramp-up to new installs until landed).
11. **#10 + #12 Test coverage** — `AiConfigApplyService` and `NexusApiClient` integration tests. Probably involves introducing an `INexusApiClient` interface (small refactor).

### Should fix in next minor

12. #13, #14, #15, #17, #18, #19, #20, #21, #22, #23, #27, #28, #29, #30, #31, #32 — quality and observability improvements.

### Backlog (LOW)

13. Everything in the Low table.

## What's Good

1. Atomicity intent is sound — two-phase write + atomic rename + rollback-on-failure is the right shape.
2. Fingerprint algorithm is deterministic and order-invariant; tests exercise the right properties.
3. Trigger logic — login, manual button, and 4h timer all funnel through a single re-entry-guarded entry point.
4. DPAPI token handling is correct; no plaintext token leakage in the AI config flow.
5. Activity log writes locally only; no surprise data exfiltration via the transcripts pipeline.
6. The popup code-behind is appropriately thin.
7. Backup retention with cap-on-take is the right shape (modulo the ordering bug #7).
8. Test scaffolding exists — `ManifestFingerprintTests` is well-structured and easy to extend.

## Verdict

**5/10 — Critical** — The feature is well-shaped architecturally, but ships with a CRITICAL path-traversal hole and an undefended trust model. A compromised Nexus server (or anyone with manifest-write access) can write arbitrary files to arbitrary paths on every dev machine — and because `~/.claude/hooks/` and agent definitions are part of what gets written, the AI-config channel is a direct code-execution vector. The "F or F'" atomicity invariant is also overstated: newly-added files, rollback rethrow paths, and the backup-then-prune ordering all break it in observable ways. None of the blockers are large refactors — most are 1-20 line containment checks — but they must land before the channel is used in anger. Test coverage of the orchestrator itself is zero, so confidence in the existing happy-path behavior rests on visual review alone.

Rating cap applied per conventions: 1 CRITICAL outstanding caps verdict at 5.

## Methodology Notes

- 7 specialist passes (2 architect, 2 code-reviewer, 2 security-auditor, 1 tester) ran in parallel
- Cross-agent agreement on the CRITICAL path-traversal finding (4 of 4 reviewing agents flagged it independently)
- Strong agreement on the trust-model gap (4 agents)
- One dispute resolved: re-entrancy guard severity (HIGH/MEDIUM/NOISE) — kept at MEDIUM with the dispatcher-invariant rationale documented
- Simplicity gate: 6 recommendations simplified (rollback sentinel file dropped, Task.Run dropped, migration fast-path dropped, diff/preview UI dropped, TOCTOU memory-buffering dropped, 24h cooldown dropped)
- Test suite passed: 5/5 (44ms)

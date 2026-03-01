# Compatibility Fixes — Implementation Plan

## Context

The API compatibility review (`.claude/plan-compatibility-testing-with-nexus-web.md`) identified 6 FAILs and 8 WARNINGs across 22 review items. This plan addresses all actionable findings. One WARNING (2.4 — parent_transcript_id race condition) is already tracked as sync pipeline issue #9 and is excluded here.

---

## Architecture / Approach

All fixes are scoped to minimize blast radius. Security fixes (Step 1) touch both client and server. Error handling and robustness fixes (Steps 2-3) are client-only. Cleanup (Step 4) is client-only cosmetic changes.

```
Step 1: Security          → client + server changes (MEDIUM severity)
Step 2: Error Handling    → client-only (WARNING/FAIL)
Step 3: Robustness        → client-only (WARNING/FAIL)
Step 4: Cleanup           → client-only (LOW)
```

---

## Steps

### Step 1: Security — Token Storage & Lifecycle

Fixes 4.2 (plaintext token), 4.3 (no expiry/revocation, silent 401 stall).

| # | Description | Agent | Action | Files |
|---|-------------|-------|--------|-------|
| 1.1 | Encrypt token with DPAPI before writing to config.json | dev | MODIFY | `src/Nexus.Core/Models/AppConfig.cs` |
| 1.2 | Add logout endpoint to AuthController | dev | MODIFY | `c:\xampp\htdocs\nexus\app\Http\Controllers\Api\V1\AuthController.php`, `c:\xampp\htdocs\nexus\routes\api.php` |
| 1.3 | Call revoke endpoint from client Logout() before clearing local config | dev | MODIFY | `src/Nexus.App/App.xaml.cs`, `src/Nexus.Core/Services/NexusApiClient.cs` |
| 1.4 | Set Sanctum token expiry (30 days) | dev | MODIFY | `c:\xampp\htdocs\nexus\config\sanctum.php` |
| 1.5 | Show login window on 401 during sync instead of silent stall | dev | MODIFY | `src/Nexus.Sync/Services/SyncEngine.cs`, `src/Nexus.App/App.xaml.cs` |

### Step 2: Error Handling

Fixes 3.5 (no 429 handling), 3.2 (422 detail not logged), 3.1 (login 401 branch).

| # | Description | Agent | Action | Files |
|---|-------------|-------|--------|-------|
| 2.1 | Add `IsRateLimited` property to ApiResult for 429 responses | dev | MODIFY | `src/Nexus.Core/Models/ApiResult.cs` |
| 2.2 | Handle 429 in Login — show "Too many attempts, wait X seconds" using Retry-After header | dev | MODIFY | `src/Nexus.Core/Services/NexusApiClient.cs`, `src/Nexus.App/Views/LoginWindow.xaml.cs` |
| 2.3 | Log `ErrorDetail` on 422 failures in SyncEngine instead of generic "Upload failed" | dev | MODIFY | `src/Nexus.Sync/Services/SyncEngine.cs` |
| 2.4 | Add 401-specific branch in Login method — return distinct message "Invalid credentials" | dev | MODIFY | `src/Nexus.Core/Services/NexusApiClient.cs` |

### Step 3: Robustness

Fixes 4.5 (no content size pre-check), 2.5 (sessionId validation), 1.1 (null-filtering guard).

| # | Description | Agent | Action | Files |
|---|-------------|-------|--------|-------|
| 3.1 | Add file size check before reading — skip files > 30MB with warning log | dev | MODIFY | `src/Nexus.Sync/Services/SyncEngine.cs` |
| 3.2 | Validate composite sessionId length (< 100) and format before upload | dev | MODIFY | `src/Nexus.Sync/Services/SyncEngine.cs` |
| 3.3 | Replace null-filtering with explicit payload building — main payload omits subagent fields, subagent payload includes them | dev | MODIFY | `src/Nexus.Core/Services/NexusApiClient.cs` |

### Step 4: Cleanup

Fixes 1.4 (CamelCase trap), 4.4 (Ok() hardcodes 200), 4.1 (acompact comment).

| # | Description | Agent | Action | Files |
|---|-------------|-------|--------|-------|
| 4.1 | Remove `CamelCase` naming policy from `_jsonOptions` — not needed for dictionary payloads | dev | MODIFY | `src/Nexus.Core/Services/NexusApiClient.cs` |
| 4.2 | Pass actual HTTP status code to `ApiResult.Ok()` in UploadTranscript (match Login pattern) | dev | MODIFY | `src/Nexus.Core/Services/NexusApiClient.cs` |
| 4.3 | Add comment documenting why acompact-* files are skipped | dev | MODIFY | `src/Nexus.Sync/Services/TranscriptScanner.cs` |

---

## Detailed Sub-steps

### Step 1 Sub-steps

#### 1.1 DPAPI Token Encryption

**File**: `src/Nexus.Core/Models/AppConfig.cs`

Use `System.Security.Cryptography.ProtectedData` to encrypt the token before writing and decrypt after reading. Scope: `DataProtectionScope.CurrentUser`.

```csharp
// In SetLoginData — encrypt before storing
var tokenBytes = Encoding.UTF8.GetBytes(token);
var encrypted = ProtectedData.Protect(tokenBytes, null, DataProtectionScope.CurrentUser);
AuthToken = Convert.ToBase64String(encrypted);

// In Load — decrypt after reading
var encrypted = Convert.FromBase64String(AuthToken);
var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
AuthToken = Encoding.UTF8.GetString(decrypted);
```

Add NuGet reference to `System.Security.Cryptography.ProtectedData` in `Nexus.Core.csproj`.

**Migration**: Existing plaintext tokens in config.json will fail decryption on first load. Handle gracefully — if decryption fails, clear the token and force re-login.

#### 1.2 Server Logout Endpoint

**Files**: `AuthController.php`, `routes/api.php`

Add `logout()` method to AuthController:

```php
public function logout(Request $request) {
    $request->user()->currentAccessToken()->delete();
    return response()->json(['message' => 'Token revoked'], 204);
}
```

Register route under `auth:sanctum` middleware in `api.php`.

#### 1.3 Client Logout Revoke Call

**Files**: `App.xaml.cs`, `NexusApiClient.cs`

Add `RevokeToken()` method to NexusApiClient that calls `POST /api/v1/auth/logout`. Call it from `App.Logout()` before `_config.ClearLoginData()`. Fire-and-forget — if the call fails (offline, expired token), proceed with local logout anyway.

#### 1.4 Sanctum Token Expiry

**File**: `config/sanctum.php`

Set `'expiration' => 43200` (30 days in minutes). Sanctum will automatically reject expired tokens.

#### 1.5 Auto Re-login on 401

**Files**: `SyncEngine.cs`, `App.xaml.cs`

Add an `Action? OnAuthFailed` callback to SyncEngine. When `authFailed` is set, invoke the callback. `App` wires this to show the login window. This replaces the current silent stall behavior.

### Step 2 Sub-steps

#### 2.1 IsRateLimited Property

**File**: `ApiResult.cs`

Add `public bool IsRateLimited => StatusCode == 429;`

#### 2.2 Handle 429 in Login

**Files**: `NexusApiClient.cs`, `LoginWindow.xaml.cs`

In `NexusApiClient.Login()`, check for 429 status and read the `Retry-After` header if present. Return an `ApiResult` with `IsRateLimited = true` and a message like "Too many login attempts. Please wait {seconds} seconds."

In `LoginWindow`, check `result.IsRateLimited` and show the retry message instead of generic failure.

#### 2.3 Log ErrorDetail on 422

**File**: `SyncEngine.cs`

In the failure branch after upload, log `result.ErrorDetail` when `result.IsValidationError` is true:

```csharp
if ( result.IsValidationError )
    _activity.Add($"Validation error for {file.FileName}: {result.ErrorDetail}");
```

#### 2.4 Login 401 Branch

**File**: `NexusApiClient.cs`

In `Login()`, check for 401 specifically and return a clear "Invalid email or password" message instead of generic "Login failed".

### Step 3 Sub-steps

#### 3.1 File Size Pre-check

**File**: `SyncEngine.cs`

Before `ReadToEndAsync()`, check `new FileInfo(file.FilePath).Length`. If > 30MB (31_457_280 bytes), log a warning and skip the file. Mark it in state so it's not retried every cycle.

#### 3.2 SessionId Validation

**File**: `SyncEngine.cs`

After building composite `subSessionId`, validate:
- Length <= 100 characters
- Matches `^[a-zA-Z0-9_\-]+$`

If invalid, log warning and skip the subagent upload.

#### 3.3 Explicit Payload Building

**File**: `NexusApiClient.cs`

Replace the generic null-filtering `Dictionary` pattern with two explicit payload methods or conditional key insertion. Main payloads never include `subagent_type` / `parent_transcript_id`. Subagent payloads always include them. This eliminates the ambiguity between absent-key and null-value.

### Step 4 Sub-steps

#### 4.1 Remove CamelCase Policy

**File**: `NexusApiClient.cs`

Remove `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` from `_jsonOptions`. Both payloads use dictionary keys (unaffected) or lowercase anonymous properties (unaffected). The policy adds no value and creates a trap for future typed payloads.

#### 4.2 Pass Actual Status Code to Ok()

**File**: `NexusApiClient.cs`

Change `UploadTranscript` success path from `ApiResult.Ok()` to `ApiResult.Ok()` with `StatusCode = (int)response.StatusCode`, matching the pattern already used in `Login()`.

#### 4.3 Document acompact Skip

**File**: `TranscriptScanner.cs`

Add a brief comment at the `acompact-*` skip line explaining these are context-compaction summaries, not new activity — the original session file captures all data.

---

## File Summary

### Modified Files

| File | Changes |
|------|---------|
| `src/Nexus.Core/Models/AppConfig.cs` | DPAPI encrypt/decrypt token, handle migration from plaintext |
| `src/Nexus.Core/Models/ApiResult.cs` | Add `IsRateLimited` property |
| `src/Nexus.Core/Services/NexusApiClient.cs` | 429/401 handling in Login, revoke endpoint, explicit payloads, remove CamelCase policy, pass actual status code |
| `src/Nexus.Core/Nexus.Core.csproj` | Add `System.Security.Cryptography.ProtectedData` NuGet |
| `src/Nexus.Sync/Services/SyncEngine.cs` | File size pre-check, sessionId validation, log ErrorDetail on 422, OnAuthFailed callback |
| `src/Nexus.Sync/Services/TranscriptScanner.cs` | Add comment on acompact skip |
| `src/Nexus.App/App.xaml.cs` | Wire OnAuthFailed callback, call revoke on logout |
| `src/Nexus.App/Views/LoginWindow.xaml.cs` | Handle 429 rate-limit message |
| `c:\xampp\htdocs\nexus\app\Http\Controllers\Api\V1\AuthController.php` | Add logout() method |
| `c:\xampp\htdocs\nexus\routes\api.php` | Add logout route |
| `c:\xampp\htdocs\nexus\config\sanctum.php` | Set token expiry to 30 days |

---

## Verification

1. Build solution — confirm no compilation errors
2. Login with valid credentials — token is stored encrypted in `config.json` (Base64, not readable)
3. Login with invalid credentials — shows "Invalid email or password"
4. Login rapidly 6+ times — shows "Too many attempts" on 429
5. Upload a transcript — verify 202 is handled, activity log shows session_id
6. Upload with intentionally invalid payload — verify 422 error detail appears in activity log
7. Place a 35MB dummy JSONL file in scan directory — verify it is skipped with a warning, not retried
8. Logout from tray — verify server token is revoked (subsequent API call with same token returns 401)
9. Let token expire (or manually set short expiry for testing) — verify login window appears automatically
10. Verify acompact-* files are still skipped and comment is present

---

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| DPAPI migration breaks existing installs | Graceful fallback: if decryption fails on load, clear token and force re-login |
| Sanctum expiry rejects active sessions | 30-day window is generous; auto re-login (1.5) handles expired tokens smoothly |
| Logout endpoint missing auth middleware | Route is inside `auth:sanctum` group — only authenticated users can revoke |

---

## Status: COMPLETE (2026-02-21)

All 15 sub-steps implemented, built, and verified in production. Sync confirmed working (196 sessions visible in dashboard).

- **Server-side** (1.2, 1.4): Deployed to Nexus web (`dev` branch, commit `eef4ff6`)
- **Client-side** (all remaining 12 sub-steps): Implemented, published as self-contained Release build, tested successfully

## Notes

- **Excluded**: Issue 2.4 (parent_transcript_id race condition) is tracked separately as sync pipeline issue #9 PARTIAL.
- **Excluded**: Issue 4.1 (acompact-* skip) is by design — only adding a comment (4.3).

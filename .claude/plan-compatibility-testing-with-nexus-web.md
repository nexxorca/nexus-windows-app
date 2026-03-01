# Windows App — API Compatibility Review

## Context

The Nexus Windows App (`nexus-windows-app`) is the client-side component that uploads Claude Code transcript JSONL files to the Nexus Laravel API. The Nexus server has undergone significant changes (transcript parse logging, code review fixes, API adjustments). This plan ensures the Windows app still works correctly with the latest Nexus API version.

This is a **review-only plan** — no application code is modified. Findings feed into the separate test project plan (`plan-test-project.md`).

---

## Architecture / Approach

```
Windows App (Client)                    Nexus API (Server)
─────────────────────                   ──────────────────
TranscriptScanner
  └─ scans ~/.claude/projects/
TranscriptParser
  └─ extracts sessionId, cwd, hashId
SubagentMapper
  └─ maps tool_use → subagent type
SyncEngine
  └─ NexusApiClient
       ├─ POST /api/v1/auth/login   ──► AuthController@login
       └─ POST /api/v1/transcripts  ──► TranscriptController@store
                                            └─ dispatches ParseTranscript job
LoginWindow
  └─ parses login response JSON (token, user.hash_id, user.first_name)
```

Cross-repo review: client files in `c:\xampp\htdocs\nexus-windows-app\`, server files in `c:\xampp\htdocs\nexus\`.

---

## Steps

### Step 1: API Contract Verification

Verify the Windows app sends exactly what the Nexus API expects, and correctly parses what it returns.

| # | Description | Agent | Action | Files |
|---|-------------|-------|--------|-------|
| 1.1 | Compare NexusApiClient payload fields against StoreTranscriptRequest validation rules | code-reviewer | REVIEW | `src/Nexus.Core/Services/NexusApiClient.cs`, server: `app/Http/Requests/Api/V1/StoreTranscriptRequest.php` |
| 1.2 | Compare login request payload against AuthController expectations | code-reviewer | REVIEW | `src/Nexus.Core/Services/NexusApiClient.cs`, server: `app/Http/Controllers/Api/V1/AuthController.php` |
| 1.3 | Verify login response parsing — LoginWindow extracts token, user.hash_id, user.first_name correctly from AuthController JSON response | code-reviewer | REVIEW | `src/Nexus.App/Views/LoginWindow.xaml.cs`, server: `app/Http/Controllers/Api/V1/AuthController.php` |
| 1.4 | Verify field naming: snake_case vs camelCase consistency between client and server | code-reviewer | REVIEW | `src/Nexus.Core/Services/NexusApiClient.cs` |
| 1.5 | Verify Sanctum token header format matches server middleware expectations | code-reviewer | REVIEW | `src/Nexus.Core/Services/NexusApiClient.cs` |
| 1.6 | Document any mismatches found | code-reviewer | REVIEW | — |

### Step 2: Transcript Payload Compatibility

Verify the transcript content the app sends is parseable by the latest ParseTranscript job.

| # | Description | Agent | Action | Files |
|---|-------------|-------|--------|-------|
| 2.1 | Review what TranscriptParser extracts (sessionId, cwd) and how it becomes the payload | code-reviewer | REVIEW | `src/Nexus.Sync/Services/TranscriptParser.cs` |
| 2.2 | Review SubagentMapper output — subagent_type values match server agent codes | code-reviewer | REVIEW | `src/Nexus.Sync/Services/SubagentMapper.cs`, server: `database/seeders/AgentSeeder.php` |
| 2.3 | Verify main vs subagent payload structure matches server validation rules | code-reviewer | REVIEW | `src/Nexus.Sync/Services/SyncEngine.cs` |
| 2.4 | Verify parent_transcript_id value and semantics — client sends parent sessionId; check what ParseTranscript does with it (lookup by session_id vs DB id?) | code-reviewer | REVIEW | `src/Nexus.Sync/Services/SyncEngine.cs`, server: `app/Jobs/ParseTranscript.php` |
| 2.5 | Verify subagent sessionId format (`{sessionId}-{agentId}`) stays within server's max:100 and matches regex `/^[a-zA-Z0-9_\-]+$/` | code-reviewer | REVIEW | `src/Nexus.Sync/Services/SyncEngine.cs`, server: `app/Http/Requests/Api/V1/StoreTranscriptRequest.php` |

### Step 3: Error Handling Compatibility

Verify the app handles all server response codes correctly.

| # | Description | Agent | Action | Files |
|---|-------------|-------|--------|-------|
| 3.1 | Review 401 handling — auth error stops sync, prompts re-login | code-reviewer | REVIEW | `src/Nexus.Core/Services/NexusApiClient.cs` |
| 3.2 | Review 422 handling — validation errors logged, file skipped | code-reviewer | REVIEW | `src/Nexus.Core/Services/NexusApiClient.cs` |
| 3.3 | Review 202 handling — server returns 202 (not 200) for transcript uploads; verify IsSuccessStatusCode covers this | code-reviewer | REVIEW | `src/Nexus.Core/Services/NexusApiClient.cs` |
| 3.4 | Review timeout and network error handling | code-reviewer | REVIEW | `src/Nexus.Core/Services/NexusApiClient.cs` |
| 3.5 | Review 429 handling — login endpoint is throttled (5/min); client has no specific 429 handler | code-reviewer | REVIEW | `src/Nexus.Core/Services/NexusApiClient.cs`, server: `routes/api.php` |

### Step 4: Known Limitations Audit

Document and categorize known gaps.

| # | Description | Agent | Action | Files |
|---|-------------|-------|--------|-------|
| 4.1 | acompact-* subagent gap — document severity, recommend accept or plan fix | code-reviewer | REVIEW | `src/Nexus.Sync/Services/TranscriptScanner.cs`, `src/Nexus.Sync/Services/SubagentMapper.cs` |
| 4.2 | Sanctum token plaintext storage — assess risk, document for Phase C | security-auditor | REVIEW | `src/Nexus.Core/Models/AppConfig.cs` |
| 4.3 | No token expiry/revocation — assess impact | security-auditor | REVIEW | — |
| 4.4 | ApiResult.Ok() hardcodes StatusCode=200 even though server returns 202 — cosmetic, not a bug | code-reviewer | REVIEW | `src/Nexus.Core/Models/ApiResult.cs` |
| 4.5 | No content size pre-check — server validates max 30MB, client loads entire file and sends without checking; >30MB transcripts rejected with 422 | code-reviewer | REVIEW | `src/Nexus.Sync/Services/SyncEngine.cs`, server: `app/Http/Requests/Api/V1/StoreTranscriptRequest.php` |
| 4.6 | Missing structure.md — create per NEXXOR conventions | doc-writer | CREATE | `.claude/structure.md` |

---

## Detailed Sub-steps

### Step 1 Sub-steps

#### 1.1 Payload Field Comparison

**Files**: `NexusApiClient.cs` (client), `StoreTranscriptRequest.php` (server)

The server expects (from StoreTranscriptRequest):
- `project_hash_id` — required, must exist in projects table
- `session_id` — required, max:100, regex: `/^[a-zA-Z0-9_\-]+$/`
- `type` — required, in:main,subagent
- `content` — required, string, max 30MB
- `subagent_type` — required_if type=subagent, max:50, regex: `/^[a-zA-Z0-9_\-]+$/`
- `parent_transcript_id` — required_if type=subagent, max:100, regex: `/^[a-zA-Z0-9_\-]+$/`

The client sends (via Dictionary with hardcoded snake_case keys):
- `project_hash_id`, `session_id`, `type`, `content` for main transcripts
- Additionally `subagent_type`, `parent_transcript_id` for subagent transcripts

Verify: field names match exactly (snake_case), no extra/missing fields, content vs file upload format compatible.

**Note**: The `_jsonOptions` uses `CamelCase` naming policy, but `UploadTranscript` uses a `Dictionary<string, string?>` — dictionary keys are NOT affected by `PropertyNamingPolicy` in System.Text.Json, so the hardcoded snake_case keys are preserved.

#### 1.3 Login Response Parsing

**Files**: `LoginWindow.xaml.cs` (client), `AuthController.php` (server)

Server returns on success:
```json
{
    "token": "plaintext-sanctum-token",
    "user": {
        "hash_id": "abc123",
        "first_name": "Erik"
    }
}
```

Client parses (LoginWindow.xaml.cs:55-67):
```csharp
var token = root.GetProperty("token").GetString();
var userHashId = root.GetProperty("user").GetProperty("hash_id").GetString();
var userName = root.GetProperty("user").GetProperty("first_name").GetString();
```

Verify: JSON property names match, nesting is correct, no missing fields.

#### 1.4 Field Naming Consistency

C# typically uses PascalCase for properties, but JSON serialization must produce snake_case to match Laravel validation. Two patterns in client:
- `UploadTranscript`: uses `Dictionary<string, string?>` with hardcoded snake_case keys — safe
- `Login`: uses anonymous object `new { email, password }` with CamelCase policy — safe by coincidence (both already lowercase)

Verify neither pattern breaks if extended.

---

## File Summary

### Created Files

| File | Purpose |
|------|---------|
| `.claude/structure.md` | Project structure documentation |

### Modified Files

| File | Changes |
|------|---------|
| `.claude/improvements.md` | Update items based on findings |

---

## Verification

1. Manually upload a transcript via the app to local Nexus (`http://nexus.local`) and confirm it appears in the dashboard
2. Verify subagent transcripts link correctly to parent sessions
3. Check parse logs show Success status for uploaded transcripts
4. Confirm no 422 validation errors in the app's activity log

---

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| API contract changes on server without updating client | Findings from this review inform contract tests in test project plan |
| acompact-* gap may be accepted, making some work moot | Document decision in improvements.md regardless |
| Login response structure changes silently break login | Step 1.3 catches this; contract test in test project plan prevents regression |

---

## Notes

- This plan is review-only — no application code is modified.
- Cross-repo review requires access to both `nexus-windows-app` and `nexus` repos.
- Findings from this review should be addressed before executing the test project plan (`plan-test-project.md`).
- The valid subagent codes in SubagentMapper must stay in sync with the AgentSeeder on the server side.

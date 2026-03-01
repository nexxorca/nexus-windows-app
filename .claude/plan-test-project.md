# Windows App — Test Project Creation

## Context

The Nexus Windows App currently has **zero tests**. This plan creates a foundational test project with unit tests for pure logic and integration tests with HTTP mocking to verify correct API communication.

**Prerequisite**: Complete the compatibility review (`plan-compatibility-testing-with-nexus-web.md`) first — its findings may affect what gets tested and how.

---

## Architecture / Approach

```
Nexus.Tests (xUnit, .NET 8)
├─ Services/
│   ├─ TranscriptParserTests.cs      ← pure logic, no I/O
│   ├─ SubagentMapperTests.cs        ← pure logic, no I/O
│   ├─ TranscriptScannerTests.cs     ← filesystem (temp dirs)
│   ├─ StateManagerTests.cs          ← filesystem (temp dirs)
│   ├─ NexusApiClientTests.cs        ← HTTP mocking
│   └─ SyncEngineTests.cs            ← HTTP mocking
└─ Integration/
    └─ PayloadCompatibilityTests.cs  ← contract tests
```

Testing strategy:
1. **Unit tests** for pure logic (parsing, scanning, mapping) — no server needed
2. **Integration tests** with mock HTTP responses — verify payload format matches what the server expects
3. **Contract tests** — hardcoded expected field names catch client/server drift

---

## Steps

### Step 1: Project Setup

| # | Description | Agent | Action | Files |
|---|-------------|-------|--------|-------|
| 1.1 | Create test project: `Nexus.Tests` (xUnit, .NET 8) | dev | CREATE | `src/Nexus.Tests/Nexus.Tests.csproj` |
| 1.2 | Add Nexus.Tests to solution file | dev | MODIFY | `Nexus.sln` |

### Step 2: Unit Tests — Pure Logic

| # | Description | Agent | Action | Files |
|---|-------------|-------|--------|-------|
| 2.1 | TranscriptParserTests — sessionId extraction, cwd extraction, project hash_id regex | test-creation-dev | CREATE | `src/Nexus.Tests/Services/TranscriptParserTests.cs` |
| 2.2 | SubagentMapperTests — tool_use → subagent mapping, acompact-* skip, valid code list | test-creation-dev | CREATE | `src/Nexus.Tests/Services/SubagentMapperTests.cs` |
| 2.3 | TranscriptScannerTests — file discovery, directory filtering, skip patterns | test-creation-dev | CREATE | `src/Nexus.Tests/Services/TranscriptScannerTests.cs` |
| 2.4 | StateManagerTests — state persistence, file size comparison, skip unchanged | test-creation-dev | CREATE | `src/Nexus.Tests/Services/StateManagerTests.cs` |

### Step 3: Integration Tests — HTTP Mocking

| # | Description | Agent | Action | Files |
|---|-------------|-------|--------|-------|
| 3.1 | NexusApiClientTests — login payload format, transcript upload payload format, auth header | test-creation-dev | CREATE | `src/Nexus.Tests/Services/NexusApiClientTests.cs` |
| 3.2 | SyncEngineTests — full sync cycle with mocked API responses (202, 401, 422) | test-creation-dev | CREATE | `src/Nexus.Tests/Services/SyncEngineTests.cs` |
| 3.3 | PayloadCompatibilityTests — verify payload field names match Nexus server StoreTranscriptRequest rules | test-creation-dev | CREATE | `src/Nexus.Tests/Integration/PayloadCompatibilityTests.cs` |

---

## Detailed Sub-steps

### Step 2 Sub-steps

#### 2.1 TranscriptParserTests

Test cases:
- Extract sessionId from JSONL first line (uuid format)
- Extract cwd from assistant message content
- Extract NEXUS_PROJECT_HASH_ID from CLAUDE.md content
- Handle missing CLAUDE.md gracefully
- Handle malformed JSONL lines
- Path fallback (c:\xampp\htdocs\ ↔ c:\xampp7\htdocs\)

#### 2.2 SubagentMapperTests

Test cases:
- Map tool_use Task block with subagent_type to correct agent code
- Match tool_result with agentId back to tool_use
- Skip acompact-* prefixed files
- Valid agent codes: dev, tech-lead, tester, test-creation-dev, code-reviewer, database-specialist, security-auditor, doc-writer
- Skip types: Explore, Plan, claude-code-guide, Bash, general-purpose, statusline-setup
- Handle missing tool_result for a tool_use gracefully

### Step 3 Sub-steps

#### 3.1 NexusApiClientTests

Requires `HttpMessageHandler` mocking (standard .NET pattern — no DI refactor needed).

Test cases:
- Login sends correct JSON payload (`email`, `password`) to `/api/v1/auth/login`
- Upload sends correct multipart/JSON payload to `/api/v1/transcripts`
- Bearer token is set in Authorization header after login
- 401 response sets `IsAuthError = true`
- 422 response sets `IsValidationError = true`
- Timeout returns `ApiResult.Fail()`, doesn't throw

#### 3.3 PayloadCompatibilityTests

Hardcode the exact field names the server expects and assert the client produces them. This acts as a contract test — if either side changes field names, the test breaks.

---

## File Summary

### Created Files

| File | Purpose |
|------|---------|
| `src/Nexus.Tests/Nexus.Tests.csproj` | xUnit test project targeting .NET 8 |
| `src/Nexus.Tests/Services/TranscriptParserTests.cs` | Unit tests for JSONL parsing logic |
| `src/Nexus.Tests/Services/SubagentMapperTests.cs` | Unit tests for subagent identification |
| `src/Nexus.Tests/Services/TranscriptScannerTests.cs` | Unit tests for file discovery |
| `src/Nexus.Tests/Services/StateManagerTests.cs` | Unit tests for sync state tracking |
| `src/Nexus.Tests/Services/NexusApiClientTests.cs` | Integration tests with HTTP mocking |
| `src/Nexus.Tests/Services/SyncEngineTests.cs` | Integration tests for sync cycle |
| `src/Nexus.Tests/Integration/PayloadCompatibilityTests.cs` | Contract tests for API payload format |

### Modified Files

| File | Changes |
|------|---------|
| `Nexus.sln` | Add Nexus.Tests project reference |

---

## Verification

1. Run `dotnet test` — all new tests pass
2. No build warnings in test project
3. Contract tests (3.3) match the field names confirmed in the compatibility review

---

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| NexusApiClient uses HttpClient directly — hard to mock without DI | Use HttpMessageHandler mocking (standard .NET test pattern, no refactor needed) |
| TranscriptScanner/StateManager need filesystem — tests may be slow | Use temp directories, clean up in teardown |
| API contract changes on server without updating client | PayloadCompatibilityTests catch this — run after any Nexus API changes |
| acompact-* gap may be accepted, making some tests moot | Document decision in improvements.md regardless |

---

## Notes

- The SyncEngine currently has tight coupling (no interfaces) — mocking requires HttpMessageHandler injection, not full DI. This is sufficient for Phase B. Full DI refactor is Phase C.
- The valid subagent codes in SubagentMapper must stay in sync with the AgentSeeder on the server side. The contract test (3.3) catches drift.

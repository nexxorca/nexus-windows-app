# Address acompact-* Subagent Gap — Implementation Plan

## Context

When a Claude Code conversation hits the context limit, it gets "compacted" — older messages are summarized and replaced. Subagent transcripts spawned after compaction produce files named `acompact-{hash}.jsonl`. These files are currently skipped entirely by the Windows app, meaning subagent work from long sessions never reaches Nexus. This is a known data completeness gap affecting ~10-20% of marathon sessions.

---

## Architecture / Approach

Two-part fix with a fallback strategy:

1. **Primary**: Persist the `agentId → subagent_type` mapping to `sync-state.json` as it's discovered during normal sync cycles. When `acompact-*` files appear, look up the agent type from historical state.
2. **Fallback**: If the `acompact-*` hash doesn't match any known agentId (unknown until we inspect real samples), scan the subagent transcript's first few lines for agent type clues (the system prompt typically contains the agent type).

```
Normal sync cycle:
  SubagentMapper builds agentId → type map
        │
        ├── Upload subagent files (existing behavior)
        └── Persist mapping to sync-state.json (NEW)

Post-compaction sync cycle:
  TranscriptScanner finds acompact-*.jsonl (no longer skipped)
        │
        ▼
  Check historical mapping for matching agentId
        │
        ├── Found → upload with known type
        └── Not found → scan transcript content for agent type (fallback)
```

---

## Steps

### Step 0: Investigate acompact-* File Format

Determine whether the hash in `acompact-{hash}.jsonl` is the original agentId or a new identifier. This decides whether the primary approach alone is sufficient or the fallback is needed.

| # | Description | Agent | Action | Files |
|---|-------------|-------|--------|-------|
| 0.1 | Scan local `~/.claude/projects/` for any existing `acompact-*` files | dev | RUN | — |
| 0.2 | If found, compare the hash against agentIds in the parent transcript's tool_use/tool_result blocks | dev | REVIEW | — |
| 0.3 | Document findings — is the hash the original agentId or a new one? | dev | REVIEW | — |

### Step 1: Persist Agent Mapping in State

Save discovered `agentId → subagent_type` mappings so they survive across sync cycles.

| # | Description | Agent | Action | Files |
|---|-------------|-------|--------|-------|
| 1.1 | Add `AgentMappings` dictionary to `SyncState` model | dev | MODIFY | `src/Nexus.Sync/Models/SyncState.cs` |
| 1.2 | Update `StateManager` to read/write agent mappings alongside file state | dev | MODIFY | `src/Nexus.Sync/Services/StateManager.cs` |
| 1.3 | Update `SubagentMapper` to persist discovered mappings after each parse | dev | MODIFY | `src/Nexus.Sync/Services/SubagentMapper.cs` |

### Step 2: Stop Skipping acompact-* Files

Allow the scanner to pick up compacted subagent transcripts.

| # | Description | Agent | Action | Files |
|---|-------------|-------|--------|-------|
| 2.1 | Remove `acompact-*` skip logic from `TranscriptScanner` | dev | MODIFY | `src/Nexus.Sync/Services/TranscriptScanner.cs` |
| 2.2 | Add logic to identify `acompact-*` files as subagent transcripts needing agent resolution | dev | MODIFY | `src/Nexus.Sync/Services/TranscriptScanner.cs` |

### Step 3: Resolve Agent Type for acompact-* Files

Match compacted subagent files to their agent type using historical mappings or content scanning.

| # | Description | Agent | Action | Files |
|---|-------------|-------|--------|-------|
| 3.1 | Add historical mapping lookup in `SubagentMapper` — check `SyncState.AgentMappings` for known agentId | dev | MODIFY | `src/Nexus.Sync/Services/SubagentMapper.cs` |
| 3.2 | Add fallback: scan first ~30 lines of transcript for agent type clues in system prompt (only if Step 0 shows hash ≠ agentId) | dev | MODIFY | `src/Nexus.Sync/Services/SubagentMapper.cs` |
| 3.3 | Integrate into `SyncEngine` — process `acompact-*` files with resolved agent type, upload as subagent transcripts | dev | MODIFY | `src/Nexus.Sync/Services/SyncEngine.cs` |

### Step 4: Tests

| # | Description | Agent | Action | Files |
|---|-------------|-------|--------|-------|
| 4.1 | `StateManagerTests` — verify agent mappings are persisted and loaded correctly | test-creation-dev | CREATE | `src/Nexus.Tests/Services/StateManagerTests.cs` |
| 4.2 | `SubagentMapperTests` — verify historical mapping lookup, verify fallback content scanning | test-creation-dev | CREATE | `src/Nexus.Tests/Services/SubagentMapperTests.cs` |
| 4.3 | `TranscriptScannerTests` — verify `acompact-*` files are no longer skipped | test-creation-dev | CREATE | `src/Nexus.Tests/Services/TranscriptScannerTests.cs` |

---

## Detailed Sub-steps

### Step 0 Sub-steps

#### 0.1 Find acompact-* Samples

Scan `C:\Users\erikl\.claude\projects\` recursively for `acompact-*.jsonl` files. If none exist locally, trigger a long conversation that hits compaction, or check other dev machines.

#### 0.2 Compare Hash vs AgentId

If samples found, open the parent transcript (the main session file in the same directory). Search for `tool_use` blocks with `name: "Task"` and the corresponding `tool_result` blocks with `agentId:`. Check if the `{hash}` in the `acompact-{hash}.jsonl` filename matches any `agentId` from those blocks.

**If match**: Primary approach (historical mapping) is sufficient. Skip step 3.2.
**If no match**: Fallback (content scanning) is required. Implement step 3.2.

### Step 1 Sub-steps

#### 1.1 SyncState Model Update

**File**: `src/Nexus.Sync/Models/SyncState.cs`

Add a dictionary property:
```csharp
public Dictionary<string, string> AgentMappings { get; set; } = new();
```

This maps `agentId → subagent_type` (e.g., `"a1b2c3d" → "dev"`). Serialized to `sync-state.json` alongside existing file state.

#### 1.3 SubagentMapper Persistence

**File**: `src/Nexus.Sync/Services/SubagentMapper.cs`

After the two-pass parse that builds the in-memory map, merge new discoveries into `SyncState.AgentMappings` and call `StateManager.Save()`. Existing mappings are preserved — new ones are added, never overwritten.

### Step 3 Sub-steps

#### 3.2 Content Scanning Fallback

**File**: `src/Nexus.Sync/Services/SubagentMapper.cs`

If the historical mapping doesn't match, read the first ~30 lines of the `acompact-*` file and look for:
- A `system` role entry containing the agent type in its prompt text (e.g., `"You are a dev agent"`, `"subagent_type: code-reviewer"`)
- Match against the known valid agent codes: `dev`, `tech-lead`, `tester`, `test-creation-dev`, `code-reviewer`, `database-specialist`, `security-auditor`, `doc-writer`

If no match found, log a warning and skip the file (same as today — no regression).

---

## File Summary

### Modified Files

| File | Changes |
|------|---------|
| `src/Nexus.Sync/Models/SyncState.cs` | Add `AgentMappings` dictionary |
| `src/Nexus.Sync/Services/StateManager.cs` | Read/write agent mappings in state file |
| `src/Nexus.Sync/Services/SubagentMapper.cs` | Persist mappings, add historical lookup, add content scan fallback |
| `src/Nexus.Sync/Services/TranscriptScanner.cs` | Remove `acompact-*` skip, flag these files for agent resolution |
| `src/Nexus.Sync/Services/SyncEngine.cs` | Process `acompact-*` files through resolution + upload |

### Created Files

| File | Purpose |
|------|---------|
| `src/Nexus.Tests/Services/StateManagerTests.cs` | Agent mapping persistence tests |
| `src/Nexus.Tests/Services/SubagentMapperTests.cs` | Historical lookup + fallback tests |
| `src/Nexus.Tests/Services/TranscriptScannerTests.cs` | acompact-* inclusion tests |

---

## Verification

1. Run Step 0 investigation — confirm whether hash = agentId or not
2. Run `dotnet test` — all new and existing tests pass
3. Trigger a real compacted session (long conversation) and verify:
   - `acompact-*` files are picked up by the scanner
   - Agent type is resolved (via mapping or fallback)
   - Subagent session appears in Nexus linked to correct parent
4. Verify non-compacted sessions still work identically (no regression)

---

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| `acompact-*` hash is a new identifier, not the original agentId | Step 0 investigation determines this upfront. Fallback (Step 3.2) handles this case. |
| `sync-state.json` grows unbounded with old mappings | Add a TTL or max-entries cleanup — purge mappings older than 30 days. Low priority, can be deferred. |
| Content scanning fallback is fragile if Claude Code changes system prompt format | Match against known agent codes only — a fixed list. If format changes, the worst case is skipping the file (same as today). |
| Breaking existing sync behavior | `acompact-*` files were already skipped — any new behavior is additive. Existing files continue to work unchanged. |

---

## Notes

- Step 0 is a prerequisite — its outcome determines whether Step 3.2 (fallback) is needed.
- This plan only affects the Windows app. No changes needed on the Nexus server side — it already handles subagent transcripts correctly regardless of how the client resolves the agent type.
- The valid agent codes list in `SubagentMapper` must stay in sync with `AgentSeeder` on the server. This is an existing maintenance requirement, not new.

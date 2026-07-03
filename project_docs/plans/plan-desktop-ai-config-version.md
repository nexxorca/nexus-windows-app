<!-- plan-desktop-ai-config-version.md | nexus-windows-app/project_docs/plans/plan-desktop-ai-config-version.md -->

> Author: Erik | Agent: tech-lead | Created: 2026-07-03 | Status: Proposed

# Desktop "AI Config" Version Line — Implementation Plan

## Context

Add one line to the main panel, under "Version:", showing the AI Config manifest version (the `version` field of the server manifest, currently `1.0.13`). The value already reaches the client: `TriggerAiConfigCheck` fetches the manifest and `AiConfigManifest.Version` holds it. It just isn't displayed.

## Approach

Keep it in memory. Hold the last-fetched version on `App`, set it whenever a manifest fetch succeeds, and read it in `MainWindow.RefreshState()`. No `config.json` field, no persistence — the AI Config check already runs on startup and after login, so the value repopulates within seconds of every launch. Before the first check completes on a cold start the line shows `—`; that flash is acceptable for a greyed status line and is the deliberate trade for keeping this simple.

## Precondition

The working tree carries uncommitted 1.2.8/1.2.9 work and the last commit is 1.2.5. Commit/push that first so this change lands on a clean baseline — do not start until the tree is clean.

## Steps

### Step 1: Expose the version on App

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 1.1 | Add an in-memory `LastAiConfigVersion` (nullable string) property on `App`, readable by `MainWindow`. In `TriggerAiConfigCheck`, right after the successful fetch (`var manifest = result.Data!`, [App.xaml.cs:193](src/Nexus.App/App.xaml.cs#L193)), set it to `manifest.Version` and call `_mainWindow?.RefreshState()`. One insertion point covers every success path (up-to-date, declined, applied). | dev | MODIFY | `src/Nexus.App/App.xaml.cs` | TODO |

### Step 2: Display the line

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 2.1 | Add a state-panel row under the "Version:" row (new `RowDefinition`; mirror the label + value styling at [MainWindow.xaml:41-42](src/Nexus.App/MainWindow.xaml#L41-L42)). Label reads "AI Config:". | dev | MODIFY | `src/Nexus.App/MainWindow.xaml` | TODO |
| 2.2 | In `RefreshState()`, set the new value TextBlock from `_app.LastAiConfigVersion`, with a `—` fallback when null. Mirror the `lblVersion` assignment at [MainWindow.xaml.cs:39](src/Nexus.App/MainWindow.xaml.cs#L39). | dev | MODIFY | `src/Nexus.App/MainWindow.xaml.cs` | TODO |

### Step 3: Version + changelog

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 3.1 | Bump `<Version>` 1.2.9 → 1.3.0 (new user-facing feature) and add a CHANGELOG `Added` entry. | dev | MODIFY | `src/Nexus.App/Nexus.App.csproj`, `CHANGELOG.md` | TODO |

### Step 4 (final — always last): Close-out

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 4.1 | Propose close-out checklist (chat only). | tech-lead | RUN | — | TODO |

---

## Verification

1. Open the panel before any AI Config check → "AI Config:" shows `—`.
2. Click "Check AI Config" against a server with a snapshot → line updates to the manifest version (e.g. `1.0.13`); no restart needed.
3. Confirm the app's own "Version:" line is unchanged and the new line sits directly below it.

## Notes

- No `AppConfig`/`config.json` change and no persistence file — value is in memory only.
- Cross-project origin: scoped from a Nexus session; the Nexus API needs no change.

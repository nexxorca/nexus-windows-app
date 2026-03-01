# Plan: Velopack Auto-Update Distribution

## Context
The Nexus Windows App is distributed manually. We need a proper installer and auto-update mechanism. **Velopack** handles the installer + update engine. **Nexus server** hosts releases and serves version info — the app never touches GitHub. A release script automates the full build → pack → upload flow.

## Architecture

```
Developer machine                    Nexus server                     User machine
─────────────────                    ────────────                     ────────────
release.sh 1.0.1                     POST /api/v1/app/releases        App startup
  → dotnet publish                     → stores files in              → GET /api/v1/app/latest-release
  → vpk pack                             storage/app/releases/        → compares version
  → curl POST files to Nexus API       → updates version record       → Velopack downloads + applies
```

---

## Phase 1: Velopack Integration (nexus-windows-app)

### Step 1.1 — Add version + Velopack NuGet package
**Agent**: dev

- Add `<Version>1.0.0</Version>` to `src/Nexus.App/Nexus.App.csproj`
- Change `App.xaml` build action: swap `ApplicationDefinition` → `Page`, add `<StartupObject>Nexus.App.App</StartupObject>`
- Install: `dotnet add src/Nexus.App/Nexus.App.csproj package Velopack`

**Files**:
- `src/Nexus.App/Nexus.App.csproj`

### Step 1.2 — Add custom Main() entry point
**Agent**: dev

Modify `App.xaml.cs`:
- Add `[STAThread] static void Main(string[] args)` method
- First line: `VelopackApp.Build().Run()` (before any WPF init)
- Then: `var app = new App(); app.InitializeComponent(); app.Run();`
- Existing `OnStartup` logic stays as-is

**Files**:
- `src/Nexus.App/App.xaml.cs`

### Step 1.3 — Add update-check logic
**Agent**: dev

Add update checking that runs after successful login or on startup when already logged in:
- Call `NexusApiClient.CheckForUpdateAsync(currentVersion)` → returns `{ version, downloadUrl }` or null
- If update available: use Velopack `UpdateManager` with the download URL
- Download silently, prompt user to restart (or apply on next exit)
- All errors silently caught — update failure must never break the app
- Fire-and-forget — don't block the UI

Add to `NexusApiClient`:
- `CheckForUpdateAsync(string currentVersion)` → calls `GET /api/v1/app/latest-release?current_version=X`
- Returns version + download URL if newer, null if current

**Files**:
- `src/Nexus.App/App.xaml.cs` (wire the check after login / on startup)
- `src/Nexus.Core/Services/NexusApiClient.cs` (new method)

---

## Phase 2: Nexus Web API — Release Hosting (nexus Laravel project)

> **DELEGATED** — Implemented in the `nexus` project.
> Plan: `c:\xampp\htdocs\nexus\project_docs\plans\plan-app-release-hosting.md`
> Verify completion against that plan before integrating Phase 1 Step 1.3 (update-check logic).

---

## Phase 3: Release Script (nexus-windows-app)

### Step 3.1 — Create release script
**Agent**: dev

Create `release.sh` in project root:
```bash
#!/bin/bash
# Usage: ./release.sh 1.0.1
VERSION=$1
NEXUS_URL="https://your-nexus-url"
NEXUS_TOKEN="your-admin-token"

dotnet publish src/Nexus.App/Nexus.App.csproj -c Release -r win-x64 --self-contained -o publish
vpk pack -u NexusApp -v $VERSION -p ./publish -e Nexus.App.exe --packTitle "Nexus" --packAuthors "NEXXOR inc." --icon src/Nexus.App/Resources/nexus.ico
curl -X POST "$NEXUS_URL/api/v1/app/releases" \
  -H "Authorization: Bearer $NEXUS_TOKEN" \
  -F "version=$VERSION" \
  -F "file=@./Releases/NexusApp-$VERSION-full.nupkg"
```

**Files**: `release.sh` (new, project root)

---

## Phase 4: Install vpk CLI + Test

### Step 4.1 — Install vpk
```bash
dotnet tool install -g vpk
```

### Step 4.2 — End-to-end test
1. Build + pack locally
2. Upload via release script
3. Install Setup.exe on a test machine
4. Bump version → build → upload → verify auto-update triggers

---

## Constraints
- Do NOT use `PublishTrimmed` or `SingleFile` — both break Velopack
- Config is already in `%AppData%\Nexus\` — safe from Velopack's app directory wipe
- Code signing skipped for now (internal tool)
- Update check must be non-blocking and fail-silent
- The upload endpoint should be admin-only (not accessible to regular app users)
- `releases.win.json` (Velopack index file) may also need to be served — TBD during implementation

## Verification
1. `dotnet build` compiles without errors
2. `vpk pack` produces Setup.exe and .nupkg files
3. Setup.exe installs to `%LocalAppData%\NexusApp\`
4. App starts, tray icon appears, login works
5. `POST /api/v1/app/releases` accepts and stores the file
6. `GET /api/v1/app/latest-release` returns correct version info
7. Bump to 1.0.1, upload, verify app detects and applies update

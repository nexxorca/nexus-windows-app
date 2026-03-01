# Nexus Windows App — Releasing

## Prerequisites

- **vpk CLI** installed globally: `dotnet tool install -g vpk`
- **Git Bash** (standalone, not VSCode terminal) — required for curl upload progress to display
- Access to the Nexus server (Forge) to add env variables if needed

---

## Release Steps

### 1. Open standalone Git Bash

Open **Git Bash** from the Start menu or taskbar (not from within VSCode). The VSCode integrated terminal does not render curl's upload progress.

### 2. Navigate to the project root

```bash
cd /c/xampp/htdocs/nexus-windows-app
```

### 3. Run the release script

```bash
bash release.sh 1.0.1
```

Replace `1.0.1` with the new version number (semver: `MAJOR.MINOR.PATCH`).

---

## What the Script Does

1. **Build** — `dotnet publish` compiles a self-contained win-x64 release into `./publish/`
2. **Clean** — removes any existing `.nupkg` / `.exe` for this version from `./Releases/`
3. **Pack** — `vpk pack` produces `./Releases/NexusApp-{VERSION}-full.nupkg` (and `Setup.exe`)
4. **Upload** — `curl` POSTs the `.nupkg` to `POST /api/v1/app/releases` on the Nexus server

On success, the server returns `{"version":"1.0.1"}` and marks it as `is_latest` in the DB.

---

## New User Installation

`Setup.exe` is produced by `vpk pack` in `./Releases/` but is **not uploaded to the server**. To install on a new machine:

1. After running `release.sh`, locate `./Releases/Setup.exe`
2. Share it directly with the user (email, Teams, shared drive, etc.)
3. The user runs `Setup.exe` — it installs to `%LocalAppData%\NexusApp\`
4. On subsequent startups, the app auto-updates itself via the Nexus API

> A hosted download link is tracked as a future improvement (`improvements.md`).

---

## Notes

- The `RELEASE_SECRET` in `release.sh` must match `RELEASE_SECRET` in the server's `.env` (set via Forge)
- `set -e` is active — the script aborts on any failure (build error, vpk error, upload error)
- `--fail` on curl causes a non-zero exit on HTTP 4xx/5xx
- The `[15:53:04 WRN] No signing parameters provided` warning from vpk is expected — no code signing cert yet (see `improvements.md`)
- `.nupkg` is a renamed ZIP — ~70MB for the current build

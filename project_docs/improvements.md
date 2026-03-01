# Nexus Windows App — Improvements

## Hosted Setup.exe Download

**Current**: `Setup.exe` (new-user installer) is produced locally by `vpk pack` but not uploaded to the server. New users receive it via manual distribution (email / shared drive).
**Improvement**: Upload `Setup.exe` to the Nexus server alongside the `.nupkg` during `release.sh`. Add a `GET /api/v1/app/releases/setup/download` endpoint (or similar) so new users get a stable download link. The `is_latest` record would also store the Setup.exe filename.

---

## Code Signing

**Current**: No code signing — vpk packs without a certificate. Windows SmartScreen will warn on first install.
**Improvement**: Purchase an EV code signing certificate for NEXXOR inc. One cert covers all NEXXOR executables (internal tools + client deliverables). Pass the cert to `vpk pack` via `--signParams`. Eliminates SmartScreen warnings and shows "NEXXOR inc." in the Windows security prompt.

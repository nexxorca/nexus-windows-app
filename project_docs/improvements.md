# Nexus Windows App — Improvements

## Automated Setup.exe Upload

**Current**: `NexusApp-win-Setup.exe` is hosted manually at `https://www.nexxor.ca/docs/nexus/NexusApp-win-Setup.exe`. The PM must re-upload the file after each `release.sh` run to keep the link current.
**Improvement**: Extend `release.sh` (or add a server endpoint) to upload the new `NexusApp-win-Setup.exe` automatically when a release is shipped, so the hosted link is always current without manual intervention.

---

## Code Signing

**Current**: No code signing — vpk packs without a certificate. Windows SmartScreen will warn on first install.
**Improvement**: Purchase an EV code signing certificate for NEXXOR inc. One cert covers all NEXXOR executables (internal tools + client deliverables). Pass the cert to `vpk pack` via `--signParams`. Eliminates SmartScreen warnings and shows "NEXXOR inc." in the Windows security prompt.

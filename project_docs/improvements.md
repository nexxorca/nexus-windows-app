# Nexus Windows App — Improvements

## Automated Setup.exe Upload

**Current**: `NexusApp-win-Setup.exe` is hosted manually at `https://www.nexxor.ca/docs/nexus/NexusApp-win-Setup.exe`. The PM must re-upload the file after each `release.sh` run to keep the link current.
**Improvement**: Extend `release.sh` (or add a server endpoint) to upload the new `NexusApp-win-Setup.exe` automatically when a release is shipped, so the hosted link is always current without manual intervention.

---

## Code Signing

**Current**: No code signing — vpk packs without a certificate. Windows SmartScreen will warn on first install.
**Improvement**: Purchase an EV code signing certificate for NEXXOR inc. One cert covers all NEXXOR executables (internal tools + client deliverables). Pass the cert to `vpk pack` via `--signParams`. Eliminates SmartScreen warnings and shows "NEXXOR inc." in the Windows security prompt.

---

## AI Config Manifest Signature

**Current**: AI Config Sync trusts whoever has manifest-write access on Nexus. HTTPS + Sanctum bearer is the only trust chain. The SHA-256 in the manifest only verifies transport — a compromised server (or leaked admin token) controls both manifest and file bytes, so verification passes against malicious payloads. Blast radius: arbitrary file write into `~/.claude/` on every dev machine, including hooks (which execute) and agent prompts (which steer Claude Code) — effectively remote code execution.
**Improvement**: PM holds a private signing key offline (USB key or password-protected file). `/sync-claude` signs the manifest JSON at publish time. Nexus stores the signature alongside the manifest. Desktop has the matching public key embedded at build time; verifies the signature before any apply. No valid signature → refuse. Cross-project work (PM workflow + Nexus storage + desktop verification). Algorithm: Ed25519 (.NET 8 ships native support; 32-byte public key, 64-byte signature). Key rotation requires a desktop release, so the initial key is the crown jewel — back it up, keep it offline. Until this lands, treat the AI Config Sync channel as "trusted ops only", not production-grade.

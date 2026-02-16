# Nexus Windows App — Improvements & Known Limitations

## Auth: Mixed API Authentication

**Current**: The transcript endpoint (`POST /api/v1/transcripts`) uses Laravel Sanctum user auth, while all other API routes (sessions, instructions, activities) use `AuthenticateAgent` middleware with agent API tokens.

**Why**: The desktop app only calls the transcript endpoint, so only that route was migrated. Other routes are called by different systems (Claude hooks, etc.) that use agent tokens.

**Improvement**: Consider unifying all API routes under one auth system. Options:
- Migrate all routes to Sanctum (breaking change for existing agent-token consumers)
- Support dual auth on all routes (middleware that accepts either token type)
- Keep as-is if the separation remains clean

**Risk if ignored**: Two auth systems to maintain. New endpoints need a conscious decision about which auth to use.

---

## Auth: Sanctum Token Stored as Plaintext

**Current**: The Sanctum token is stored in `%APPDATA%\Nexus\config.json` as plaintext.

**Why**: Phase A — internal tool used by a handful of NEXXOR developers on their own machines. Same security profile as the agent API token it replaced.

**Improvement**: Encrypt with Windows DPAPI (`ProtectedData.Protect()`). Token is encrypted per-user, at rest only. Tagged as S1 in reassessment, deferred to Phase C.

---

## Auth: Token Lifetime & Revocation

**Current**: Sanctum tokens are long-lived (no expiry by default). Revocation is manual — either via Logout in the app or by deleting the token from the `personal_access_tokens` table.

**Why**: Internal tool — short-lived tokens would mean frequent re-logins, which is worse UX than the risk warrants.

**Improvement**: Options for the future:
- Set token expiry in Sanctum config (e.g., 30 days) with automatic re-login prompt
- Add a "Revoke all tokens" button in the Nexus admin panel for when a developer leaves
- Add `last_used_at` monitoring to detect stale tokens

---

## Auth: No Password Stored Locally

**Note**: The app never stores the user's password. Only the Sanctum token (returned by the login API) is persisted. The password is sent once during login over HTTPS, then discarded.

---

## Future: Send Activity Log to Web App

**Current**: Activity log is local-only (`%APPDATA%\Nexus\activity.log` + in-memory for UI).

**Idea** (from user): In the future, send activity log data to the Nexus web app so management can view each developer's desktop app status remotely. Would require a new API endpoint and a dashboard view.

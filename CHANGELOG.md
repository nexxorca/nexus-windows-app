# Changelog

## [1.0.10] - 2026-03-16

### Fixed
- Sync engine no longer re-uploads active session files (defers files modified within last 5 minutes)
- Upload timeout increased from 120s to 300s to prevent false timeouts on larger transcripts via S3
- Failed upload errors now include the filename in the activity log for easier debugging
- Added 1 retry with 5s backoff for failed transcript uploads (re-reads file between attempts)

### Added
- `Dev branch: dev` config line in CLAUDE.md for push-dev skill
- `project_docs/structure.md` — full project structure documentation

namespace Nexus.Sync.Models;

public record SyncResult(
    int Uploaded,
    int Skipped,
    int ParseSkipped,
    int Errors,
    DateTime CompletedAt,
    string Status   // "ok" | "error" — matches existing activity log convention
);

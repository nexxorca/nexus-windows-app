namespace Nexus.Sync.Models;

// Status: "applied" | "rolled_back" | "aborted" | "skipped_no_snapshot" | "skipped_no_install"
public record AiConfigApplyResult(
    bool Success,
    string Status,
    string? Version,
    string? Message
);

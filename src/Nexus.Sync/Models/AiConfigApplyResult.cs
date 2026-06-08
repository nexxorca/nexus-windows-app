namespace Nexus.Sync.Models;

public record AiConfigApplyResult(
    bool Success,
    AiConfigApplyStatuses Status,
    string? Version,
    string? Message
);

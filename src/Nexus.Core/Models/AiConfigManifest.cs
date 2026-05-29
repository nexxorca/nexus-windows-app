namespace Nexus.Core.Models;

public record AiConfigManifest(
    string Version,
    DateTime GeneratedAt,
    IReadOnlyList<AiConfigManifestFile> Files
);

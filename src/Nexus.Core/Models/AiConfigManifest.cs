using System.Text.Json.Serialization;

namespace Nexus.Core.Models;

public record AiConfigManifest(
    string Version,
    DateTime GeneratedAt,
    [property: JsonPropertyName("managed_roots")] string[]? ManagedRoots,
    IReadOnlyList<AiConfigManifestFile> Files
);

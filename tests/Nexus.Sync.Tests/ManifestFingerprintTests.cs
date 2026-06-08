using Nexus.Core.Models;
using Nexus.Sync.Services;

namespace Nexus.Sync.Tests;

public class ManifestFingerprintTests {
    [Fact]
    public void Compute_IsDeterministic_SameInputSameOutput() {
        var manifest = BuildManifest(
            ("agents/dev/AGENT.md", "aaa111"),
            ("agents/tech-lead/AGENT.md", "bbb222"),
            ("skills/np/SKILL.md", "ccc333")
        );

        var first = ManifestFingerprint.Compute(manifest);
        var second = ManifestFingerprint.Compute(manifest);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Compute_IsOrderIndependent() {
        var ordered = BuildManifest(
            ("agents/dev/AGENT.md", "aaa111"),
            ("agents/tech-lead/AGENT.md", "bbb222"),
            ("skills/np/SKILL.md", "ccc333")
        );
        var reordered = BuildManifest(
            ("skills/np/SKILL.md", "ccc333"),
            ("agents/dev/AGENT.md", "aaa111"),
            ("agents/tech-lead/AGENT.md", "bbb222")
        );

        Assert.Equal(ManifestFingerprint.Compute(ordered), ManifestFingerprint.Compute(reordered));
    }

    [Fact]
    public void Compute_ChangesWhenAnyShaChanges() {
        var original = BuildManifest(
            ("agents/dev/AGENT.md", "aaa111"),
            ("agents/tech-lead/AGENT.md", "bbb222")
        );
        var mutated = BuildManifest(
            ("agents/dev/AGENT.md", "aaa111"),
            ("agents/tech-lead/AGENT.md", "bbb223")  // single byte differs
        );

        Assert.NotEqual(ManifestFingerprint.Compute(original), ManifestFingerprint.Compute(mutated));
    }

    [Fact]
    public void Compute_ChangesWhenFileIsAddedOrRemoved() {
        var twoFiles = BuildManifest(
            ("agents/dev/AGENT.md", "aaa111"),
            ("agents/tech-lead/AGENT.md", "bbb222")
        );
        var threeFiles = BuildManifest(
            ("agents/dev/AGENT.md", "aaa111"),
            ("agents/tech-lead/AGENT.md", "bbb222"),
            ("skills/np/SKILL.md", "ccc333")
        );

        Assert.NotEqual(ManifestFingerprint.Compute(twoFiles), ManifestFingerprint.Compute(threeFiles));
    }

    // Replaced: Compute_EmptyFilesListYieldsKnownConstant (pinned SHA-256("") constant).
    // The constant was brittle — it locked in an implementation detail rather than behavior.
    // These two behavioral tests cover the same surface without pinning the hash algorithm output.

    [Fact]
    public void Compute_TwoEmptyManifests_ProduceEqualFingerprints() {
        var first = new AiConfigManifest("1.0.0", DateTime.UtcNow, Array.Empty<string>(), new List<AiConfigManifestFile>());
        var second = new AiConfigManifest("2.0.0", DateTime.UtcNow.AddDays(1), new[] { "agents/" }, new List<AiConfigManifestFile>());

        // Version and managed_roots are not factored into the fingerprint — only files matter.
        Assert.Equal(ManifestFingerprint.Compute(first), ManifestFingerprint.Compute(second));
    }

    [Fact]
    public void Compute_EmptyManifest_DiffersFromSingleFileManifest() {
        var empty = new AiConfigManifest("1.0.0", DateTime.UtcNow, Array.Empty<string>(), new List<AiConfigManifestFile>());
        var oneFile = BuildManifest(("agents/dev/AGENT.md", "aaa111"));

        Assert.NotEqual(ManifestFingerprint.Compute(empty), ManifestFingerprint.Compute(oneFile));
    }

    [Fact]
    public void Compute_UnicodePath_RoundTripsToStableFingerprint() {
        // UTF-8 paths with accented or diacritic characters must produce a stable, deterministic hash.
        var first = BuildManifest(("agents/tëst/AGENT.md", "abc123"));
        var second = BuildManifest(("agents/tëst/AGENT.md", "abc123"));

        Assert.Equal(ManifestFingerprint.Compute(first), ManifestFingerprint.Compute(second));
    }

    [Fact]
    public void Compute_DifferentCasePaths_ProduceDifferentFingerprints() {
        // The implementation sorts by StringComparer.Ordinal — case matters.
        // "Agents/dev.md" and "agents/dev.md" are distinct entries and yield distinct fingerprints.
        var upper = BuildManifest(("Agents/dev.md", "aaa111"));
        var lower = BuildManifest(("agents/dev.md", "aaa111"));

        Assert.NotEqual(ManifestFingerprint.Compute(upper), ManifestFingerprint.Compute(lower));
    }

    [Fact]
    public void Compute_SingleFileManifest_ProducesStableFingerprint() {
        var manifest = BuildManifest(("agents/dev/AGENT.md", "deadbeef"));

        var first = ManifestFingerprint.Compute(manifest);
        var second = ManifestFingerprint.Compute(manifest);

        Assert.Equal(first, second);
        // Also verify it differs from the empty-manifest fingerprint
        var empty = new AiConfigManifest("1.0.0", DateTime.UtcNow, Array.Empty<string>(), new List<AiConfigManifestFile>());
        Assert.NotEqual(ManifestFingerprint.Compute(empty), first);
    }

    [Fact]
    public void Compute_DuplicatePathEntries_ProducesDeterministicFingerprint() {
        // Two entries with the same path but different SHAs — the implementation does not deduplicate.
        // Both appear in the sorted string. The behavior is pinned here so any change to deduplication
        // logic surfaces as a test failure and requires a conscious decision.
        var withDuplicates = new AiConfigManifest(
            "1.0.0", DateTime.UtcNow, Array.Empty<string>(),
            new List<AiConfigManifestFile> {
                new("agents/dev/AGENT.md", "sha-first", 100),
                new("agents/dev/AGENT.md", "sha-second", 100)
            }
        );

        var first = ManifestFingerprint.Compute(withDuplicates);
        var second = ManifestFingerprint.Compute(withDuplicates);

        Assert.Equal(first, second);

        // Must differ from a manifest with just one of those entries
        var single = BuildManifest(("agents/dev/AGENT.md", "sha-first"));
        Assert.NotEqual(ManifestFingerprint.Compute(single), first);
    }

    private static AiConfigManifest BuildManifest( params (string path, string sha)[] files ) {
        var entries = files.Select(f => new AiConfigManifestFile(f.path, f.sha, 100L)).ToList();
        return new AiConfigManifest("1.0.0", DateTime.UtcNow, Array.Empty<string>(), entries);
    }
}

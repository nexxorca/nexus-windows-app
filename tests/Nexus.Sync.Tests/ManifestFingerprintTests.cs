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

    [Fact]
    public void Compute_EmptyFilesListYieldsKnownConstant() {
        var empty = new AiConfigManifest("1.0.0", DateTime.UtcNow, new List<AiConfigManifestFile>());

        // SHA-256 of empty string (UTF-8 bytes) — well-defined cryptographic constant.
        const string sha256OfEmpty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        Assert.Equal(sha256OfEmpty, ManifestFingerprint.Compute(empty));
    }

    private static AiConfigManifest BuildManifest( params (string path, string sha)[] files ) {
        var entries = files.Select(f => new AiConfigManifestFile(f.path, f.sha, 100L)).ToList();
        return new AiConfigManifest("1.0.0", DateTime.UtcNow, entries);
    }
}

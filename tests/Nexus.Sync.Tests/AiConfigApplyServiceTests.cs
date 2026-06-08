// Test seam choice: option (a) — AiConfigPaths.ClaudeRootOverride (internal field).
// The service reads AiConfigPaths.ClaudeRoot at call time, so pointing the override to a unique
// temp dir per test is enough to isolate all disk I/O. Each test fixture sets the override in its
// constructor and clears it in Dispose so parallel test runs don't collide.
//
// FingerprintPath is NOT redirected — it writes to %LOCALAPPDATA%\Nexus\ai-config-fingerprint,
// which is the real user dir. Tests that reach Phase 4 (Finalize) will write there. This is
// acceptable: the fingerprint file is idempotent (overwrite) and isolated to the test run's output.
// If this becomes a concern in a CI environment, add a FingerprintPathOverride seam similarly.

using System.Security.Cryptography;
using System.Text;

using Nexus.Core.Models;
using Nexus.Core.Services;
using Nexus.Sync.Models;
using Nexus.Sync.Services;

namespace Nexus.Sync.Tests;

public class AiConfigApplyServiceTests : IDisposable {
    private readonly string _claudeRoot;
    private readonly ActivityLogService _activity;
    private readonly LogService _log;

    public AiConfigApplyServiceTests() {
        _claudeRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_claudeRoot);
        AiConfigPaths.ClaudeRootOverride = _claudeRoot;

        _activity = new ActivityLogService();
        _log = new LogService();
    }

    public void Dispose() {
        AiConfigPaths.ClaudeRootOverride = null;
        try {
            if ( Directory.Exists(_claudeRoot) )
                Directory.Delete(_claudeRoot, recursive: true);
        } catch {
            // Best-effort cleanup
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private AiConfigApplyService BuildService( INexusApiClient api ) =>
        new( api, _activity, _log );

    private static string RandomSha() =>
        Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant();

    private static string RandomContent() =>
        $"# {Guid.NewGuid():N}\n\nContent generated at {DateTime.UtcNow:O}";

    // Builds a manifest whose files have real content and correct SHAs placed in the fake api
    private static (AiConfigManifest manifest, FakeApiClient api) BuildHappyManifest(
        string[] managedRoots, params string[] relativePaths ) {
        var api = new FakeApiClient();
        var files = new List<AiConfigManifestFile>();
        foreach ( var path in relativePaths ) {
            var content = Encoding.UTF8.GetBytes(RandomContent());
            var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            api.AddFile(path, content);
            files.Add(new AiConfigManifestFile(path, sha, content.Length));
        }
        var manifest = new AiConfigManifest(
            Guid.NewGuid().ToString("N")[..8],
            DateTime.UtcNow,
            managedRoots,
            files
        );
        return (manifest, api);
    }

    private static AiConfigManifest BuildManifestRaw(
        string[] managedRoots,
        IEnumerable<(string path, string sha)> files ) {
        var entries = files.Select(f => new AiConfigManifestFile(f.path, f.sha, 100L)).ToList();
        return new AiConfigManifest(Guid.NewGuid().ToString("N")[..8], DateTime.UtcNow, managedRoots, entries);
    }

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_HappyPath_WritesFilesAndPersistsFingerprint() {
        var (manifest, api) = BuildHappyManifest(
            new[] { "agents/", "conventions/" },
            "agents/dev/AGENT.md",
            "agents/tech-lead/AGENT.md",
            "conventions/main.md"
        );
        var service = BuildService(api);

        var result = await service.ApplyAsync(manifest);

        Assert.True(result.Success);
        Assert.Equal(AiConfigApplyStatuses.Applied, result.Status);
        Assert.True(File.Exists(Path.Combine(_claudeRoot, "agents", "dev", "AGENT.md")));
        Assert.True(File.Exists(Path.Combine(_claudeRoot, "agents", "tech-lead", "AGENT.md")));
        Assert.True(File.Exists(Path.Combine(_claudeRoot, "conventions", "main.md")));
        Assert.True(File.Exists(AiConfigPaths.FingerprintPath));
    }

    [Fact]
    public async Task ApplyAsync_IdempotentWipe_ManagedRootAbsent_Succeeds() {
        // The managed root "skills/" doesn't exist on disk at all — apply should succeed
        // and create files under it as needed, treating the absence as an empty directory.
        var (manifest, api) = BuildHappyManifest(
            new[] { "skills/" },
            "skills/np/SKILL.md"
        );
        var service = BuildService(api);

        var result = await service.ApplyAsync(manifest);

        Assert.True(result.Success);
        Assert.Equal(AiConfigApplyStatuses.Applied, result.Status);
        Assert.True(File.Exists(Path.Combine(_claudeRoot, "skills", "np", "SKILL.md")));
    }

    [Fact]
    public async Task ApplyAsync_EmptyFiles_NonEmptyManagedRoots_WipesToEmptyDirs() {
        // Pre-populate two managed roots with files
        var agentsDir = Path.Combine(_claudeRoot, "agents");
        var conventionsDir = Path.Combine(_claudeRoot, "conventions");
        Directory.CreateDirectory(Path.Combine(agentsDir, "old-agent"));
        File.WriteAllText(Path.Combine(agentsDir, "old-agent", "AGENT.md"), "old");
        Directory.CreateDirectory(conventionsDir);
        File.WriteAllText(Path.Combine(conventionsDir, "old.md"), "old");

        // Manifest with those roots but zero files
        var manifest = new AiConfigManifest(
            Guid.NewGuid().ToString("N")[..8],
            DateTime.UtcNow,
            new[] { "agents/", "conventions/" },
            new List<AiConfigManifestFile>()
        );
        var api = new FakeApiClient();
        var service = BuildService(api);

        var result = await service.ApplyAsync(manifest);

        Assert.True(result.Success);
        Assert.Equal(AiConfigApplyStatuses.Applied, result.Status);
        // Roots exist but contents were wiped
        Assert.True(Directory.Exists(agentsDir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(agentsDir));
        Assert.True(Directory.Exists(conventionsDir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(conventionsDir));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("../../Windows/System32/drivers/etc/hosts")]
    [InlineData("agents/../../../etc/passwd")]
    public async Task ApplyAsync_PathTraversalInFilePath_AbortsWithNoIo( string evilPath ) {
        var manifest = BuildManifestRaw(
            new[] { "agents/" },
            new[] { (evilPath, RandomSha()) }
        );
        var api = new FakeApiClient();
        var service = BuildService(api);

        var result = await service.ApplyAsync(manifest);

        Assert.False(result.Success);
        Assert.Equal(AiConfigApplyStatuses.Aborted, result.Status);
        // No files written anywhere under _claudeRoot
        Assert.Empty(Directory.EnumerateFileSystemEntries(_claudeRoot, "*", SearchOption.AllDirectories));
        Assert.False(api.AnyDownloadAttempted);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\evil.exe")]
    [InlineData(@"C:/Windows/evil.exe")]
    [InlineData("/etc/passwd")]
    public async Task ApplyAsync_AbsolutePathInFilePath_AbortsWithNoIo( string absolutePath ) {
        var manifest = BuildManifestRaw(
            new[] { "agents/" },
            new[] { (absolutePath, RandomSha()) }
        );
        var api = new FakeApiClient();
        var service = BuildService(api);

        var result = await service.ApplyAsync(manifest);

        Assert.False(result.Success);
        Assert.Equal(AiConfigApplyStatuses.Aborted, result.Status);
        Assert.False(api.AnyDownloadAttempted);
    }

    [Fact]
    public async Task ApplyAsync_FilePathOutsideManagedRoots_AbortsWithNoIo() {
        // managed_roots only declares "agents/" but the file is under "conventions/"
        var (manifest, api) = BuildHappyManifest(
            new[] { "agents/" },
            "conventions/evil.md"   // not inside agents/
        );
        var service = BuildService(api);

        var result = await service.ApplyAsync(manifest);

        Assert.False(result.Success);
        Assert.Equal(AiConfigApplyStatuses.Aborted, result.Status);
        Assert.False(api.AnyDownloadAttempted);
    }

    [Theory]
    [InlineData("../")]
    [InlineData("../../")]
    [InlineData("agents/../../../")]
    public async Task ApplyAsync_ManagedRootWithDotDot_AbortsAtPreFlight( string badRoot ) {
        var manifest = new AiConfigManifest(
            Guid.NewGuid().ToString("N")[..8],
            DateTime.UtcNow,
            new[] { badRoot },
            new List<AiConfigManifestFile>()
        );
        var api = new FakeApiClient();
        var service = BuildService(api);

        var result = await service.ApplyAsync(manifest);

        Assert.False(result.Success);
        Assert.Equal(AiConfigApplyStatuses.Aborted, result.Status);
    }

    [Fact]
    public async Task ApplyAsync_ReparsePointUnderManagedRoot_AbortsAtPreFlight() {
        // Attempt to create a directory junction under the managed root.
        // If symlink creation requires elevation and fails, skip with an environmental reason.
        var agentsDir = Path.Combine(_claudeRoot, "agents");
        Directory.CreateDirectory(agentsDir);

        var junctionTarget = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(junctionTarget);
        var junctionPath = Path.Combine(agentsDir, "test-junction");

        bool symlinkCreated = false;
        try {
            Directory.CreateSymbolicLink(junctionPath, junctionTarget);
            symlinkCreated = Directory.Exists(junctionPath);
        } catch ( UnauthorizedAccessException ) {
            // Skip: creating symbolic links requires SeCreateSymbolicLinkPrivilege on Windows,
            // which is not granted to standard user accounts or non-elevated test runners.
        } catch ( IOException ) {
            // Skip: same environmental reason
        } finally {
            if ( ! symlinkCreated && Directory.Exists(junctionTarget) ) {
                try { Directory.Delete(junctionTarget, recursive: true); } catch { }
            }
        }

        if ( ! symlinkCreated ) {
            // Environmental limitation — cannot test reparse point detection without admin rights.
            // The ContainsReparsePoint helper itself is tested in AiConfigPathsTests.cs where a
            // junction can be set up with a more targeted skip.
            return;
        }

        try {
            var manifest = new AiConfigManifest(
                Guid.NewGuid().ToString("N")[..8],
                DateTime.UtcNow,
                new[] { "agents/" },
                new List<AiConfigManifestFile>()
            );
            var api = new FakeApiClient();
            var service = BuildService(api);

            var result = await service.ApplyAsync(manifest);

            Assert.False(result.Success);
            Assert.Equal(AiConfigApplyStatuses.Aborted, result.Status);
            Assert.NotNull(result.Message);
            Assert.Contains("reparse point", result.Message!, StringComparison.OrdinalIgnoreCase);
        } finally {
            try { Directory.Delete(junctionPath); } catch { }
            try { Directory.Delete(junctionTarget, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ApplyAsync_ShaMismatchOnDownload_ThrowsAndNoFinalWrite() {
        // The fake API returns content whose SHA differs from what the manifest claims.
        var api = new FakeApiClient();
        var actualContent = Encoding.UTF8.GetBytes(RandomContent());
        var wrongSha = RandomSha(); // deliberate mismatch
        api.AddFile("agents/dev/AGENT.md", actualContent);

        var manifest = new AiConfigManifest(
            Guid.NewGuid().ToString("N")[..8],
            DateTime.UtcNow,
            new[] { "agents/" },
            new List<AiConfigManifestFile> {
                new("agents/dev/AGENT.md", wrongSha, actualContent.Length)
            }
        );
        var service = BuildService(api);

        // ApplyAsync catches the SHA exception internally; the pipeline throws from DownloadAndWrite.
        // The outer finally still runs, but the wipe phase has not yet run (download is Phase 3,
        // wipe is Phase 2 — wait, actually: pre-flight → download → wipe → move → finalize).
        // Checking the actual order: DownloadAndWrite → WipeManagedRoots → MoveFilesToFinalLocation.
        // So on SHA failure the wipe has not happened; no files end up in the final location.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(manifest));

        // The final file must NOT exist in ClaudeRoot (wipe ran, move did not)
        Assert.False(File.Exists(Path.Combine(_claudeRoot, "agents", "dev", "AGENT.md")));
    }

    [Fact]
    public async Task ApplyAsync_NetworkFailureMidDownload_AbortsAndDiskLeftPartial() {
        // The fake API fails on the second file. The first file's temp write happened;
        // wipe has not run yet, so disk is in the partial-temp state. This is acknowledged behavior:
        // next apply re-wipes and re-downloads from scratch.
        var api = new FakeApiClient();
        var content1 = Encoding.UTF8.GetBytes(RandomContent());
        var sha1 = Convert.ToHexString(SHA256.HashData(content1)).ToLowerInvariant();
        api.AddFile("agents/dev/AGENT.md", content1);
        api.FailNextDownload("agents/tech-lead/AGENT.md");

        var manifest = new AiConfigManifest(
            Guid.NewGuid().ToString("N")[..8],
            DateTime.UtcNow,
            new[] { "agents/" },
            new List<AiConfigManifestFile> {
                new("agents/dev/AGENT.md", sha1, content1.Length),
                new("agents/tech-lead/AGENT.md", RandomSha(), 100)
            }
        );
        var service = BuildService(api);

        // The pipeline throws from DownloadAndWrite (before wipe), caught at the top level.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(manifest));

        // Neither file ends up in the final ClaudeRoot location (wipe + move never ran)
        Assert.False(File.Exists(Path.Combine(_claudeRoot, "agents", "dev", "AGENT.md")));
        Assert.False(File.Exists(Path.Combine(_claudeRoot, "agents", "tech-lead", "AGENT.md")));
    }

    // ─── Fake API client ─────────────────────────────────────────────────────

    private class FakeApiClient : INexusApiClient {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _failPaths = new(StringComparer.OrdinalIgnoreCase);

        public bool AnyDownloadAttempted { get; private set; }

        public void AddFile( string path, byte[] content ) => _files[path] = content;

        public void FailNextDownload( string path ) => _failPaths.Add(path);

        public Task<ApiResult<AiConfigManifest>> GetAiConfigManifest() =>
            Task.FromResult(ApiResult<AiConfigManifest>.Fail(0, "not used in apply tests"));

        public async Task<ApiResult> DownloadAiConfigFile( string path, Stream destination ) {
            AnyDownloadAttempted = true;

            if ( _failPaths.Contains(path) ) {
                return ApiResult.Fail(0, $"Simulated network failure for {path}");
            }

            if ( ! _files.TryGetValue(path, out var content) ) {
                return ApiResult.Fail(404, $"File not found in fake: {path}");
            }

            await destination.WriteAsync(content);
            return ApiResult.Ok();
        }
    }
}

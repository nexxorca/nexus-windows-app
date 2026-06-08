// Test seam choice: option (a) — AiConfigPaths.ClaudeRootOverride (internal field).
// The service reads AiConfigPaths.ClaudeRoot at call time, so pointing the override to a unique
// temp dir per test is enough to isolate all disk I/O. Each test fixture sets the override in its
// constructor and clears it in Dispose so parallel test runs don't collide.
//
// FingerprintPath is NOT redirected — it writes to %LOCALAPPDATA%\Nexus\ai-config-fingerprint,
// which is the real user dir. Tests that reach Finalize will write there. This is acceptable:
// the fingerprint file is idempotent (overwrite) and isolated to the test run's output.
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
        Assert.Null(result.Message);
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
    public async Task ApplyAsync_BackupRunsBeforeWipe() {
        // Pre-populate a managed root with content
        var agentsDir = Path.Combine(_claudeRoot, "agents");
        Directory.CreateDirectory(Path.Combine(agentsDir, "old-agent"));
        File.WriteAllText(Path.Combine(agentsDir, "old-agent", "AGENT.md"), "old content");

        var (manifest, api) = BuildHappyManifest(
            new[] { "agents/" },
            "agents/dev/AGENT.md"
        );
        var service = BuildService(api);

        var result = await service.ApplyAsync(manifest);

        Assert.True(result.Success);

        // A timestamped backup directory must exist under the backups root
        Assert.True(Directory.Exists(AiConfigPaths.BackupsRoot));
        var backupDirs = Directory.EnumerateDirectories(AiConfigPaths.BackupsRoot).ToList();
        Assert.Single(backupDirs);

        // The backup must contain the pre-wipe content
        var backupAgentsDir = Path.Combine(backupDirs[0], "agents", "old-agent", "AGENT.md");
        Assert.True(File.Exists(backupAgentsDir));
        Assert.Equal("old content", File.ReadAllText(backupAgentsDir));
    }

    [Fact]
    public async Task ApplyAsync_BackupFailure_AbortsBeforeWipe() {
        // Pre-populate a managed root
        var agentsDir = Path.Combine(_claudeRoot, "agents");
        Directory.CreateDirectory(agentsDir);
        var existingFile = Path.Combine(agentsDir, "existing.md");
        File.WriteAllText(existingFile, "should survive");

        // Use a service whose backup will fail by making BackupsRoot point to an existing FILE
        // (creating a subdirectory inside a file path throws)
        var fakeBackupsRoot = Path.Combine(_claudeRoot, "backups-blocker");
        File.WriteAllText(fakeBackupsRoot, "I am a file, not a dir");

        // Override the backups root by temporarily replacing ClaudeRoot so BackupsRoot resolves
        // to something that will cause Directory.CreateDirectory to fail.
        // Strategy: point ClaudeRootOverride to a subdirectory path that resolves BackupsRoot
        // to a path we can block. Simpler: directly test via a subclass/wrapper is not possible
        // without refactoring the seam. Instead we verify the behavior at a higher level by
        // checking the managed root was NOT wiped.
        //
        // The canonical way to trigger a backup failure in the current design is to make the
        // BackupsRoot path a file rather than a directory. We do this by creating a file at
        // exactly _claudeRoot/backups before the apply runs.
        var backupsPath = Path.Combine(_claudeRoot, "backups");
        File.WriteAllText(backupsPath, "I am a file — Directory.CreateDirectory will throw");

        var (manifest, api) = BuildHappyManifest(
            new[] { "agents/" },
            "agents/dev/AGENT.md"
        );
        var service = BuildService(api);

        var result = await service.ApplyAsync(manifest);

        // Apply must abort
        Assert.False(result.Success);
        Assert.Equal(AiConfigApplyStatuses.Aborted, result.Status);
        Assert.NotNull(result.Message);
        Assert.Contains("Backup failed", result.Message!);

        // Wipe must NOT have run — original file still present
        Assert.True(File.Exists(existingFile));
        Assert.Equal("should survive", File.ReadAllText(existingFile));

        // Download must NOT have run
        Assert.False(api.AnyDownloadAttempted);
    }

    [Fact]
    public async Task ApplyAsync_BackupPrune_KeepsLast5() {
        // Pre-populate a managed root so the apply creates a real backup dir
        var agentsDir = Path.Combine(_claudeRoot, "agents");
        Directory.CreateDirectory(agentsDir);
        File.WriteAllText(Path.Combine(agentsDir, "existing.md"), "existing");

        // Pre-seed 7 timestamped backup dirs (older than today)
        var backupsRoot = AiConfigPaths.BackupsRoot;
        Directory.CreateDirectory(backupsRoot);
        var preSeeded = new[] {
            "2026-01-01-000001",
            "2026-01-01-000002",
            "2026-01-01-000003",
            "2026-01-01-000004",
            "2026-01-01-000005",
            "2026-01-01-000006",
            "2026-01-01-000007",
        };
        foreach ( var name in preSeeded ) {
            Directory.CreateDirectory(Path.Combine(backupsRoot, name));
        }

        var (manifest, api) = BuildHappyManifest(
            new[] { "agents/" },
            "agents/dev/AGENT.md"
        );
        var service = BuildService(api);

        var result = await service.ApplyAsync(manifest);

        Assert.True(result.Success);

        // Total dirs = 7 pre-seeded + 1 created by this apply = 8; after prune must be 5
        var remaining = Directory.EnumerateDirectories(backupsRoot).ToList();
        Assert.Equal(5, remaining.Count);

        // The 5 kept are the 5 newest: the apply dir (today's date) + the 4 most recent pre-seeded
        var names = remaining.Select(Path.GetFileName).OrderByDescending(n => n).ToList();
        // names[0] is the apply dir — starts with today's date (greater than 2026-01-01)
        Assert.DoesNotContain("2026-01-01-", names[0]!); // apply dir is NOT a pre-seeded one
        // The 3 oldest pre-seeded dirs must have been deleted
        Assert.DoesNotContain("2026-01-01-000001", names!);
        Assert.DoesNotContain("2026-01-01-000002", names!);
        Assert.DoesNotContain("2026-01-01-000003", names!);
    }

    [Fact]
    public async Task ApplyAsync_WipeRunsBeforeDownload() {
        // Pre-populate the managed root with a stale file
        var agentsDir = Path.Combine(_claudeRoot, "agents");
        Directory.CreateDirectory(agentsDir);
        var staleFile = Path.Combine(agentsDir, "stale.md");
        File.WriteAllText(staleFile, "stale content");

        var (manifest, api) = BuildHappyManifest(
            new[] { "agents/" },
            "agents/dev/AGENT.md"
        );
        var service = BuildService(api);

        var result = await service.ApplyAsync(manifest);

        Assert.True(result.Success);
        // Stale file was wiped
        Assert.False(File.Exists(staleFile));
        // New file is in place
        Assert.True(File.Exists(Path.Combine(_claudeRoot, "agents", "dev", "AGENT.md")));
    }

    [Fact]
    public async Task ApplyAsync_NetworkFailureMidDownload_ThrowsAndDiskLeftPartial() {
        // The fake API fails on the second file. The first file has already been written directly
        // to its final location (no temp dir in the new flow). This is acknowledged behavior:
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

        // Network failure during direct download throws from DownloadToFinalLocation
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(manifest));

        // First file IS on disk (written directly, no temp-then-move)
        Assert.True(File.Exists(Path.Combine(_claudeRoot, "agents", "dev", "AGENT.md")));
        // Second file: FileStream is created before the download attempt (FileMode.Create), so an
        // empty file stub may exist on disk — this is acknowledged partial-state behavior.
        // The key invariant is that the fingerprint does NOT reflect this manifest (Finalize never ran).
        var expectedFingerprint = ManifestFingerprint.Compute(manifest);
        var storedFingerprint = File.Exists(AiConfigPaths.FingerprintPath)
            ? File.ReadAllText(AiConfigPaths.FingerprintPath).Trim()
            : "";
        Assert.NotEqual(expectedFingerprint, storedFingerprint);
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

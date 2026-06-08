using Nexus.Sync.Models;

namespace Nexus.Sync.Tests;

public class AiConfigPathsTests : IDisposable {
    private readonly string _tempRoot;

    public AiConfigPathsTests() {
        _tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose() {
        try {
            if ( Directory.Exists(_tempRoot) )
                Directory.Delete(_tempRoot, recursive: true);
        } catch {
            // Best-effort cleanup
        }
    }

    // ─── IsContainedIn — positive cases ─────────────────────────────────────

    [Fact]
    public void IsContainedIn_TargetDirectlyInsideRoot_ReturnsTrue() {
        var root = Path.GetFullPath(Path.Combine(_tempRoot, "root"));
        var target = Path.GetFullPath(Path.Combine(root, "child.txt"));

        Assert.True(AiConfigPaths.IsContainedIn(root, target));
    }

    [Fact]
    public void IsContainedIn_TargetDeeplyNestedInsideRoot_ReturnsTrue() {
        var root = Path.GetFullPath(Path.Combine(_tempRoot, "root"));
        var target = Path.GetFullPath(Path.Combine(root, "a", "b", "c", "deep.md"));

        Assert.True(AiConfigPaths.IsContainedIn(root, target));
    }

    // ─── IsContainedIn — negative cases ─────────────────────────────────────

    [Fact]
    public void IsContainedIn_TargetOutsideRoot_ReturnsFalse() {
        var root = Path.GetFullPath(Path.Combine(_tempRoot, "root"));
        var target = Path.GetFullPath(Path.Combine(_tempRoot, "sibling", "file.txt"));

        Assert.False(AiConfigPaths.IsContainedIn(root, target));
    }

    [Fact]
    public void IsContainedIn_TargetIsRoot_ReturnsFalse() {
        // IsContainedIn is strict: root itself is NOT considered contained in itself.
        // Equal paths are handled by the caller (IsContainedInClaudeRoot) which adds an equality check.
        var root = Path.GetFullPath(Path.Combine(_tempRoot, "root"));

        Assert.False(AiConfigPaths.IsContainedIn(root, root));
    }

    [Fact]
    public void IsContainedIn_TargetWithDotDotResolvesOutsideRoot_ReturnsFalse() {
        // Both arguments must be pre-canonicalized. If the caller resolves "../" first,
        // the result is a path outside the root and IsContainedIn correctly returns false.
        var root = Path.GetFullPath(Path.Combine(_tempRoot, "root"));
        var target = Path.GetFullPath(Path.Combine(_tempRoot, "root", "..", "outside.txt"));

        Assert.False(AiConfigPaths.IsContainedIn(root, target));
    }

    [Fact]
    public void IsContainedIn_PathSharingPrefixButNotChild_ReturnsFalse() {
        // Regression guard: "rootext" must not be treated as inside "root".
        var root = Path.GetFullPath(Path.Combine(_tempRoot, "root"));
        var target = Path.GetFullPath(Path.Combine(_tempRoot, "rootext", "file.txt"));

        Assert.False(AiConfigPaths.IsContainedIn(root, target));
    }

    // ─── IsContainedIn — Windows case insensitivity ──────────────────────────

    [Fact]
    public void IsContainedIn_DifferentCasing_ReturnsTrue() {
        // Windows paths are case-insensitive. IsContainedIn uses OrdinalIgnoreCase.
        var root = Path.GetFullPath(Path.Combine(_tempRoot, "Agents"));
        var target = Path.GetFullPath(Path.Combine(_tempRoot, "AGENTS", "dev", "AGENT.MD"));

        Assert.True(AiConfigPaths.IsContainedIn(root, target));
    }

    // ─── ContainsReparsePoint ─────────────────────────────────────────────────

    [Fact]
    public void ContainsReparsePoint_RootDoesNotExist_ReturnsFalse() {
        var nonExistentPath = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));

        Assert.False(AiConfigPaths.ContainsReparsePoint(nonExistentPath));
    }

    [Fact]
    public void ContainsReparsePoint_EmptyDirectory_ReturnsFalse() {
        var emptyDir = Path.Combine(_tempRoot, "empty");
        Directory.CreateDirectory(emptyDir);

        Assert.False(AiConfigPaths.ContainsReparsePoint(emptyDir));
    }

    [Fact]
    public void ContainsReparsePoint_TreeWithNoReparsePoints_ReturnsFalse() {
        var dir = Path.Combine(_tempRoot, "clean");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "file.md"), "content");
        File.WriteAllText(Path.Combine(dir, "sub", "nested.md"), "content");

        Assert.False(AiConfigPaths.ContainsReparsePoint(dir));
    }

    [Fact]
    public void ContainsReparsePoint_WithSymlink_ReturnsTrue() {
        // Creating a symbolic link requires SeCreateSymbolicLinkPrivilege on Windows.
        // If the test runner is not elevated, Directory.CreateSymbolicLink will throw
        // UnauthorizedAccessException. In that case we skip with a clear reason.
        var dir = Path.Combine(_tempRoot, "with-symlink");
        Directory.CreateDirectory(dir);

        var symlinkTarget = Path.Combine(_tempRoot, "symlink-target");
        Directory.CreateDirectory(symlinkTarget);
        var symlinkPath = Path.Combine(dir, "junction");

        bool created = false;
        try {
            Directory.CreateSymbolicLink(symlinkPath, symlinkTarget);
            created = Directory.Exists(symlinkPath);
        } catch ( UnauthorizedAccessException ) {
            // Skip: symbolic link creation requires SeCreateSymbolicLinkPrivilege.
            // Run tests as administrator or enable Developer Mode to exercise this path.
            return;
        } catch ( IOException ) {
            // Skip: same environmental reason (e.g., filesystem does not support symlinks).
            return;
        }

        if ( ! created ) {
            // Symlink creation silently failed — treat as environmental limitation.
            return;
        }

        try {
            Assert.True(AiConfigPaths.ContainsReparsePoint(dir));
        } finally {
            try { Directory.Delete(symlinkPath); } catch { }
        }
    }
}

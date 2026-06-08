// All paths use forward slashes; convert at filesystem boundary.
namespace Nexus.Sync.Models;

public static class AiConfigPaths {
    // Test seam: tests/Nexus.Sync.Tests sets this to a temp dir so tests never touch the real ~/.claude
    internal static string? ClaudeRootOverride;

    public static string ClaudeRoot =>
        ClaudeRootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>Timestamped backup directory root. Each apply writes a subdirectory
    /// named "yyyy-MM-dd-HHmmss"; the 5 most recent are kept and older ones are pruned.</summary>
    public static string BackupsRoot => Path.Combine(ClaudeRoot, "backups");

    public static readonly string FingerprintPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nexus",
            "ai-config-fingerprint"
        );

    /// <summary>Returns true when <paramref name="targetFull"/> is located inside
    /// <paramref name="rootFull"/>. Both parameters MUST already be canonicalized via
    /// <see cref="Path.GetFullPath"/> before calling — this method does NOT canonicalize them.</summary>
    public static bool IsContainedIn( string rootFull, string targetFull ) {
        return targetFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns true if any file-system entry under <paramref name="root"/> is a reparse
    /// point (symlink or junction). Returns false when the root does not exist.</summary>
    public static bool ContainsReparsePoint( string root ) {
        if ( ! Directory.Exists(root) ) return false;

        foreach ( var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories) ) {
            var attrs = File.GetAttributes(entry);
            if ( (attrs & FileAttributes.ReparsePoint) != 0 ) return true;
        }

        return false;
    }
}

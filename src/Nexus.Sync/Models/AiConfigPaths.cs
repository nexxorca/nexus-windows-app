// All paths use forward slashes; convert at filesystem boundary.
namespace Nexus.Sync.Models;

using System.IO;

public static class AiConfigPaths
{
    public static readonly string ClaudeRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public static readonly string BackupsRoot =
        Path.Combine(ClaudeRoot, "backups");

    public static readonly string VersionMarkerPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nexus",
            "ai-config-version"
        );

    public static readonly IReadOnlyList<string> ExclusionList = new[]
    {
        "projects/",
        "settings.local.json",
        ".credentials*",
        "mcp-needs-auth-cache.json",
        "backups/",
        "cache/",
        "sessions/",
        "plans/",
        "plugins/",
        "telemetry/",
        "todos/",
        "*.log",
        "shell-snapshots/",
        "statsig/",
        "ide/",
        "paste-cache/",
        "debug/",
        "file-history/",
        "session-env/",
        "*.tmp",
        "*.rollback.tmp",
    };

    public static bool IsExcluded(string relativePath)
    {
        relativePath = relativePath.Replace('\\', '/');

        foreach ( var entry in ExclusionList )
        {
            if ( entry.EndsWith('/') )
            {
                // Prefix match: "projects/" matches "projects/" or "projects/foo/bar.md"
                if ( relativePath == entry || relativePath.StartsWith(entry) )
                    return true;
            }
            else if ( entry.Contains('*') )
            {
                // Glob match: split on '*' and check StartsWith / EndsWith / both
                var parts = entry.Split('*');
                if ( parts.Length == 2 )
                {
                    var prefix = parts[0];
                    var suffix = parts[1];

                    var fileName = relativePath.Contains('/')
                        ? relativePath[(relativePath.LastIndexOf('/') + 1)..]
                        : relativePath;

                    if ( prefix.Length > 0 && suffix.Length > 0 )
                    {
                        if ( fileName.StartsWith(prefix) && fileName.EndsWith(suffix) )
                            return true;
                    }
                    else if ( prefix.Length > 0 )
                    {
                        if ( fileName.StartsWith(prefix) )
                            return true;
                    }
                    else if ( suffix.Length > 0 )
                    {
                        if ( fileName.EndsWith(suffix) )
                            return true;
                    }
                }
            }
            else
            {
                // Exact match against file name only (no directory component in entry)
                var fileName = relativePath.Contains('/')
                    ? relativePath[(relativePath.LastIndexOf('/') + 1)..]
                    : relativePath;

                if ( fileName == entry )
                    return true;
            }
        }

        return false;
    }
}

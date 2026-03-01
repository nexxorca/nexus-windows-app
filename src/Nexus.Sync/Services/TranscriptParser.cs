using System.Text.Json;
using System.Text.RegularExpressions;
using Nexus.Core.Services;

namespace Nexus.Sync.Services;

public class TranscriptMetadata {
    public string SessionId { get; set; } = "";
    public string? Cwd { get; set; }
    public string? ProjectHashId { get; set; }
    public string? SkipReason { get; set; }
}

public partial class TranscriptParser {
    private readonly LogService _log;

    public TranscriptParser( LogService log ) {
        _log = log;
    }

    public TranscriptMetadata? Parse(string filePath) {
        try {
            var lines = ReadLines(filePath, 30);
            if ( lines.Count == 0 ) return null;

            string? sessionId = null;
            string? cwd = null;
            var hasRealEntries = false;

            foreach ( var line in lines ) {
                if ( string.IsNullOrWhiteSpace(line) ) continue;

                try {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    string? entryType = null;
                    if ( root.TryGetProperty("type", out var typeProp) ) {
                        entryType = typeProp.GetString();
                    }

                    if ( entryType == "file-history-snapshot" || entryType == "queue-operation" ) continue;

                    if ( entryType == "user" || entryType == "assistant" ) {
                        hasRealEntries = true;
                    }

                    if ( sessionId == null && root.TryGetProperty("sessionId", out var sidProp) ) {
                        sessionId = sidProp.GetString();
                    }

                    if ( cwd == null && root.TryGetProperty("cwd", out var cwdProp) ) {
                        cwd = cwdProp.GetString();
                    }

                    if ( hasRealEntries && sessionId != null && cwd != null ) break;
                } catch {
                    // Skip malformed lines
                }
            }

            if ( ! hasRealEntries ) {
                return new TranscriptMetadata {
                    SkipReason = "Non-transcript file (file-history/queue only)"
                };
            }

            if ( string.IsNullOrEmpty(sessionId) ) {
                _log.Write($"No sessionId found in: {filePath}");
                return null;
            }

            string? projectHashId = null;
            string? skipReason = null;

            if ( string.IsNullOrEmpty(cwd) ) {
                skipReason = "No working directory in transcript";
            } else {
                (projectHashId, skipReason) = ExtractProjectHash(cwd);
            }

            return new TranscriptMetadata {
                SessionId = sessionId,
                Cwd = cwd,
                ProjectHashId = projectHashId,
                SkipReason = skipReason
            };
        } catch ( Exception ex ) {
            _log.Error($"Failed to parse transcript: {filePath}", ex);
            return null;
        }
    }

    private (string? hash, string? reason) ExtractProjectHash( string cwd ) {
        var effectiveCwd = cwd;

        if ( ! Directory.Exists(effectiveCwd) ) {
            var fallbackCwd = GetXamppFallbackPath(effectiveCwd);
            if ( fallbackCwd != null && Directory.Exists(fallbackCwd) ) {
                effectiveCwd = fallbackCwd;
            } else {
                var fallbackDesc = fallbackCwd != null ? $" (also checked {fallbackCwd})" : "";
                return (null, $"Project directory not found: {cwd}{fallbackDesc}");
            }
        }

        var claudeMdPath = Path.Combine(effectiveCwd, "CLAUDE.md");
        if ( ! File.Exists(claudeMdPath) ) {
            return (null, $"No CLAUDE.md found at: {effectiveCwd}");
        }

        try {
            var content = File.ReadAllText(claudeMdPath);
            var match = ProjectHashRegex().Match(content);
            if ( match.Success ) return (match.Groups[1].Value, null);
            return (null, $"No NEXUS_PROJECT_HASH_ID in CLAUDE.md at: {effectiveCwd}");
        } catch {
            return (null, $"Failed to read CLAUDE.md at: {effectiveCwd}");
        }
    }

    private static string? GetXamppFallbackPath( string path ) {
        const string xampp = @"c:\xampp\htdocs\";
        const string xampp7 = @"c:\xampp7\htdocs\";

        if ( path.StartsWith(xampp, StringComparison.OrdinalIgnoreCase) ) {
            return xampp7 + path[xampp.Length..];
        }

        if ( path.StartsWith(xampp7, StringComparison.OrdinalIgnoreCase) ) {
            return xampp + path[xampp7.Length..];
        }

        return null;
    }

    private static List<string> ReadLines( string filePath, int maxLines ) {
        var lines = new List<string>();
        try {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            while ( lines.Count < maxLines && ! reader.EndOfStream ) {
                var line = reader.ReadLine();
                if ( line != null ) lines.Add(line);
            }
        } catch {
            // Return whatever we got
        }
        return lines;
    }

    [GeneratedRegex(@"NEXUS_PROJECT_HASH_ID\s*=\s*([a-f0-9]+)")]
    private static partial Regex ProjectHashRegex();
}

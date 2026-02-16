using System.Text.Json;
using System.Text.RegularExpressions;
using Nexus.Core.Services;

namespace Nexus.Sync.Services;

public class TranscriptMetadata {
    public string SessionId { get; set; } = "";
    public string? Cwd { get; set; }
    public string? ProjectHashId { get; set; }
}

public partial class TranscriptParser {
    private readonly LogService _log;

    public TranscriptParser(LogService log) {
        _log = log;
    }

    public TranscriptMetadata? Parse(string filePath) {
        try {
            var lines = ReadLines(filePath, 30);
            if ( lines.Count == 0 ) return null;

            string? sessionId = null;
            string? cwd = null;

            foreach ( var line in lines ) {
                if ( string.IsNullOrWhiteSpace(line) ) continue;

                try {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if ( sessionId == null && root.TryGetProperty("sessionId", out var sidProp) ) {
                        sessionId = sidProp.GetString();
                    }

                    if ( cwd == null && root.TryGetProperty("message", out var msgProp) ) {
                        if ( msgProp.TryGetProperty("content", out var contentProp) ) {
                            var contentStr = contentProp.ValueKind == JsonValueKind.String
                                ? contentProp.GetString()
                                : contentProp.GetRawText();

                            if ( contentStr != null ) {
                                var cwdMatch = CwdRegex().Match(contentStr);
                                if ( cwdMatch.Success ) {
                                    cwd = cwdMatch.Groups[1].Value;
                                }
                            }
                        }
                    }

                    if ( sessionId != null && cwd != null ) break;
                } catch {
                    // Skip malformed lines
                }
            }

            if ( string.IsNullOrEmpty(sessionId) ) {
                _log.Write($"No sessionId found in: {filePath}");
                return null;
            }

            string? projectHashId = null;
            if ( ! string.IsNullOrEmpty(cwd) ) {
                projectHashId = ExtractProjectHash(cwd);
            }

            return new TranscriptMetadata {
                SessionId = sessionId,
                Cwd = cwd,
                ProjectHashId = projectHashId
            };
        } catch ( Exception ex ) {
            _log.Error($"Failed to parse transcript: {filePath}", ex);
            return null;
        }
    }

    private string? ExtractProjectHash( string cwd ) {
        var claudeMdPath = Path.Combine(cwd, "CLAUDE.md");
        if ( ! File.Exists(claudeMdPath) ) return null;

        try {
            var content = File.ReadAllText(claudeMdPath);
            var match = ProjectHashRegex().Match(content);
            return match.Success ? match.Groups[1].Value : null;
        } catch {
            return null;
        }
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

    [GeneratedRegex(@"Primary working directory:\s*(.+)")]
    private static partial Regex CwdRegex();

    [GeneratedRegex(@"NEXUS_PROJECT_HASH_ID\s*=\s*([a-f0-9]+)")]
    private static partial Regex ProjectHashRegex();
}

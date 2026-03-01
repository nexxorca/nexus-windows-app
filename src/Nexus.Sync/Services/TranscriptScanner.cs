using Nexus.Core.Services;
using Nexus.Sync.Models;

namespace Nexus.Sync.Services;

public class TranscriptScanner {
    private readonly LogService _log;

    public TranscriptScanner( LogService log ) {
        _log = log;
    }

    public List<TranscriptFile> Scan() {
        var results = new List<TranscriptFile>();
        var claudeProjectsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects"
        );

        if ( ! Directory.Exists(claudeProjectsDir) ) {
            _log.Write($"Claude projects directory not found: {claudeProjectsDir}");
            return results;
        }

        foreach ( var projectDir in Directory.GetDirectories(claudeProjectsDir) ) {
            var projectSlug = Path.GetFileName(projectDir);

            try {
                var jsonlFiles = Directory.GetFiles(projectDir, "*.jsonl");
                foreach ( var filePath in jsonlFiles ) {
                    var fileName = Path.GetFileName(filePath);

                    // acompact-* files are context-compaction summaries, not new activity — the original session file captures all data
                    if ( fileName.StartsWith("acompact-", StringComparison.OrdinalIgnoreCase) ) continue;

                    var info = new FileInfo(filePath);
                    results.Add(new TranscriptFile {
                        FilePath = filePath,
                        ProjectSlug = projectSlug,
                        FileSize = info.Length
                    });
                }
            } catch ( Exception ex ) {
                _log.Error($"Error scanning project directory: {projectDir}", ex);
            }
        }

        _log.Write($"Scanned {results.Count} transcript files");
        return results;
    }
}

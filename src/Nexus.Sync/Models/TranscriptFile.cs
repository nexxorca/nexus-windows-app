namespace Nexus.Sync.Models;

public class TranscriptFile {
    public string FilePath { get; set; } = "";
    public string ProjectSlug { get; set; } = "";
    public long FileSize { get; set; }
}

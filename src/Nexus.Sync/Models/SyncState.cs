namespace Nexus.Sync.Models;

public class SyncState {
    public Dictionary<string, FileState> Files { get; set; } = new();
}

public class FileState {
    public long Size { get; set; }
    public string Timestamp { get; set; } = "";
}

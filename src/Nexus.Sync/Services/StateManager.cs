using System.Text.Json;
using Nexus.Core.Models;
using Nexus.Core.Services;
using Nexus.Sync.Models;

namespace Nexus.Sync.Services;

public class StateManager {
    private SyncState _state;
    private readonly string _statePath;
    private readonly LogService _log;
    private static readonly JsonSerializerOptions _jsonOptions = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public StateManager(LogService log) {
        _log = log;
        _statePath = Path.Combine(AppConfig.ConfigDir, "sync-state.json");
        _state = Load();
    }

    public bool HasChanged(string filePath, long currentSize) {
        if ( ! _state.Files.TryGetValue(filePath, out var fileState) ) return true;
        return fileState.Size != currentSize;
    }

    public void MarkUploaded(string filePath, long size) {
        _state.Files[filePath] = new FileState {
            Size = size,
            Timestamp = DateTime.Now.ToString("o")
        };
    }

    public void Save() {
        try {
            var json = JsonSerializer.Serialize(_state, _jsonOptions);
            File.WriteAllText(_statePath, json);
        } catch (Exception ex) {
            _log.Error("Failed to save sync state", ex);
        }
    }

    private SyncState Load() {
        if ( ! File.Exists(_statePath) ) return new SyncState();
        try {
            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize<SyncState>(json, _jsonOptions) ?? new SyncState();
        } catch {
            return new SyncState();
        }
    }
}

using System.Text.Json;

using Nexus.Core.Models;

namespace Nexus.Core.Services;

public class ActivityLogEntry {
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "ok";
}

public class ActivityLogService {
    private readonly List<ActivityLogEntry> _entries = new();
    private readonly string _logPath;
    private readonly object _lock = new();
    private const int MaxEntries = 500;

    private static readonly JsonSerializerOptions _jsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ActivityLogService() {
        _logPath = Path.Combine(AppConfig.ConfigDir, "activity.log");
        Directory.CreateDirectory(AppConfig.ConfigDir);
    }

    public void Log( string type, string description, string status = "ok" ) {
        var entry = new ActivityLogEntry {
            Type = type,
            Description = description,
            Status = status
        };

        lock ( _lock ) {
            _entries.Add(entry);
            if ( _entries.Count > MaxEntries ) {
                _entries.RemoveAt(0);
            }
        }
    }

    public List<ActivityLogEntry> GetRecent( int count = 100, string? typeFilter = null ) {
        lock ( _lock ) {
            var query = _entries.AsEnumerable();
            if ( ! string.IsNullOrEmpty(typeFilter) && typeFilter != "all" ) {
                query = query.Where(e => e.Type == typeFilter);
            }
            return query.OrderByDescending(e => e.Timestamp).Take(count).ToList();
        }
    }

    public void Flush() {
        List<ActivityLogEntry> snapshot;
        lock ( _lock ) {
            snapshot = new List<ActivityLogEntry>(_entries);
        }

        try {
            var lines = snapshot.Select(e => JsonSerializer.Serialize(e, _jsonOptions));
            File.WriteAllLines(_logPath, lines);
        } catch {
            // Silently ignore flush failures
        }
    }
}

using Nexus.Core.Models;

namespace Nexus.Core.Services;

public class LogService {
    private readonly string _logPath;
    private readonly object _lock = new();

    public LogService() {
        _logPath = Path.Combine(AppConfig.ConfigDir, "debug.log");
        Directory.CreateDirectory(AppConfig.ConfigDir);
    }

    public void Write( string message ) {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        lock ( _lock ) {
            try {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            } catch {
                // Silently ignore log write failures
            }
        }
    }

    public void Error( string message, Exception? ex = null ) {
        var detail = ex != null ? $" | {ex.GetType().Name}: {ex.Message}" : "";
        Write($"ERROR: {message}{detail}");
    }
}

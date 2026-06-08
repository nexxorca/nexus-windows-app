using System.Diagnostics;

namespace Nexus.Core.Services;

public static class ClaudeCodeInstallProbe {
    private static readonly string _probePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "claude", "claude.exe"
    );

    public static bool IsInstalled() {
        return File.Exists(_probePath);
    }

    public static bool IsRunning() {
        var processes = Process.GetProcessesByName("claude");
        var running = processes.Length > 0;
        foreach ( var p in processes ) p.Dispose();
        return running;
    }
}

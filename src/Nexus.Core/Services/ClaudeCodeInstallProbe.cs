namespace Nexus.Core.Services;

public static class ClaudeCodeInstallProbe {
    private static readonly string _probePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "claude", "claude.exe"
    );

    public static bool IsInstalled() {
        return File.Exists(_probePath);
    }
}

using Microsoft.Win32;

namespace Nexus.App;

public static class StartupManager {
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NexusDesktop";

    public static void SetLaunchOnStartup( bool enable ) {
        try {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
            if ( key == null ) return;

            if ( enable ) {
                var exePath = Environment.ProcessPath;
                if ( string.IsNullOrEmpty(exePath) ) return;
                key.SetValue(ValueName, exePath);
            } else {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        } catch {
            // fail silently — registry access issues should not crash the app
        }
    }

    public static bool IsRegisteredForStartup() {
        try {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: false);
            return key?.GetValue(ValueName) != null;
        } catch {
            return false;
        }
    }
}

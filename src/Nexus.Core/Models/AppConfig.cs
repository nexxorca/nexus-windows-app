using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nexus.Core.Models;

public class AppConfig {
    public string NexusUrl { get; set; } = "";
    public string? AuthToken { get; set; }
    public string? UserHashId { get; set; }
    public string? UserName { get; set; }
    public int SyncIntervalSeconds { get; set; } = 60;

    public bool IsLoggedIn => ! string.IsNullOrEmpty(AuthToken);
    public bool IsValid => ! string.IsNullOrEmpty(NexusUrl) && IsLoggedIn;

    public string? DecryptedToken => DecryptToken(AuthToken);

    public static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Nexus"
    );

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions _jsonOptions = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static AppConfig Load() {
        if ( ! File.Exists(ConfigPath) ) {
            var legacyConfig = ImportFromLegacyEnv();
            if ( legacyConfig != null ) {
                legacyConfig.Save();
                return legacyConfig;
            }
            return new AppConfig();
        }

        try {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, _jsonOptions) ?? new AppConfig();
            if ( config.AuthToken != null && config.DecryptedToken == null ) {
                var tokenBytes = Encoding.UTF8.GetBytes(config.AuthToken);
                var encrypted = ProtectedData.Protect(tokenBytes, null, DataProtectionScope.CurrentUser);
                config.AuthToken = Convert.ToBase64String(encrypted);
                config.Save();
            }
            return config;
        } catch {
            return new AppConfig();
        }
    }

    public void Save() {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(this, _jsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    public void SetLoginData( string authToken, string userHashId, string userName ) {
        var tokenBytes = Encoding.UTF8.GetBytes(authToken);
        var encrypted = ProtectedData.Protect(tokenBytes, null, DataProtectionScope.CurrentUser);
        AuthToken = Convert.ToBase64String(encrypted);
        UserHashId = userHashId;
        UserName = userName;
        Save();
    }

    public void ClearLoginData() {
        AuthToken = null;
        UserHashId = null;
        UserName = null;
        Save();
    }

    private static string? DecryptToken( string? encryptedBase64 ) {
        if ( string.IsNullOrEmpty(encryptedBase64) ) return null;
        try {
            var encrypted = Convert.FromBase64String(encryptedBase64);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        } catch {
            return null;
        }
    }

    private static AppConfig? ImportFromLegacyEnv() {
        var envPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "nexus.env"
        );

        if ( ! File.Exists(envPath) ) return null;

        try {
            var lines = File.ReadAllLines(envPath);
            var config = new AppConfig();

            foreach ( var line in lines ) {
                var trimmed = line.Trim();
                if ( string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#') ) continue;

                var eqIndex = trimmed.IndexOf('=');
                if ( eqIndex < 0 ) continue;

                var key = trimmed[..eqIndex].Trim();
                var value = trimmed[(eqIndex + 1)..].Trim();

                switch ( key ) {
                    case "NEXUS_URL":
                        config.NexusUrl = value;
                        break;
                }
            }

            return string.IsNullOrEmpty(config.NexusUrl) ? null : config;
        } catch {
            return null;
        }
    }
}

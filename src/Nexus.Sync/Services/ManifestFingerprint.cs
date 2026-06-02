using System.Security.Cryptography;
using System.Text;

using Nexus.Core.Models;

namespace Nexus.Sync.Services;

public static class ManifestFingerprint {
    public static string Compute( AiConfigManifest manifest ) {
        var joined = string.Join("\n",
            manifest.Files
                .OrderBy(f => f.Path, StringComparer.Ordinal)
                .Select(f => $"{f.Path}|{f.Sha}")
        );

        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(joined);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

using System.Security.Cryptography;

using Nexus.Core.Models;
using Nexus.Core.Services;
using Nexus.Sync.Models;

namespace Nexus.Sync.Services;

public class AiConfigApplyService {
    private readonly INexusApiClient _api;
    private readonly ActivityLogService _activity;
    private readonly LogService _log;

    private readonly object _lock = new();
    private bool _running;

    public AiConfigApplyService( INexusApiClient api, ActivityLogService activity, LogService log ) {
        _api = api;
        _activity = activity;
        _log = log;
    }

    public async Task<AiConfigApplyResult> ApplyAsync( AiConfigManifest manifest ) {
        lock ( _lock ) {
            if ( _running ) return new AiConfigApplyResult(false, AiConfigApplyStatuses.Aborted, null, "already running");
            _running = true;
        }

        // Contract guard: NexusApiClient.GetAiConfigManifest substitutes ManagedRoots = [] when the
        // server omits the field. If it is somehow null here, that is a caller bug — fail fast.
        if ( manifest.ManagedRoots is null )
            throw new InvalidOperationException("ManagedRoots must be non-null — NexusApiClient.GetAiConfigManifest guarantees this.");

        string? tempDir = null;

        try {
            var abortResult = PreFlight(manifest);
            if ( abortResult != null ) return abortResult;

            var claudeRootFull = Path.GetFullPath(AiConfigPaths.ClaudeRoot);

            tempDir = Path.Combine(Path.GetTempPath(), $"nexus-ai-config-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            await DownloadAndWrite(manifest, tempDir, claudeRootFull);

            WipeManagedRoots(manifest, claudeRootFull);

            MoveFilesToFinalLocation(manifest, tempDir, claudeRootFull);

            Finalize(manifest);

            return new AiConfigApplyResult(true, AiConfigApplyStatuses.Applied, manifest.Version, null);

        } finally {
            lock ( _lock ) { _running = false; }

            if ( tempDir != null && Directory.Exists(tempDir) ) {
                try {
                    Directory.Delete(tempDir, recursive: true);
                } catch ( Exception ex ) {
                    _activity.Log("ai_config_apply", $"Cleanup: failed to delete tempDir {tempDir}: {ex.Message}", "warning");
                    _log.Error($"AI config: failed to delete tempDir {tempDir}", ex);
                }
            }
        }
    }

    // ─── Phase 1: Pre-flight ─────────────────────────────────────────────────

    private AiConfigApplyResult? PreFlight( AiConfigManifest manifest ) {
        // (1) Manifest path validation — cheap, run before disk walks
        var validationError = ValidateManifestPaths(manifest);
        if ( validationError != null ) {
            _activity.Log("ai_config_apply", $"Pre-flight ABORT — {validationError}", "error");
            _log.Error($"AI config pre-flight failed: {validationError}");
            return new AiConfigApplyResult(false, AiConfigApplyStatuses.Aborted, null, validationError);
        }

        // (2) Symlink / reparse-point check — disk walk, after manifest validation
        var claudeRootFull = Path.GetFullPath(AiConfigPaths.ClaudeRoot);
        foreach ( var root in manifest.ManagedRoots! ) {
            var rootFull = Path.GetFullPath(Path.Combine(AiConfigPaths.ClaudeRoot, root.TrimEnd('/', '\\')));
            if ( ! IsContainedInClaudeRoot(claudeRootFull, rootFull) ) continue; // already caught by validation

            if ( AiConfigPaths.ContainsReparsePoint(rootFull) ) {
                var msg = $"Reparse point (symlink/junction) detected under managed root '{root}' — apply aborted. Remove the symlink and retry.";
                _activity.Log("ai_config_apply", $"Pre-flight ABORT — {msg}", "error");
                _log.Error($"AI config pre-flight: {msg}");
                return new AiConfigApplyResult(false, AiConfigApplyStatuses.Aborted, null, msg);
            }
        }

        return null;
    }

    private string? ValidateManifestPaths( AiConfigManifest manifest ) {
        foreach ( var root in manifest.ManagedRoots! ) {
            var err = ValidateRelativePath(root, "managed_roots");
            if ( err != null ) return err;

            // Ensure the resolved path is contained within ClaudeRoot
            var claudeRootFull = Path.GetFullPath(AiConfigPaths.ClaudeRoot);
            var rootNormalized = root.TrimEnd('/', '\\');
            if ( rootNormalized.Length == 0 ) return "managed_roots entry must not be empty";

            // A single-file entry (no trailing slash) resolves directly; verify containment
            var resolvedFull = Path.GetFullPath(Path.Combine(AiConfigPaths.ClaudeRoot, rootNormalized));
            if ( ! IsContainedInClaudeRoot(claudeRootFull, resolvedFull) ) {
                return $"managed_roots entry '{root}' resolves outside ~/.claude/";
            }
        }

        var managedRootsFull = manifest.ManagedRoots!
            .Select(r => Path.GetFullPath(Path.Combine(AiConfigPaths.ClaudeRoot, r.TrimEnd('/', '\\'))))
            .ToList();

        var claudeRootFullForFiles = Path.GetFullPath(AiConfigPaths.ClaudeRoot);

        foreach ( var file in manifest.Files ) {
            var err = ValidateRelativePath(file.Path, "files[].path");
            if ( err != null ) return err;

            // File must fall within at least one managed root
            var fileFull = Path.GetFullPath(Path.Combine(AiConfigPaths.ClaudeRoot, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if ( ! IsContainedInClaudeRoot(claudeRootFullForFiles, fileFull) ) {
                return $"files[].path '{file.Path}' resolves outside ~/.claude/";
            }

            var inManagedRoot = managedRootsFull.Any(rootFull =>
                fileFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                fileFull.Equals(rootFull, StringComparison.OrdinalIgnoreCase));

            if ( ! inManagedRoot ) {
                return $"files[].path '{file.Path}' does not fall within any managed_roots entry";
            }
        }

        return null;
    }

    private static string? ValidateRelativePath( string path, string fieldName ) {
        if ( string.IsNullOrEmpty(path) ) return $"{fieldName} entry must not be empty";
        if ( path.Length > 260 ) return $"{fieldName} entry exceeds 260 characters: '{path}'";
        if ( Path.IsPathRooted(path) ) return $"{fieldName} entry must not be an absolute path: '{path}'";
        if ( path.Contains(':') ) return $"{fieldName} entry must not contain ':': '{path}'";
        if ( path.Contains('\\') ) return $"{fieldName} entry must not contain embedded backslash: '{path}'";
        if ( path.Contains("..") ) return $"{fieldName} entry must not contain '..': '{path}'";
        if ( path.Any(c => char.IsControl(c) ) ) return $"{fieldName} entry must not contain control characters: '{path}'";
        return null;
    }

    private static bool IsContainedInClaudeRoot( string claudeRootFull, string targetFull ) {
        // Allow the root itself (for single-file managed_roots entries like "CLAUDE.md")
        return targetFull.Equals(claudeRootFull, StringComparison.OrdinalIgnoreCase)
            || AiConfigPaths.IsContainedIn(claudeRootFull, targetFull);
    }

    // ─── Phase 2: Wipe managed roots ─────────────────────────────────────────

    private void WipeManagedRoots( AiConfigManifest manifest, string claudeRootFull ) {
        _activity.Log("ai_config_apply", $"Wipe: clearing {manifest.ManagedRoots!.Length} managed root(s)");

        foreach ( var root in manifest.ManagedRoots ) {
            var rootNormalized = root.TrimEnd('/', '\\');
            var rootFull = Path.GetFullPath(Path.Combine(AiConfigPaths.ClaudeRoot, rootNormalized));

            // Guard: containment already verified in pre-flight, but double-check before I/O
            if ( ! IsContainedInClaudeRoot(claudeRootFull, rootFull) ) {
                throw new InvalidOperationException($"Wipe target '{root}' resolves outside ~/.claude/ — aborting.");
            }

            if ( File.Exists(rootFull) ) {
                // Single-file managed root
                File.Delete(rootFull);
                _log.Write($"AI config wipe: deleted file {rootNormalized}");
            } else if ( Directory.Exists(rootFull) ) {
                // Directory managed root — delete contents recursively, keep the directory itself
                foreach ( var entry in Directory.EnumerateFileSystemEntries(rootFull, "*", SearchOption.TopDirectoryOnly) ) {
                    var entryFull = Path.GetFullPath(entry);
                    if ( ! AiConfigPaths.IsContainedIn(rootFull, entryFull) ) {
                        throw new InvalidOperationException($"Wipe enumeration: entry '{entryFull}' resolved outside managed root — aborting.");
                    }

                    if ( Directory.Exists(entryFull) ) {
                        Directory.Delete(entryFull, recursive: true);
                    } else {
                        File.Delete(entryFull);
                    }
                }
                _log.Write($"AI config wipe: cleared directory {rootNormalized}/");
            }
            // If neither exists, skip — idempotent
        }

        _activity.Log("ai_config_apply", "Wipe: complete");
    }

    // ─── Phase 3: Download and write to temp ─────────────────────────────────

    private async Task DownloadAndWrite( AiConfigManifest manifest, string tempDir, string claudeRootFull ) {
        _activity.Log("ai_config_apply", $"Download: fetching {manifest.Files.Count} file(s)");

        foreach ( var f in manifest.Files ) {
            var osRelPath = f.Path.Replace('/', Path.DirectorySeparatorChar);
            var destPath = Path.GetFullPath(Path.Combine(tempDir, osRelPath));

            // Verify temp write target is under tempDir (path containment)
            var tempDirFull = Path.GetFullPath(tempDir);
            if ( ! AiConfigPaths.IsContainedIn(tempDirFull, destPath) ) {
                throw new InvalidOperationException($"Download: '{f.Path}' resolves outside temp dir — aborting.");
            }

            var parentDir = Path.GetDirectoryName(destPath);
            if ( parentDir != null ) Directory.CreateDirectory(parentDir);

            using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var downloadResult = await _api.DownloadAiConfigFile(f.Path, fileStream);
            if ( ! downloadResult.Success ) {
                _activity.Log("ai_config_apply", $"Download: ABORT — {f.Path}: {downloadResult.Message}", "error");
                _log.Error($"AI config download failed for {f.Path}: {downloadResult.Message}");
                throw new InvalidOperationException($"Download failed for {f.Path}: {downloadResult.Message}");
            }
        }

        _activity.Log("ai_config_apply", "Download: all files fetched");

        // SHA-256 verify — corruption check on the download
        foreach ( var f in manifest.Files ) {
            var osRelPath = f.Path.Replace('/', Path.DirectorySeparatorChar);
            var filePath = Path.Combine(tempDir, osRelPath);
            var actualSha = ComputeSha256(filePath);
            if ( actualSha != f.Sha ) {
                _activity.Log("ai_config_apply", $"Verify: ABORT — SHA mismatch for {f.Path}", "error");
                _log.Error($"AI config SHA mismatch: {f.Path} expected={f.Sha} actual={actualSha}");
                throw new InvalidOperationException($"SHA mismatch for {f.Path} — download may be corrupted");
            }
        }

        _activity.Log("ai_config_apply", "Verify: all SHAs match");
    }

    // ─── Phase 3 (continued): Move files to final location ───────────────────

    private void MoveFilesToFinalLocation( AiConfigManifest manifest, string tempDir, string claudeRootFull ) {
        _activity.Log("ai_config_apply", $"Write: placing {manifest.Files.Count} file(s)");

        foreach ( var f in manifest.Files ) {
            var osRelPath = f.Path.Replace('/', Path.DirectorySeparatorChar);
            var srcPath = Path.Combine(tempDir, osRelPath);
            var targetFull = Path.GetFullPath(Path.Combine(AiConfigPaths.ClaudeRoot, osRelPath));

            // Final containment check before I/O
            if ( ! AiConfigPaths.IsContainedIn(claudeRootFull, targetFull) ) {
                throw new InvalidOperationException($"Write: '{f.Path}' resolves outside ~/.claude/ — aborting.");
            }

            var parentDir = Path.GetDirectoryName(targetFull);
            if ( parentDir != null ) Directory.CreateDirectory(parentDir);

            File.Move(srcPath, targetFull, overwrite: true);
        }

        _activity.Log("ai_config_apply", "Write: complete");
    }

    // ─── Phase 4: Finalize ────────────────────────────────────────────────────

    private void Finalize( AiConfigManifest manifest ) {
        var fingerprint = ManifestFingerprint.Compute(manifest);
        var markerDir = Path.GetDirectoryName(AiConfigPaths.FingerprintPath);
        if ( markerDir != null ) Directory.CreateDirectory(markerDir);
        File.WriteAllText(AiConfigPaths.FingerprintPath, fingerprint);
        _activity.Log("ai_config_apply", $"Applied version {manifest.Version} — {manifest.Files.Count} file(s) in {manifest.ManagedRoots!.Length} managed root(s)");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string ComputeSha256( string filePath ) {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

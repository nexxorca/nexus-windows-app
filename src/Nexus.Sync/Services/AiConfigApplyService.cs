using System.Security.Cryptography;

using Nexus.Core.Models;
using Nexus.Core.Services;
using Nexus.Sync.Models;

namespace Nexus.Sync.Services;

public class AiConfigApplyService {
    private readonly NexusApiClient _api;
    private readonly ActivityLogService _activity;
    private readonly LogService _log;

    private readonly object _lock = new();
    private bool _running;

    public AiConfigApplyService( NexusApiClient api, ActivityLogService activity, LogService log ) {
        _api = api;
        _activity = activity;
        _log = log;
    }

    public async Task<AiConfigApplyResult> ApplyAsync( AiConfigManifest manifest ) {
        lock ( _lock ) {
            if ( _running ) return new AiConfigApplyResult(false, "aborted", null, "already running");
            _running = true;
        }

        var backupDir = Path.Combine(AiConfigPaths.BackupsRoot, DateTime.UtcNow.ToString("yyyy-MM-dd_HHmmss"));
        string? tempDir = null;

        try {
            // Step 2 + 3a+b: backup pass
            _activity.Log("ai_config_apply", "Backup: starting pre-apply backup");
            EnforceBackupRetention();
            Directory.CreateDirectory(backupDir);
            BackupClaudeRoot(backupDir);
            _activity.Log("ai_config_apply", $"Backup: complete → {backupDir}");

            // Step 4: temp dir
            tempDir = Path.Combine(Path.GetTempPath(), $"nexus-ai-config-{DateTime.UtcNow:yyyyMMddHHmmss}");
            Directory.CreateDirectory(tempDir);

            // Step 5: download
            _activity.Log("ai_config_apply", $"Download: fetching {manifest.Files.Count} files");
            foreach ( var f in manifest.Files ) {
                var destPath = Path.Combine(tempDir, f.Path.Replace('/', Path.DirectorySeparatorChar));
                var parentDir = Path.GetDirectoryName(destPath);
                if ( parentDir != null ) Directory.CreateDirectory(parentDir);

                using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var downloadResult = await _api.DownloadAiConfigFile(f.Path, fileStream);
                if ( ! downloadResult.Success ) {
                    _activity.Log("ai_config_apply", $"Download: ABORT — {f.Path}: {downloadResult.Message}", "error");
                    _log.Error($"AI config download failed for {f.Path}: {downloadResult.Message}");
                    return new AiConfigApplyResult(false, "aborted", null, $"Download failed for {f.Path}: {downloadResult.Message}");
                }
            }
            _activity.Log("ai_config_apply", "Download: all files downloaded");

            // Step 6: SHA-256 verify
            foreach ( var f in manifest.Files ) {
                var filePath = Path.Combine(tempDir, f.Path.Replace('/', Path.DirectorySeparatorChar));
                var actualSha = ComputeSha256(filePath);
                if ( actualSha != f.Sha ) {
                    _activity.Log("ai_config_apply", $"Verify: ABORT — SHA mismatch for {f.Path}", "error");
                    _log.Error($"AI config SHA mismatch: {f.Path} expected={f.Sha} actual={actualSha}");
                    return new AiConfigApplyResult(false, "aborted", null, $"SHA mismatch for {f.Path}");
                }
            }
            _activity.Log("ai_config_apply", "Verify: all SHAs match");

            // Step 7: write phase — copy to .tmp
            var writtenTmps = new List<string>();
            foreach ( var f in manifest.Files ) {
                var targetPath = Path.Combine(AiConfigPaths.ClaudeRoot, f.Path.Replace('/', Path.DirectorySeparatorChar));
                var targetTmp = targetPath + ".tmp";
                var parentDir = Path.GetDirectoryName(targetTmp);
                if ( parentDir != null ) Directory.CreateDirectory(parentDir);

                try {
                    var srcPath = Path.Combine(tempDir, f.Path.Replace('/', Path.DirectorySeparatorChar));
                    using ( var src = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read) )
                    using ( var dst = new FileStream(targetTmp, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, FileOptions.WriteThrough) ) {
                        await src.CopyToAsync(dst);
                        await dst.FlushAsync();
                    }
                    writtenTmps.Add(targetTmp);
                } catch ( Exception ex ) {
                    _activity.Log("ai_config_apply", $"Write: ABORT — {f.Path}: {ex.Message}", "error");
                    _log.Error($"AI config write failed for {f.Path}", ex);
                    DeleteTmpFiles(writtenTmps);
                    return new AiConfigApplyResult(false, "aborted", null, $"Write failed for {f.Path}: {ex.Message}");
                }
            }
            _activity.Log("ai_config_apply", "Write: all .tmp files written");

            // Step 8: rename phase
            var successList = new List<string>();
            foreach ( var f in manifest.Files ) {
                var targetPath = Path.Combine(AiConfigPaths.ClaudeRoot, f.Path.Replace('/', Path.DirectorySeparatorChar));
                var targetTmp = targetPath + ".tmp";

                try {
                    File.Move(targetTmp, targetPath, overwrite: true);
                    successList.Add(f.Path);
                } catch ( Exception ex ) {
                    _activity.Log("ai_config_apply", $"Rename: ROLLBACK triggered — {f.Path}: {ex.Message}", "error");
                    _log.Error($"AI config rename failed for {f.Path} — triggering rollback", ex);

                    var remainingTmps = manifest.Files
                        .Skip(successList.Count + 1)
                        .Select(rf => Path.Combine(AiConfigPaths.ClaudeRoot, rf.Path.Replace('/', Path.DirectorySeparatorChar)) + ".tmp")
                        .ToList();

                    await RollbackAsync(backupDir, successList, manifest);
                    DeleteTmpFiles(remainingTmps);

                    return new AiConfigApplyResult(false, "rolled_back", null,
                        $"Rename failed for {f.Path}: {ex.Message} — restored previous version. Close Claude Code and retry.");
                }
            }
            _activity.Log("ai_config_apply", $"Rename: {successList.Count} files renamed");

            // Step 9: perfect-fit delete
            PerfectFitDelete(manifest);
            _activity.Log("ai_config_apply", "Perfect-fit delete: complete");

            // Step 10: version marker
            var markerDir = Path.GetDirectoryName(AiConfigPaths.VersionMarkerPath);
            if ( markerDir != null ) Directory.CreateDirectory(markerDir);
            File.WriteAllText(AiConfigPaths.VersionMarkerPath, manifest.Version);
            _activity.Log("ai_config_apply", $"Version marker written: {manifest.Version}");

            // Step 11: success
            return new AiConfigApplyResult(true, "applied", manifest.Version, null);

        } finally {
            // Step 12: drop lock (via _running clear) + delete tempDir
            lock ( _lock ) {
                _running = false;
            }

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

    // 4.3 — Rollback: reverse-walk successList, restore each file from backup
    private async Task RollbackAsync( string backupDir, List<string> successList, AiConfigManifest manifest ) {
        _activity.Log("ai_config_apply", $"Rollback: restoring {successList.Count} files from {backupDir}");

        for ( var i = successList.Count - 1; i >= 0; i-- ) {
            var relPath = successList[i];
            var osSep = relPath.Replace('/', Path.DirectorySeparatorChar);
            var src = Path.Combine(backupDir, osSep);
            var target = Path.Combine(AiConfigPaths.ClaudeRoot, osSep);
            var rollbackTmp = target + ".rollback.tmp";

            try {
                if ( ! File.Exists(src) ) {
                    // Newly-added manifest path with no backup counterpart — skip
                    _activity.Log("ai_config_apply", $"Rollback: skipping {relPath} — no backup counterpart (newly-added file)", "warning");
                    _log.Write($"AI config rollback: skipping {relPath} — no backup counterpart");
                    continue;
                }

                File.Copy(src, rollbackTmp, overwrite: true);
                using ( var fs = new FileStream(rollbackTmp, FileMode.Open, FileAccess.Write, FileShare.None, bufferSize: 4096, FileOptions.WriteThrough) ) {
                    await fs.FlushAsync();
                }
                File.Move(rollbackTmp, target, overwrite: true);

            } catch ( Exception ex ) {
                // Rollback step itself failed — STOP immediately
                var criticalMsg = $"Rollback failed at {relPath} — backup at {backupDir}. Restore manually.";
                _activity.Log("ai_config_apply", criticalMsg, "error");
                _log.Error($"AI config rollback failed at {relPath}", ex);

                // Best-effort cleanup of any remaining .rollback.tmp files
                CleanupRollbackTmpFiles();
                throw;
            }
        }

        // Best-effort cleanup of any remaining .rollback.tmp files
        CleanupRollbackTmpFiles();

        _activity.Log("ai_config_apply", "Rollback: complete — previous version restored");
    }

    // 4.4 — Cap-on-take: delete oldest backup dirs until ≤ 4 remain
    private void EnforceBackupRetention() {
        if ( ! Directory.Exists(AiConfigPaths.BackupsRoot) ) return;

        var dirs = Directory.GetDirectories(AiConfigPaths.BackupsRoot)
            .OrderBy(d => Path.GetFileName(d))
            .ToList();

        while ( dirs.Count > 4 ) {
            var oldest = dirs[0];
            dirs.RemoveAt(0);
            try {
                Directory.Delete(oldest, recursive: true);
                _log.Write($"AI config: deleted old backup {oldest}");
            } catch ( Exception ex ) {
                _log.Error($"AI config: failed to delete old backup {oldest}", ex);
            }
        }
    }

    // 4.4 — Recursive backup copy of ~/.claude/ → backupDir, skipping excluded paths
    private void BackupClaudeRoot( string backupDir ) {
        if ( ! Directory.Exists(AiConfigPaths.ClaudeRoot) ) return;

        var claudeRoot = AiConfigPaths.ClaudeRoot;
        var allFiles = Directory.GetFiles(claudeRoot, "*", SearchOption.AllDirectories);

        foreach ( var file in allFiles ) {
            var relPath = Path.GetRelativePath(claudeRoot, file).Replace('\\', '/');

            if ( AiConfigPaths.IsExcluded(relPath) ) continue;

            var destPath = Path.Combine(backupDir, relPath.Replace('/', Path.DirectorySeparatorChar));
            var destDir = Path.GetDirectoryName(destPath);
            if ( destDir != null ) Directory.CreateDirectory(destDir);

            File.Copy(file, destPath, overwrite: false);
        }
    }

    // 4.5 — Perfect-fit delete: remove files not in manifest and not excluded; prune empty dirs
    private void PerfectFitDelete( AiConfigManifest manifest ) {
        if ( ! Directory.Exists(AiConfigPaths.ClaudeRoot) ) return;

        var manifestPaths = new HashSet<string>(
            manifest.Files.Select(f => f.Path.Replace('\\', '/')),
            StringComparer.OrdinalIgnoreCase
        );

        var claudeRoot = AiConfigPaths.ClaudeRoot;
        var allFiles = Directory.GetFiles(claudeRoot, "*", SearchOption.AllDirectories);

        foreach ( var file in allFiles ) {
            var relPath = Path.GetRelativePath(claudeRoot, file).Replace('\\', '/');

            if ( AiConfigPaths.IsExcluded(relPath) ) continue;
            if ( manifestPaths.Contains(relPath) ) continue;

            try {
                File.Delete(file);
                _log.Write($"AI config perfect-fit delete: {relPath}");
            } catch ( Exception ex ) {
                _log.Error($"AI config perfect-fit delete failed for {relPath}", ex);
            }
        }

        // Prune empty directories bottom-up (skip BackupsRoot and excluded dirs)
        var allDirs = Directory.GetDirectories(claudeRoot, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length) // deepest first = bottom-up
            .ToList();

        foreach ( var dir in allDirs ) {
            var relDir = Path.GetRelativePath(claudeRoot, dir).Replace('\\', '/') + "/";

            if ( AiConfigPaths.IsExcluded(relDir) ) continue;
            if ( dir.StartsWith(AiConfigPaths.BackupsRoot, StringComparison.OrdinalIgnoreCase) ) continue;

            try {
                if ( ! Directory.EnumerateFileSystemEntries(dir).Any() ) {
                    Directory.Delete(dir);
                    _log.Write($"AI config: pruned empty dir {relDir}");
                }
            } catch ( Exception ex ) {
                _log.Error($"AI config: failed to prune dir {relDir}", ex);
            }
        }
    }

    private static string ComputeSha256( string filePath ) {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void DeleteTmpFiles( IEnumerable<string> paths ) {
        foreach ( var path in paths ) {
            try {
                if ( File.Exists(path) ) File.Delete(path);
            } catch {
                // Best-effort
            }
        }
    }

    private static void CleanupRollbackTmpFiles() {
        if ( ! Directory.Exists(AiConfigPaths.ClaudeRoot) ) return;

        try {
            var rollbackTmps = Directory.GetFiles(AiConfigPaths.ClaudeRoot, "*.rollback.tmp", SearchOption.AllDirectories);
            foreach ( var f in rollbackTmps ) {
                try { File.Delete(f); } catch { }
            }
        } catch {
            // Best-effort
        }
    }
}

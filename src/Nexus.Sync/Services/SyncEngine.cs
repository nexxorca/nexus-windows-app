using System.Text.RegularExpressions;
using Nexus.Core.Models;
using Nexus.Core.Services;
using Nexus.Sync.Models;

namespace Nexus.Sync.Services;

public class SyncEngine {
    private const long MaxContentSize = 31_457_280; // 30MB — server validation limit

    private readonly AppConfig _config;
    private readonly NexusApiClient _api;
    private readonly StateManager _state;
    private readonly TranscriptScanner _scanner;
    private readonly TranscriptParser _parser;
    private readonly SubagentMapper _mapper;
    private readonly LogService _log;
    private readonly ActivityLogService _activity;

    public SyncEngine(
        AppConfig config,
        NexusApiClient api,
        StateManager state,
        TranscriptScanner scanner,
        TranscriptParser parser,
        SubagentMapper mapper,
        LogService log,
        ActivityLogService activity
    ) {
        _config = config;
        _api = api;
        _state = state;
        _scanner = scanner;
        _parser = parser;
        _mapper = mapper;
        _log = log;
        _activity = activity;
    }

    public Action? OnAuthFailed { get; set; }
    public bool IsRunning { get; private set; }
    public SyncResult? LastSync { get; private set; }
    public event Action? SyncStarted;
    public event Action<SyncResult>? SyncCompleted;

    // Must be called on the UI dispatcher. IsRunning is plain check-then-set, not Interlocked.
    // All current callers (DispatcherTimer.Tick, tray menu, MainWindow buttons) run on the UI thread.
    public async Task RunSync() {
        if ( IsRunning ) return;
        IsRunning = true;

        var uploadCount = 0;
        var skipCount = 0;
        var parseSkipCount = 0;
        var errorCount = 0;
        var authFailed = false;
        var status = "ok";

        _activity.Log("sync_start", "Starting sync cycle");
        _log.Write("=== Sync cycle started ===");
        SyncStarted?.Invoke();

        try {
            try {
                var files = _scanner.Scan();

                foreach ( var file in files ) {
                if ( ! _state.HasChanged(file.FilePath, file.FileSize) ) {
                    skipCount++;
                    continue;
                }

                var lastWrite = File.GetLastWriteTimeUtc(file.FilePath);
                if ( (DateTime.UtcNow - lastWrite).TotalMinutes < 5 ) {
                    _log.Write($"Deferring active file: {Path.GetFileName(file.FilePath)}");
                    skipCount++;
                    continue;
                }

                var metadata = _parser.Parse(file.FilePath);

                if ( metadata == null ) {
                    _log.Write($"Skipping: Parse failed — {file.FilePath}");
                    _activity.Log("sync_skip", $"Skipped: Parse failed — {Path.GetFileName(file.FilePath)}");
                    parseSkipCount++;
                    continue;
                }

                if ( string.IsNullOrEmpty(metadata.ProjectHashId) ) {
                    if ( string.IsNullOrEmpty(metadata.SessionId) ) {
                        _log.Write($"Skipping (non-transcript): {metadata.SkipReason} — {file.FilePath}");
                        _state.MarkIgnored(file.FilePath, file.FileSize);
                        parseSkipCount++;
                        continue;
                    }

                    // Valid session without a project — upload as unassigned
                }

                var fileSize = new FileInfo(file.FilePath).Length;
                if ( fileSize > MaxContentSize ) {
                    _log.Write($"Skipping oversized file ({fileSize:N0} bytes): {file.FilePath}");
                    _activity.Log("sync_skip", $"Skipped: File too large ({fileSize / 1_048_576}MB) — {Path.GetFileName(file.FilePath)}");
                    _state.MarkIgnored(file.FilePath, file.FileSize);
                    parseSkipCount++;
                    continue;
                }

                string content;
                try {
                    using var stream = new FileStream(file.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    content = await reader.ReadToEndAsync();
                } catch ( Exception ex ) {
                    _log.Error($"Failed to read file: {file.FilePath}", ex);
                    errorCount++;
                    continue;
                }

                ApiResult result = null!;
                var maxAttempts = 2;
                for ( var attempt = 1; attempt <= maxAttempts; attempt++ ) {
                    result = await _api.UploadTranscript(
                        metadata.ProjectHashId,
                        metadata.SessionId,
                        "main",
                        content
                    );

                    if ( result.Success || result.IsAuthError || result.IsValidationError || attempt == maxAttempts ) break;

                    _log.Write($"Upload attempt {attempt} failed, retrying in 5s: {Path.GetFileName(file.FilePath)}");
                    await Task.Delay(5000);

                    try {
                        using var retryStream = new FileStream(file.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var retryReader = new StreamReader(retryStream);
                        content = await retryReader.ReadToEndAsync();
                    } catch ( Exception ex ) {
                        _log.Error($"Failed to re-read file for retry: {file.FilePath}", ex);
                        break;
                    }
                }

                if ( result.IsAuthError ) {
                    _activity.Log("api_error", "Authentication failed — check login", "error");
                    _log.Error("Auth error — stopping sync cycle");
                    errorCount++;
                    authFailed = true;
                    break;
                }

                if ( ! result.Success ) {
                    var detail = result.IsValidationError && ! string.IsNullOrEmpty(result.ErrorDetail)
                        ? result.ErrorDetail
                        : result.Message;
                    _activity.Log("api_error", $"Upload failed: {detail} — {Path.GetFileName(file.FilePath)}", "error");
                    errorCount++;
                    continue;
                }

                _state.MarkUploaded(file.FilePath, file.FileSize);
                _activity.Log("file_upload", $"Uploaded: {Path.GetFileName(file.FilePath)} → {metadata.ProjectHashId}");
                uploadCount++;

                var subagents = _mapper.MapSubagents(content);
                foreach ( var sub in subagents ) {
                    _activity.Log("subagent_found", $"Subagent: {sub.SubagentType} ({sub.AgentId})");

                    var subSessionId = $"{metadata.SessionId}-{sub.AgentId}";

                    if ( subSessionId.Length > 100 || ! Regex.IsMatch(subSessionId, @"^[a-zA-Z0-9_\-]+$") ) {
                        _log.Write($"Skipping subagent: invalid sessionId format — {subSessionId}");
                        _activity.Log("sync_skip", $"Skipped: Invalid sessionId — {sub.SubagentType}");
                        continue;
                    }

                    var subFilePath = Path.Combine(
                        Path.GetDirectoryName(file.FilePath) ?? "",
                        $"{sub.AgentId}.jsonl"
                    );

                    if ( ! File.Exists(subFilePath) ) {
                        _log.Write($"Subagent file not found: {subFilePath}");
                        continue;
                    }

                    var subInfo = new FileInfo(subFilePath);
                    if ( ! _state.HasChanged(subFilePath, subInfo.Length) ) continue;

                    if ( subInfo.Length > MaxContentSize ) {
                        _log.Write($"Skipping oversized subagent file ({subInfo.Length:N0} bytes): {subFilePath}");
                        _activity.Log("sync_skip", $"Skipped: Subagent file too large — {sub.SubagentType}");
                        _state.MarkIgnored(subFilePath, subInfo.Length);
                        continue;
                    }

                    string subContent;
                    try {
                        using var subStream = new FileStream(subFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var subReader = new StreamReader(subStream);
                        subContent = await subReader.ReadToEndAsync();
                    } catch ( Exception ex ) {
                        _log.Error($"Failed to read subagent file: {subFilePath}", ex);
                        errorCount++;
                        continue;
                    }

                    ApiResult subResult = null!;
                    var subMaxAttempts = 2;
                    for ( var attempt = 1; attempt <= subMaxAttempts; attempt++ ) {
                        subResult = await _api.UploadTranscript(
                            metadata.ProjectHashId,
                            subSessionId,
                            "subagent",
                            subContent,
                            sub.SubagentType,
                            metadata.SessionId
                        );

                        if ( subResult.Success || subResult.IsAuthError || subResult.IsValidationError || attempt == subMaxAttempts ) break;

                        _log.Write($"Subagent upload attempt {attempt} failed, retrying in 5s: {Path.GetFileName(subFilePath)}");
                        await Task.Delay(5000);

                        try {
                            using var retryStream = new FileStream(subFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            using var retryReader = new StreamReader(retryStream);
                            subContent = await retryReader.ReadToEndAsync();
                        } catch ( Exception ex ) {
                            _log.Error($"Failed to re-read subagent file for retry: {subFilePath}", ex);
                            break;
                        }
                    }

                    if ( subResult.IsAuthError ) {
                        _activity.Log("api_error", "Authentication failed — check login", "error");
                        _log.Error("Auth error on subagent upload — stopping sync cycle");
                        errorCount++;
                        authFailed = true;
                        break;
                    }

                    if ( subResult.Success ) {
                        _state.MarkUploaded(subFilePath, subInfo.Length);
                        _activity.Log("file_upload", $"Uploaded subagent: {sub.SubagentType}");
                        uploadCount++;
                    } else {
                        var detail = subResult.IsValidationError && ! string.IsNullOrEmpty(subResult.ErrorDetail)
                            ? subResult.ErrorDetail
                            : subResult.Message;
                        _activity.Log("api_error", $"Subagent upload failed: {detail} — {sub.SubagentType} ({Path.GetFileName(subFilePath)})", "error");
                        errorCount++;
                    }
                }

                if ( authFailed ) break;
            }
            } catch ( Exception ex ) {
                _log.Error("Sync cycle failed with exception", ex);
                _activity.Log("api_error", $"Sync failed: {ex.Message}", "error");
                errorCount++;
            }

            _state.Save();

            if ( authFailed ) {
                OnAuthFailed?.Invoke();
            }

            status = errorCount > 0 ? "error" : "ok";

            _activity.Log("sync_complete",
                $"Sync: {uploadCount} uploaded, {skipCount} unchanged, {parseSkipCount} skipped, {errorCount} errors",
                status);
        } finally {
            _activity.Flush();

            var result = new SyncResult(
                uploadCount, skipCount, parseSkipCount, errorCount, DateTime.Now, status
            );
            LastSync = result;

            _log.Write($"=== Sync complete: {uploadCount} uploaded, {skipCount} unchanged, {parseSkipCount} skipped, {errorCount} errors ===");

            IsRunning = false;
            SyncCompleted?.Invoke(result);
        }
    }
}

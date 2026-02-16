using Nexus.Core.Models;
using Nexus.Core.Services;
using Nexus.Sync.Models;

namespace Nexus.Sync.Services;

public class SyncEngine {
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

    public bool LastSyncHadErrors { get; private set; }

    public async Task RunSync() {
        _activity.Log("sync_start", "Starting sync cycle");
        _log.Write("=== Sync cycle started ===");

        var uploadCount = 0;
        var skipCount = 0;
        var errorCount = 0;

        try {
            var files = _scanner.Scan(_config.LookbackMinutes);

            foreach ( var file in files ) {
                var metadata = _parser.Parse(file.FilePath);
                if ( metadata == null || string.IsNullOrEmpty(metadata.ProjectHashId) ) {
                    _log.Write($"Skipping (no metadata): {file.FilePath}");
                    skipCount++;
                    continue;
                }

                if ( ! _state.HasChanged(file.FilePath, file.FileSize) ) {
                    skipCount++;
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

                var result = await _api.UploadTranscript(
                    metadata.ProjectHashId,
                    metadata.SessionId,
                    "main",
                    content
                );

                if ( result.IsAuthError ) {
                    _activity.Log("api_error", "Authentication failed — check login", "error");
                    _log.Error("Auth error — stopping sync cycle");
                    errorCount++;
                    break;
                }

                if ( ! result.Success ) {
                    _activity.Log("api_error", $"Upload failed: {result.Message}", "error");
                    errorCount++;
                    continue;
                }

                _state.MarkUploaded(file.FilePath, file.FileSize);
                _activity.Log("file_upload", $"Uploaded: {Path.GetFileName(file.FilePath)}");
                uploadCount++;

                var subagents = _mapper.MapSubagents(content);
                foreach ( var sub in subagents ) {
                    _activity.Log("subagent_found", $"Subagent: {sub.SubagentType} ({sub.AgentId})");

                    var subSessionId = $"{metadata.SessionId}-{sub.AgentId}";
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

                    string subContent;
                    try {
                        using var subStream = new FileStream(subFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var subReader = new StreamReader(subStream);
                        subContent = await subReader.ReadToEndAsync();
                    } catch ( Exception ex ) {
                        _log.Error($"Failed to read subagent file: {subFilePath}", ex);
                        continue;
                    }

                    var subResult = await _api.UploadTranscript(
                        metadata.ProjectHashId,
                        subSessionId,
                        "subagent",
                        subContent,
                        sub.SubagentType,
                        metadata.SessionId
                    );

                    if ( subResult.Success ) {
                        _state.MarkUploaded(subFilePath, subInfo.Length);
                        _activity.Log("file_upload", $"Uploaded subagent: {sub.SubagentType}");
                        uploadCount++;
                    } else {
                        _activity.Log("api_error", $"Subagent upload failed: {subResult.Message}", "error");
                        errorCount++;
                    }
                }
            }
        } catch ( Exception ex ) {
            _log.Error("Sync cycle failed with exception", ex);
            _activity.Log("api_error", $"Sync failed: {ex.Message}", "error");
            errorCount++;
        }

        _state.Save();
        _activity.Flush();

        LastSyncHadErrors = errorCount > 0;

        var status = errorCount > 0 ? "error" : "ok";
        _activity.Log("sync_complete",
            $"Sync complete: {uploadCount} uploaded, {skipCount} skipped, {errorCount} errors",
            status);

        _log.Write($"=== Sync complete: {uploadCount} uploaded, {skipCount} skipped, {errorCount} errors ===");
    }
}

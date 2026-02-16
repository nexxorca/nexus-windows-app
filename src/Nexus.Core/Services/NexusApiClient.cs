using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Nexus.Core.Models;

namespace Nexus.Core.Services;

public class NexusApiClient {
    private readonly HttpClient _http;
    private readonly LogService _log;
    private string _baseUrl = "";

    private static readonly JsonSerializerOptions _jsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public NexusApiClient( LogService log ) {
        _log = log;
        _http = new HttpClient {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public void Configure( string baseUrl, string? authToken ) {
        _baseUrl = baseUrl.TrimEnd('/');
        _http.DefaultRequestHeaders.Authorization = ! string.IsNullOrEmpty(authToken)
            ? new AuthenticationHeaderValue("Bearer", authToken)
            : null;
    }

    public async Task<ApiResult> Login( string nexusUrl, string email, string password ) {
        try {
            var url = nexusUrl.TrimEnd('/') + "/api/v1/auth/login";
            var payload = new { email, password };
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync(url, content);
            var body = await response.Content.ReadAsStringAsync();

            if ( ! response.IsSuccessStatusCode ) {
                _log.Write($"Login failed: {(int)response.StatusCode} - {body}");
                return ApiResult.Fail((int)response.StatusCode, "Login failed", body);
            }

            _log.Write("Login successful");
            return new ApiResult {
                Success = true,
                StatusCode = (int)response.StatusCode,
                Message = body
            };
        } catch ( TaskCanceledException ) {
            _log.Write("Login timed out");
            return ApiResult.Fail(0, "Request timed out");
        } catch ( HttpRequestException ex ) {
            _log.Write($"Login connection error: {ex.Message}");
            return ApiResult.Fail(0, "Could not connect to server", ex.Message);
        }
    }

    public async Task<ApiResult> UploadTranscript(
        string projectHashId, string sessionId, string type,
        string content, string? subagentType = null,
        string? parentTranscriptId = null
    ) {
        try {
            var url = _baseUrl + "/api/v1/transcripts";

            var payload = new Dictionary<string, string?> {
                ["project_hash_id"] = projectHashId,
                ["session_id"] = sessionId,
                ["type"] = type,
                ["content"] = content,
                ["subagent_type"] = subagentType,
                ["parent_transcript_id"] = parentTranscriptId
            };

            var filtered = payload.Where(kv => kv.Value != null)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            var json = JsonSerializer.Serialize(filtered, _jsonOptions);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
            httpContent.Headers.Add("Accept", "application/json");

            var response = await _http.PostAsync(url, httpContent);
            var body = await response.Content.ReadAsStringAsync();

            if ( ! response.IsSuccessStatusCode ) {
                _log.Write($"Upload failed [{sessionId}]: {(int)response.StatusCode} - {body}");
                return ApiResult.Fail((int)response.StatusCode, "Upload failed", body);
            }

            return ApiResult.Ok();
        } catch ( TaskCanceledException ) {
            _log.Write($"Upload timed out [{sessionId}]");
            return ApiResult.Fail(0, "Request timed out");
        } catch ( HttpRequestException ex ) {
            _log.Write($"Upload connection error [{sessionId}]: {ex.Message}");
            return ApiResult.Fail(0, "Could not connect to server", ex.Message);
        }
    }
}

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

    private static readonly JsonSerializerOptions _jsonOptions = new();

    public NexusApiClient( LogService log ) {
        _log = log;
        _http = new HttpClient {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var response = await _http.PostAsync(url, content, cts.Token);
            var body = await response.Content.ReadAsStringAsync();

            if ( ! response.IsSuccessStatusCode ) {
                if ( (int)response.StatusCode == 429 ) {
                    var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60;
                    _log.Write($"Login rate-limited: retry after {retryAfter}s");
                    return ApiResult.Fail(429, $"Too many login attempts. Please wait {(int)retryAfter} seconds.");
                }

                if ( (int)response.StatusCode == 401 ) {
                    _log.Write("Login failed: invalid credentials");
                    return ApiResult.Fail(401, "Invalid email or password.");
                }

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
        string? projectHashId, string sessionId, string type,
        string content, string? subagentType = null,
        string? parentTranscriptId = null
    ) {
        try {
            var url = _baseUrl + "/api/v1/transcripts";

            var payload = new Dictionary<string, string> {
                ["session_id"] = sessionId,
                ["type"] = type,
                ["content"] = content
            };

            if ( projectHashId != null ) {
                payload["project_hash_id"] = projectHashId;
            }

            if ( subagentType != null ) {
                payload["subagent_type"] = subagentType;
            }

            if ( parentTranscriptId != null ) {
                payload["parent_transcript_id"] = parentTranscriptId;
            }

            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
            var response = await _http.PostAsync(url, httpContent, cts.Token);
            var body = await response.Content.ReadAsStringAsync();

            if ( ! response.IsSuccessStatusCode ) {
                _log.Write($"Upload failed [{sessionId}]: {(int)response.StatusCode} - {body}");
                return ApiResult.Fail((int)response.StatusCode, "Upload failed", body);
            }

            return new ApiResult {
                Success = true,
                StatusCode = (int)response.StatusCode,
                Message = body
            };
        } catch ( TaskCanceledException ) {
            _log.Write($"Upload timed out [{sessionId}]");
            return ApiResult.Fail(0, "Request timed out");
        } catch ( HttpRequestException ex ) {
            _log.Write($"Upload connection error [{sessionId}]: {ex.Message}");
            return ApiResult.Fail(0, "Could not connect to server", ex.Message);
        }
    }

    public async Task RevokeToken() {
        try {
            var url = _baseUrl + "/api/v1/auth/logout";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _http.PostAsync(url, null, cts.Token);
        } catch {
            // Fire-and-forget — if revoke fails (offline, expired), proceed with local logout
        }
    }
}

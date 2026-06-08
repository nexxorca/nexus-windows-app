// Test seam: NexusApiClient has an internal constructor accepting a pre-built HttpClient.
// This lets tests inject a FakeHttpMessageHandler without touching network or config files.
// The internal constructor is exposed to this assembly via InternalsVisibleTo in Nexus.Core.csproj.
//
// NexusApiClient.Configure() must be called before GetAiConfigManifest() — it sets _baseUrl.
// Tests use http://localhost (allowed by the HTTPS enforcement rule as a local-dev exemption).

using System.Net;
using System.Net.Http;
using System.Text;

using Nexus.Core.Models;
using Nexus.Core.Services;

namespace Nexus.Sync.Tests;

public class NexusApiClientAiConfigTests {
    private const string LocalBase = "http://localhost";

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static (NexusApiClient client, FakeHandler handler) Build( string baseUrl = LocalBase ) {
        var handler = new FakeHandler();
        var http = new HttpClient(handler);
        var log = new LogService();
        var client = new NexusApiClient(http, log);
        client.Configure(baseUrl, "test-token-" + Guid.NewGuid().ToString("N"));
        return (client, handler);
    }

    private static string FullManifestJson( bool includeManagedRoots = true ) {
        var roots = includeManagedRoots
            ? """
              "managed_roots": ["agents/", "conventions/"],
              """
            : "";
        return $$"""
            {
              "version": "2026-06-08",
              "generated_at": "2026-06-08T00:00:00Z",
              {{roots}}
              "files": [
                { "path": "agents/dev/AGENT.md", "sha": "abc123", "size": 512 },
                { "path": "conventions/main.md",  "sha": "def456", "size": 256 }
              ]
            }
            """;
    }

    // ─── GetAiConfigManifest — success paths ─────────────────────────────────

    [Fact]
    public async Task GetAiConfigManifest_200WithManagedRoots_DeserializesCorrectly() {
        var (client, handler) = Build();
        handler.Respond(HttpStatusCode.OK, FullManifestJson(includeManagedRoots: true));

        var result = await client.GetAiConfigManifest();

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("2026-06-08", result.Data!.Version);
        Assert.NotNull(result.Data.ManagedRoots);
        Assert.Equal(2, result.Data.ManagedRoots!.Length);
        Assert.Contains("agents/", result.Data.ManagedRoots);
        Assert.Contains("conventions/", result.Data.ManagedRoots);
        Assert.Equal(2, result.Data.Files.Count);
        Assert.Equal("agents/dev/AGENT.md", result.Data.Files[0].Path);
    }

    [Fact]
    public async Task GetAiConfigManifest_200WithoutManagedRoots_FallsBackToEmptyArray() {
        // Server omits managed_roots — Step 1.3 fallback: treat as empty array, log warning.
        var (client, handler) = Build();
        handler.Respond(HttpStatusCode.OK, FullManifestJson(includeManagedRoots: false));

        var result = await client.GetAiConfigManifest();

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        // ManagedRoots must be non-null (fallback) and empty
        Assert.NotNull(result.Data!.ManagedRoots);
        Assert.Empty(result.Data.ManagedRoots!);
        // Files still deserialize normally
        Assert.Equal(2, result.Data.Files.Count);
    }

    // ─── GetAiConfigManifest — error paths ───────────────────────────────────

    [Fact]
    public async Task GetAiConfigManifest_404_ReturnsFailWithNoSnapshotMessage() {
        var (client, handler) = Build();
        handler.Respond(HttpStatusCode.NotFound, "");

        var result = await client.GetAiConfigManifest();

        Assert.False(result.Success);
        Assert.Equal(404, result.StatusCode);
        Assert.Contains("no snapshot", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAiConfigManifest_401_ReturnsAuthError() {
        var (client, handler) = Build();
        handler.Respond(HttpStatusCode.Unauthorized, "Unauthorized");

        var result = await client.GetAiConfigManifest();

        Assert.False(result.Success);
        Assert.True(result.IsAuthError);
        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public async Task GetAiConfigManifest_Timeout_ReturnsFailureWithoutException() {
        var (client, handler) = Build();
        handler.ThrowOn(new OperationCanceledException("simulated timeout"));

        // Must NOT throw — the client catches TaskCanceledException/OperationCanceledException
        var result = await client.GetAiConfigManifest();

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task GetAiConfigManifest_MalformedJson_ReturnsFail() {
        var (client, handler) = Build();
        handler.Respond(HttpStatusCode.OK, "{ this is not valid json %%%");

        var result = await client.GetAiConfigManifest();

        Assert.False(result.Success);
    }

    // ─── Configure — URL validation ──────────────────────────────────────────

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("http://192.168.1.1")]
    [InlineData("http://my-server.local")]
    public void Configure_NonHttpsNonLocalUrl_ThrowsArgumentException( string badUrl ) {
        var handler = new FakeHandler();
        var http = new HttpClient(handler);
        var log = new LogService();
        var client = new NexusApiClient(http, log);

        Assert.Throws<ArgumentException>(() => client.Configure(badUrl, null));
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:8000")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:8080")]
    public void Configure_HttpLocalhost_Succeeds( string localUrl ) {
        var handler = new FakeHandler();
        var http = new HttpClient(handler);
        var log = new LogService();
        var client = new NexusApiClient(http, log);

        // Must not throw
        client.Configure(localUrl, null);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://nexus.nexxor.ca")]
    [InlineData("https://192.168.1.1")]
    public void Configure_Https_Succeeds( string httpsUrl ) {
        var handler = new FakeHandler();
        var http = new HttpClient(handler);
        var log = new LogService();
        var client = new NexusApiClient(http, log);

        // Must not throw
        client.Configure(httpsUrl, null);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com")]
    [InlineData("")]
    [InlineData(":::bad:::")]
    public void Configure_UnparseableOrInvalidScheme_ThrowsArgumentException( string badUrl ) {
        var handler = new FakeHandler();
        var http = new HttpClient(handler);
        var log = new LogService();
        var client = new NexusApiClient(http, log);

        Assert.Throws<ArgumentException>(() => client.Configure(badUrl, null));
    }

    // ─── Fake handler ─────────────────────────────────────────────────────────

    private class FakeHandler : HttpMessageHandler {
        private HttpStatusCode _status;
        private string _body = "";
        private Exception? _throw;

        public void Respond( HttpStatusCode status, string body ) {
            _status = status;
            _body = body;
            _throw = null;
        }

        public void ThrowOn( Exception ex ) {
            _throw = ex;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken ) {
            if ( _throw != null ) throw _throw;

            var response = new HttpResponseMessage(_status) {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}

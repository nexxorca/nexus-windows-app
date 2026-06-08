using Nexus.Core.Models;

namespace Nexus.Core.Services;

public interface INexusApiClient {
    Task<ApiResult<AiConfigManifest>> GetAiConfigManifest();
    Task<ApiResult> DownloadAiConfigFile( string path, Stream destination );
}

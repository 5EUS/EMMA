using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace EMMA.PluginTemplate.Infrastructure;

public sealed class HttpJsonClient(HttpClient httpClient, ILogger<HttpJsonClient> logger)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly ILogger<HttpJsonClient> _logger = logger;

    public async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("HTTP {StatusCode} for {Path}", (int)response.StatusCode, path);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    public async Task<T?> GetJsonAsync<T>(string path, JsonSerializerOptions? options, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("HTTP {StatusCode} for {Path}", (int)response.StatusCode, path);
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(options, cancellationToken);
    }
}

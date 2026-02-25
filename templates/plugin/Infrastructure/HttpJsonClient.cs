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

    public static JsonElement? GetObject(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        return null;
    }

    public static JsonElement? GetArray(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            return value;
        }

        return null;
    }

    public static string? GetString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    public static string? PickMapString(JsonElement? map)
    {
        if (map is null || map.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (map.Value.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String)
        {
            var value = en.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        foreach (var property in map.Value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }
}

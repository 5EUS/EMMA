using System.Text.Json;
using EMMA.Contracts.Plugins;
using EMMA.PluginTemplate.Infrastructure;
using Microsoft.Extensions.Logging;

namespace EMMA.PluginTemplate.Services;

public sealed class TypedApiClient(HttpJsonClient httpJsonClient, ILogger<TypedApiClient> logger)
{
    private const string SourceId = "replace-me";
    private const string MediaTypePaged = "paged";
    private readonly HttpJsonClient _httpJsonClient = httpJsonClient;
    private readonly ILogger<TypedApiClient> _logger = logger;

    public async Task<IReadOnlyList<MediaSummary>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        using var doc = await _httpJsonClient.GetJsonAsync($"/search?q={Uri.EscapeDataString(query)}", cancellationToken);
        if (doc is null)
        {
            return [];
        }

        var data = HttpJsonClient.GetArray(doc.RootElement, "data");
        if (data is null)
        {
            return [];
        }

        var results = new List<MediaSummary>();
        foreach (var item in data.Value.EnumerateArray())
        {
            var id = HttpJsonClient.GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var title = HttpJsonClient.GetString(item, "title") ?? "Untitled";
            results.Add(new MediaSummary
            {
                Id = id,
                Source = SourceId,
                Title = title,
                MediaType = MediaTypePaged
            });
        }

        _logger.LogInformation("Search query={Query} results={Count}", query, results.Count);
        return results;
    }
}

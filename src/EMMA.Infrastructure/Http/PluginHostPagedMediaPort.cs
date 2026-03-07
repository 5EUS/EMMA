using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using EMMA.Application.Ports;
using EMMA.Domain;
using Microsoft.Extensions.Options;

namespace EMMA.Infrastructure.Http;

/// <summary>
/// Plugin-host-backed port for paged media operations.
/// </summary>
public sealed partial class PluginHostPagedMediaPort(HttpClient client, IOptions<PluginHostClientOptions> options) : IMediaSearchPort, IPageProviderPort
{
    private readonly HttpClient _client = client;
    private readonly PluginHostClientOptions _options = options.Value;

    public async Task<IReadOnlyList<MediaSummary>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var url = BuildUrl("/pipeline/paged/search", new Dictionary<string, string?>
        {
            ["query"] = query
        });

        var response = await _client.GetAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var results = JsonSerializer.Deserialize(
            payload,
            typeof(List<SearchResultDto>),
            PluginHostPagedMediaPortJsonContext.Default) as List<SearchResultDto> ?? [];

        return [.. results.Select(item => new MediaSummary(
            MediaId.Create(item.Id ?? string.Empty),
            item.Source ?? string.Empty,
            item.Title ?? string.Empty,
            ParseMediaType(item.MediaType),
            string.IsNullOrWhiteSpace(item.ThumbnailUrl) ? null : item.ThumbnailUrl,
            string.IsNullOrWhiteSpace(item.Description) ? null : item.Description))];
    }

    public async Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(MediaId mediaId, CancellationToken cancellationToken)
    {
        var url = BuildUrl("/pipeline/paged/chapters", new Dictionary<string, string?>
        {
            ["mediaId"] = mediaId.Value
        });

        var response = await _client.GetAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var results = JsonSerializer.Deserialize(
            payload,
            typeof(List<ChapterDto>),
            PluginHostPagedMediaPortJsonContext.Default) as List<ChapterDto> ?? [];

        return [.. results.Select(item => new MediaChapter(
            item.Id ?? string.Empty,
            item.Number,
            item.Title ?? string.Empty))];
    }

    public async Task<MediaPage> GetPageAsync(
        MediaId mediaId,
        string chapterId,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl("/pipeline/paged/page", new Dictionary<string, string?>
        {
            ["mediaId"] = mediaId.Value,
            ["chapterId"] = chapterId,
            ["index"] = pageIndex.ToString()
        });

        var response = await _client.GetAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize(
            payload,
            typeof(PageDto),
            PluginHostPagedMediaPortJsonContext.Default) as PageDto
            ?? throw new InvalidOperationException("Plugin host returned an empty page payload.");

        if (!Uri.TryCreate(result.ContentUri, UriKind.Absolute, out var contentUri))
        {
            throw new InvalidOperationException("Plugin host returned an invalid page URI.");
        }

        return new MediaPage(result.Id ?? string.Empty, result.Index, contentUri);
    }

    public async Task<MediaPageAsset> GetPageAssetAsync(
        MediaId mediaId,
        string chapterId,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl("/pipeline/paged/page-asset", new Dictionary<string, string?>
        {
            ["mediaId"] = mediaId.Value,
            ["chapterId"] = chapterId,
            ["index"] = pageIndex.ToString()
        });

        var response = await _client.GetAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

        return new MediaPageAsset(contentType, payload, DateTimeOffset.UtcNow);
    }

    private string BuildUrl(string path, IReadOnlyDictionary<string, string?> parameters)
    {
        var parts = new List<string>();
        foreach (var (key, value) in parameters)
        {
            parts.Add($"{key}={Uri.EscapeDataString(value ?? string.Empty)}");
        }

        if (!string.IsNullOrWhiteSpace(_options.PluginId))
        {
            parts.Add($"pluginId={Uri.EscapeDataString(_options.PluginId)}");
        }

        return parts.Count == 0 ? path : $"{path}?{string.Join("&", parts)}";
    }

    private static MediaType ParseMediaType(string? value)
    {
        return string.Equals(value, "video", StringComparison.OrdinalIgnoreCase)
            ? MediaType.Video
            : MediaType.Paged;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var details = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = string.IsNullOrWhiteSpace(details)
            ? $"Plugin host request failed with status {(int)response.StatusCode}."
            : details;

        throw new InvalidOperationException(message);
    }

    private sealed record SearchResultDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("source")]
        public string? Source { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("mediaType")]
        public string? MediaType { get; init; }

        [JsonPropertyName("thumbnailUrl")]
        public string? ThumbnailUrl { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }
    }

    private sealed record ChapterDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("number")]
        public int Number { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }
    }

    private sealed record PageDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("index")]
        public int Index { get; init; }

        [JsonPropertyName("contentUri")]
        public string? ContentUri { get; init; }
    }

    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(List<SearchResultDto>))]
    [JsonSerializable(typeof(List<ChapterDto>))]
    [JsonSerializable(typeof(PageDto))]
    private sealed partial class PluginHostPagedMediaPortJsonContext : JsonSerializerContext;
}

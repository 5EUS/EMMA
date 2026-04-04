using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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

        var results = await GetAndDeserializeAsync(
            url,
            PluginHostPagedMediaPortJsonContext.Default.ListSearchResultDto,
            () => [],
            cancellationToken);

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

        var results = await GetAndDeserializeAsync(
            url,
            PluginHostPagedMediaPortJsonContext.Default.ListChapterDto,
            () => [],
            cancellationToken);

        return [.. results.Select(item => new MediaChapter(
            item.Id ?? string.Empty,
            item.Number,
            item.Title ?? string.Empty,
            item.UploaderGroups ?? []))];
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

        var result = await GetAndDeserializeAsync(
            url,
            PluginHostPagedMediaPortJsonContext.Default.PageDto,
            () => throw new InvalidOperationException("Plugin host returned an empty page payload."),
            cancellationToken);

        var contentUri = ParseAbsoluteUri(result.ContentUri, "Plugin host returned an invalid page URI.");

        return new MediaPage(result.Id ?? string.Empty, result.Index, contentUri);
    }

    public async Task<MediaPagesResult> GetPagesAsync(
        MediaId mediaId,
        string chapterId,
        int startIndex,
        int count,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl("/pipeline/paged/pages", new Dictionary<string, string?>
        {
            ["mediaId"] = mediaId.Value,
            ["chapterId"] = chapterId,
            ["startIndex"] = startIndex.ToString(),
            ["count"] = count.ToString()
        });

        var result = await GetAndDeserializeAsync(
            url,
            PluginHostPagedMediaPortJsonContext.Default.PagesResultDto,
            () => new PagesResultDto(),
            cancellationToken);

        var pages = (result.Pages ?? [])
            .Select(item =>
            {
                var contentUri = ParseAbsoluteUri(item.ContentUri, "Plugin host returned an invalid page URI.");

                return new MediaPage(item.Id ?? string.Empty, item.Index, contentUri);
            })
            .ToList();

        return new MediaPagesResult(pages, result.ReachedEnd);
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

    private async Task<T> GetAndDeserializeAsync<T>(
        string url,
        JsonTypeInfo<T> typeInfo,
        Func<T> fallback,
        CancellationToken cancellationToken)
    {
        var response = await _client.GetAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken) ?? fallback();
    }

    private static Uri ParseAbsoluteUri(string? value, string errorMessage)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri;
        }

        throw new InvalidOperationException(errorMessage);
    }

    private static MediaType ParseMediaType(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.Equals(normalized, "video", StringComparison.OrdinalIgnoreCase))
        {
            return MediaType.Video;
        }

        if (string.Equals(normalized, "audio", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "music", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "podcast", StringComparison.OrdinalIgnoreCase))
        {
            return MediaType.Audio;
        }

        return MediaType.Paged;
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

        [JsonPropertyName("uploaderGroups")]
        public List<string>? UploaderGroups { get; init; }
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

    private sealed record PagesResultDto
    {
        [JsonPropertyName("pages")]
        public List<PageDto>? Pages { get; init; }

        [JsonPropertyName("reachedEnd")]
        public bool ReachedEnd { get; init; }
    }

    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(List<SearchResultDto>))]
    [JsonSerializable(typeof(List<ChapterDto>))]
    [JsonSerializable(typeof(PageDto))]
    [JsonSerializable(typeof(PagesResultDto))]
    private sealed partial class PluginHostPagedMediaPortJsonContext : JsonSerializerContext;
}

using System.Collections.Concurrent;
using System.Net;
using EMMA.Application.Ports;
using PluginContracts = EMMA.Contracts.Plugins;
using EMMA.Domain;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Services;

internal sealed record PluginGrpcEndpoint(PluginRecord Record, Uri Address, string CorrelationId);

internal sealed class PluginSearchPort(
    PluginGrpcEndpoint endpoint,
    IOptions<PluginHostOptions> options,
    ILogger<PluginSearchPort> logger) : IMediaSearchPort
{
    private readonly PluginGrpcEndpoint _endpoint = endpoint;
    private readonly IOptions<PluginHostOptions> _options = options;
    private readonly ILogger<PluginSearchPort> _logger = logger;

    public async Task<IReadOnlyList<MediaSummary>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var (cts, deadlineUtc) = PluginGrpcHelpers.CreateCallTimeout(_options, cancellationToken);
        using var ctsScope = cts;
        using var lease = await PluginGrpcHelpers.AcquireLeaseAsync(
            _endpoint.Record.Manifest.Id,
            _options.Value,
            cts.Token);

        using var httpClient = PluginGrpcHelpers.CreateHttpClient(_endpoint.Address);
        using var channel = GrpcChannel.ForAddress(_endpoint.Address, new GrpcChannelOptions
        {
            HttpClient = httpClient
        });

        var client = new PluginContracts.SearchProvider.SearchProviderClient(channel);
        var response = await client.SearchAsync(new PluginContracts.SearchRequest
        {
            Query = query ?? string.Empty,
            Context = PluginGrpcHelpers.CreateRequestContext(_endpoint.CorrelationId, deadlineUtc)
        }, headers: PluginGrpcHelpers.CreateHeaders(_endpoint.CorrelationId), cancellationToken: cts.Token);

        var results = response.Results
            .Select(MapSummary)
            .ToList();

        _logger.LogInformation(
            "Pipeline search {CorrelationId} pluginId={PluginId} query={Query} count={Count}",
            _endpoint.CorrelationId,
            _endpoint.Record.Manifest.Id,
            query ?? string.Empty,
            results.Count);

        return results;
    }

    private static MediaSummary MapSummary(PluginContracts.MediaSummary result)
    {
        var mediaId = MediaId.Create(result.Id);
        var source = result.Source ?? string.Empty;
        var title = result.Title ?? string.Empty;
        var mediaType = ParseMediaType(result.MediaType);
        var thumbnailUrl = string.IsNullOrWhiteSpace(result.ThumbnailUrl)
            ? null
            : result.ThumbnailUrl;
        var description = string.IsNullOrWhiteSpace(result.Description)
            ? null
            : result.Description;

        return new MediaSummary(mediaId, source, title, mediaType, thumbnailUrl, description);
    }

    private static MediaType ParseMediaType(string? value)
    {
        if (string.Equals(value, "video", StringComparison.OrdinalIgnoreCase))
        {
            return MediaType.Video;
        }

        return MediaType.Paged;
    }
}

internal sealed class PluginPageProviderPort(
    PluginGrpcEndpoint endpoint,
    IOptions<PluginHostOptions> options,
    ILogger<PluginPageProviderPort> logger) : IPageProviderPort
{
    private readonly PluginGrpcEndpoint _endpoint = endpoint;
    private readonly IOptions<PluginHostOptions> _options = options;
    private readonly ILogger<PluginPageProviderPort> _logger = logger;

    public async Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(MediaId mediaId, CancellationToken cancellationToken)
    {
        var (cts, deadlineUtc) = PluginGrpcHelpers.CreateCallTimeout(_options, cancellationToken);
        using var ctsScope = cts;
        using var lease = await PluginGrpcHelpers.AcquireLeaseAsync(
            _endpoint.Record.Manifest.Id,
            _options.Value,
            cts.Token);

        using var httpClient = PluginGrpcHelpers.CreateHttpClient(_endpoint.Address);
        using var channel = GrpcChannel.ForAddress(_endpoint.Address, new GrpcChannelOptions
        {
            HttpClient = httpClient
        });

        var client = new PluginContracts.PageProvider.PageProviderClient(channel);
        var response = await client.GetChaptersAsync(new PluginContracts.ChaptersRequest
        {
            MediaId = mediaId.Value,
            Context = PluginGrpcHelpers.CreateRequestContext(_endpoint.CorrelationId, deadlineUtc)
        }, headers: PluginGrpcHelpers.CreateHeaders(_endpoint.CorrelationId), cancellationToken: cts.Token);

        var chapters = response.Chapters
            .Select(MapChapter)
            .ToList();

        _logger.LogInformation(
            "Pipeline chapters {CorrelationId} pluginId={PluginId} mediaId={MediaId} count={Count}",
            _endpoint.CorrelationId,
            _endpoint.Record.Manifest.Id,
            mediaId.Value,
            chapters.Count);

        return chapters;
    }

    public async Task<MediaPage> GetPageAsync(
        MediaId mediaId,
        string chapterId,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var (cts, deadlineUtc) = PluginGrpcHelpers.CreateCallTimeout(_options, cancellationToken);
        using var ctsScope = cts;
        using var lease = await PluginGrpcHelpers.AcquireLeaseAsync(
            _endpoint.Record.Manifest.Id,
            _options.Value,
            cts.Token);

        using var httpClient = PluginGrpcHelpers.CreateHttpClient(_endpoint.Address);
        using var channel = GrpcChannel.ForAddress(_endpoint.Address, new GrpcChannelOptions
        {
            HttpClient = httpClient
        });

        var client = new PluginContracts.PageProvider.PageProviderClient(channel);
        var response = await client.GetPageAsync(new PluginContracts.PageRequest
        {
            MediaId = mediaId.Value,
            ChapterId = chapterId,
            Index = pageIndex,
            Context = PluginGrpcHelpers.CreateRequestContext(_endpoint.CorrelationId, deadlineUtc)
        }, headers: PluginGrpcHelpers.CreateHeaders(_endpoint.CorrelationId), cancellationToken: cts.Token);

        if (response.Page is null)
        {
            throw new KeyNotFoundException($"Page {pageIndex} not found for chapter {chapterId}.");
        }

        var page = MapPage(response.Page);

        _logger.LogInformation(
            "Pipeline page {CorrelationId} pluginId={PluginId} mediaId={MediaId} chapterId={ChapterId} index={Index}",
            _endpoint.CorrelationId,
            _endpoint.Record.Manifest.Id,
            mediaId.Value,
            chapterId,
            pageIndex);

        return page;
    }

    public async Task<MediaPagesResult> GetPagesAsync(
        MediaId mediaId,
        string chapterId,
        int startIndex,
        int count,
        CancellationToken cancellationToken)
    {
        var (cts, deadlineUtc) = PluginGrpcHelpers.CreateCallTimeout(_options, cancellationToken);
        using var ctsScope = cts;
        using var lease = await PluginGrpcHelpers.AcquireLeaseAsync(
            _endpoint.Record.Manifest.Id,
            _options.Value,
            cts.Token);

        using var httpClient = PluginGrpcHelpers.CreateHttpClient(_endpoint.Address);
        using var channel = GrpcChannel.ForAddress(_endpoint.Address, new GrpcChannelOptions
        {
            HttpClient = httpClient
        });

        var client = new PluginContracts.PageProvider.PageProviderClient(channel);
        var response = await client.GetPagesAsync(new PluginContracts.PagesRequest
        {
            MediaId = mediaId.Value,
            ChapterId = chapterId,
            StartIndex = startIndex,
            Count = count,
            Context = PluginGrpcHelpers.CreateRequestContext(_endpoint.CorrelationId, deadlineUtc)
        }, headers: PluginGrpcHelpers.CreateHeaders(_endpoint.CorrelationId), cancellationToken: cts.Token);

        var pages = response.Pages
            .Select(MapPage)
            .ToList();

        _logger.LogInformation(
            "Pipeline pages {CorrelationId} pluginId={PluginId} mediaId={MediaId} chapterId={ChapterId} startIndex={StartIndex} count={Count} resultCount={ResultCount} reachedEnd={ReachedEnd}",
            _endpoint.CorrelationId,
            _endpoint.Record.Manifest.Id,
            mediaId.Value,
            chapterId,
            startIndex,
            count,
            pages.Count,
            response.ReachedEnd);

        return new MediaPagesResult(pages, response.ReachedEnd);
    }

    private static MediaChapter MapChapter(PluginContracts.MediaChapter chapter)
    {
        var chapterId = chapter.Id ?? string.Empty;
        var title = chapter.Title ?? string.Empty;
        var uploaderGroups = chapter.UploaderGroups
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new MediaChapter(chapterId, chapter.Number, title, uploaderGroups);
    }

    private static MediaPage MapPage(PluginContracts.MediaPage page)
    {
        if (!Uri.TryCreate(page.ContentUri, UriKind.Absolute, out var contentUri))
        {
            throw new InvalidOperationException("Page content URI is invalid.");
        }

        return new MediaPage(page.Id ?? string.Empty, page.Index, contentUri);
    }
}

internal sealed record VideoStreamResult(string Id, string Label, string PlaylistUri);

internal sealed record VideoSegmentResult(string ContentType, byte[] Payload);

internal sealed class PluginVideoProviderPort(
    PluginGrpcEndpoint endpoint,
    IOptions<PluginHostOptions> options,
    ILogger<PluginVideoProviderPort> logger)
{
    private readonly PluginGrpcEndpoint _endpoint = endpoint;
    private readonly IOptions<PluginHostOptions> _options = options;
    private readonly ILogger<PluginVideoProviderPort> _logger = logger;

    public async Task<IReadOnlyList<VideoStreamResult>> GetStreamsAsync(string mediaId, CancellationToken cancellationToken)
    {
        var (cts, deadlineUtc) = PluginGrpcHelpers.CreateCallTimeout(_options, cancellationToken);
        using var ctsScope = cts;
        using var lease = await PluginGrpcHelpers.AcquireLeaseAsync(
            _endpoint.Record.Manifest.Id,
            _options.Value,
            cts.Token);

        using var httpClient = PluginGrpcHelpers.CreateHttpClient(_endpoint.Address);
        using var channel = GrpcChannel.ForAddress(_endpoint.Address, new GrpcChannelOptions
        {
            HttpClient = httpClient
        });

        var client = new PluginContracts.VideoProvider.VideoProviderClient(channel);
        var response = await client.GetStreamsAsync(new PluginContracts.StreamRequest
        {
            MediaId = mediaId,
            Context = PluginGrpcHelpers.CreateRequestContext(_endpoint.CorrelationId, deadlineUtc)
        }, headers: PluginGrpcHelpers.CreateHeaders(_endpoint.CorrelationId), cancellationToken: cts.Token);

        var results = response.Streams
            .Select(stream => new VideoStreamResult(
                stream.Id ?? string.Empty,
                stream.Label ?? string.Empty,
                stream.PlaylistUri ?? string.Empty))
            .ToList();

        _logger.LogInformation(
            "Pipeline video streams {CorrelationId} pluginId={PluginId} mediaId={MediaId} count={Count}",
            _endpoint.CorrelationId,
            _endpoint.Record.Manifest.Id,
            mediaId,
            results.Count);

        return results;
    }

    public async Task<VideoSegmentResult> GetSegmentAsync(
        string mediaId,
        string streamId,
        int sequence,
        CancellationToken cancellationToken)
    {
        var (cts, deadlineUtc) = PluginGrpcHelpers.CreateCallTimeout(_options, cancellationToken);
        using var ctsScope = cts;
        using var lease = await PluginGrpcHelpers.AcquireLeaseAsync(
            _endpoint.Record.Manifest.Id,
            _options.Value,
            cts.Token);

        using var httpClient = PluginGrpcHelpers.CreateHttpClient(_endpoint.Address);
        using var channel = GrpcChannel.ForAddress(_endpoint.Address, new GrpcChannelOptions
        {
            HttpClient = httpClient
        });

        var client = new PluginContracts.VideoProvider.VideoProviderClient(channel);
        var response = await client.GetSegmentAsync(new PluginContracts.SegmentRequest
        {
            MediaId = mediaId,
            StreamId = streamId,
            Sequence = sequence,
            Context = PluginGrpcHelpers.CreateRequestContext(_endpoint.CorrelationId, deadlineUtc)
        }, headers: PluginGrpcHelpers.CreateHeaders(_endpoint.CorrelationId), cancellationToken: cts.Token);

        var payload = response.Payload.ToByteArray();
        _logger.LogInformation(
            "Pipeline video segment {CorrelationId} pluginId={PluginId} mediaId={MediaId} streamId={StreamId} sequence={Sequence} bytes={Size}",
            _endpoint.CorrelationId,
            _endpoint.Record.Manifest.Id,
            mediaId,
            streamId,
            sequence,
            payload.Length);

        return new VideoSegmentResult(
            string.IsNullOrWhiteSpace(response.ContentType)
                ? "application/octet-stream"
                : response.ContentType,
            payload);
    }
}

internal static class PluginGrpcHelpers
{
    private const string CorrelationIdHeader = "x-correlation-id";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _pluginLimits =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task<IDisposable> AcquireLeaseAsync(
        string pluginId,
        PluginHostOptions options,
        CancellationToken cancellationToken)
    {
        var maxConcurrent = Math.Max(1, options.MaxConcurrentCallsPerPlugin);
        var semaphore = _pluginLimits.GetOrAdd(
            pluginId,
            _ => new SemaphoreSlim(maxConcurrent, maxConcurrent));

        await semaphore.WaitAsync(cancellationToken);
        return new CallLease(semaphore);
    }

    public static HttpClient CreateHttpClient(Uri address)
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true
        };

        return new HttpClient(handler)
        {
            BaseAddress = address,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    public static Metadata CreateHeaders(string correlationId) => new()
    {
        { CorrelationIdHeader, correlationId }
    };

    public static string CreateCorrelationId() => Guid.NewGuid().ToString("n");

    public static (CancellationTokenSource Cts, DateTimeOffset DeadlineUtc) CreateCallTimeout(
        IOptions<PluginHostOptions> options,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = Math.Max(1, options.Value.ProbeTimeoutSeconds);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        return (cts, deadlineUtc);
    }

    public static PluginContracts.RequestContext CreateRequestContext(
        string correlationId,
        DateTimeOffset deadlineUtc)
    {
        return new PluginContracts.RequestContext
        {
            CorrelationId = correlationId,
            DeadlineUtc = deadlineUtc.ToString("O")
        };
    }

    private sealed class CallLease(SemaphoreSlim semaphore) : IDisposable
    {
        private readonly SemaphoreSlim _semaphore = semaphore;

        public void Dispose()
        {
            _semaphore.Release();
        }
    }
}

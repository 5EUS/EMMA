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
        using var cts = PluginGrpcHelpers.CreateCallTimeout(_options, cancellationToken);
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
            Query = query ?? string.Empty
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

        return new MediaSummary(mediaId, source, title, mediaType);
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
        using var cts = PluginGrpcHelpers.CreateCallTimeout(_options, cancellationToken);
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
            MediaId = mediaId.Value
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
        using var cts = PluginGrpcHelpers.CreateCallTimeout(_options, cancellationToken);
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
            Index = pageIndex
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

    private static MediaChapter MapChapter(PluginContracts.MediaChapter chapter)
    {
        var chapterId = chapter.Id ?? string.Empty;
        var title = chapter.Title ?? string.Empty;
        return new MediaChapter(chapterId, chapter.Number, title);
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

    public static CancellationTokenSource CreateCallTimeout(
        IOptions<PluginHostOptions> options,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = Math.Max(1, options.Value.ProbeTimeoutSeconds);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        return cts;
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

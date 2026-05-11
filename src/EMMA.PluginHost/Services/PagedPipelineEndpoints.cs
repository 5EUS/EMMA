using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using EMMA.Application.Pipelines;
using EMMA.Application.Ports;
using PluginContracts = EMMA.Contracts.Plugins;
using EMMA.Domain;
using EMMA.Infrastructure.InMemory;
using EMMA.Infrastructure.Policy;
using EMMA.Plugin.Common;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Services;

/// <summary>
/// Paged media pipeline endpoints backed by plugin gRPC ports.
/// </summary>
public static class PagedPipelineEndpoints
{
    private static readonly ConcurrentDictionary<string, ICachePort> _metadataCaches = new(StringComparer.OrdinalIgnoreCase);

    public static WebApplication MapPagedPipelineEndpoints(this WebApplication app)
    {
        app.MapGet("/pipeline/paged/search", async (
            string? query,
            string? pluginId,
            PluginResolutionService pluginResolution,
            IWasmPluginRuntimeHost wasmRuntimeHost,
            PluginProcessManager processManager,
            IOptions<PluginHostOptions> options,
            IMediaCatalogPort catalog,
            IPageAssetCachePort pageAssetCache,
            IPageAssetFetcherPort pageAssetFetcher,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var (Record, Address, IsWasm, Error) = await ResolvePluginAsync(pluginId, pluginResolution, wasmRuntimeHost, cancellationToken);
            if (Error is not null)
            {
                return Error;
            }

            var record = Record!;

            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);

            IReadOnlyList<MediaSummary> results;
            if (IsWasm)
            {
                var wasmLogger = loggerFactory.CreateLogger("WasmSearchEndpoint");
                var timeoutSeconds = Math.Max(1, options.Value.WasmOperationTimeoutSeconds);
                var wasmTimeout = TimeSpan.FromSeconds(timeoutSeconds);
                using var wasmTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wasmTimeoutCts.CancelAfter(wasmTimeout);

                try
                {
                    var searchTask = wasmRuntimeHost.SearchAsync(record, query ?? string.Empty, wasmTimeoutCts.Token);
                    var completed = await Task.WhenAny(searchTask, Task.Delay(wasmTimeout, cancellationToken));
                    if (completed != searchTask)
                    {
                        return Results.Problem(
                            detail: $"WASM search timed out after {timeoutSeconds}s.",
                            statusCode: StatusCodes.Status504GatewayTimeout);
                    }

                    results = await searchTask;
                }
                catch (TimeoutException ex)
                {
                    return PipelineErrorContract.ToResult(ex, "paged.search");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return PipelineErrorContract.ToResult(
                        new TimeoutException($"WASM search timed out after {timeoutSeconds}s."),
                        "paged.search");
                }
                catch (Exception ex)
                {
                    return PipelineErrorContract.ToResult(ex, "paged.search");
                }
            }
            else
            {
                var correlationId = PluginGrpcHelpers.CreateCorrelationId();
                var pipeline = CreatePipeline(
                    record,
                    Address!,
                    options,
                    catalog,
                    pageAssetCache,
                    pageAssetFetcher,
                    loggerFactory,
                    correlationId);
                results = await pipeline.SearchAsync(query ?? string.Empty, cancellationToken);
            }

            return Results.Ok(results.Select(result => new
            {
                Id = result.Id.ToString(),
                Source = result.SourceId,
                result.Title,
                MediaType = result.MediaType.ToString().ToLowerInvariant(),
                result.ThumbnailUrl,
                result.Description
            }));
        });

        app.MapPost("/pipeline/paged/search/suggestions", async (
            string? pluginId,
            HttpRequest request,
            PluginResolutionService pluginResolution,
            IWasmPluginRuntimeHost wasmRuntimeHost,
            PluginProcessManager processManager,
            CancellationToken cancellationToken) =>
        {
            var (Record, Address, IsWasm, Error) = await ResolvePluginAsync(pluginId, pluginResolution, wasmRuntimeHost, cancellationToken);
            if (Error is not null)
            {
                return Error;
            }

            var record = Record!;
            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);

            try
            {
                using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
                var suggestionRequest = ParseSearchSuggestionsRequest(document.RootElement);

                IReadOnlyList<SearchSuggestionItem> suggestions;
                if (IsWasm)
                {
                    suggestions = await wasmRuntimeHost.GetSearchSuggestionsAsync(
                        record,
                        suggestionRequest,
                        cancellationToken);
                }
                else
                {
                    suggestions = await GetGrpcPluginSearchSuggestionsAsync(
                        record.Manifest.Id,
                        Address!,
                        processManager,
                        suggestionRequest,
                        cancellationToken);
                }

                return Results.Ok(suggestions.Select(static suggestion => new
                {
                    suggestion.Value,
                    suggestion.Label,
                    suggestion.Description
                }));
            }
            catch (Exception ex)
            {
                return PipelineErrorContract.ToResult(ex, "paged.search-suggestions");
            }
        });

        app.MapGet("/pipeline/paged/chapters", async (
            string? mediaId,
            string? pluginId,
            PluginResolutionService pluginResolution,
            IWasmPluginRuntimeHost wasmRuntimeHost,
            PluginProcessManager processManager,
            IOptions<PluginHostOptions> options,
            IMediaCatalogPort catalog,
            IPageAssetCachePort pageAssetCache,
            IPageAssetFetcherPort pageAssetFetcher,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                return Results.BadRequest(new { message = "mediaId is required." });
            }

            var (Record, Address, IsWasm, Error) = await ResolvePluginAsync(pluginId, pluginResolution, wasmRuntimeHost, cancellationToken);
            if (Error is not null)
            {
                return Error;
            }

            var record = Record!;

            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);
            try
            {
                IReadOnlyList<MediaChapter> chapters;
                if (IsWasm)
                {
                    chapters = await wasmRuntimeHost.GetChaptersAsync(record, MediaId.Create(mediaId), cancellationToken);
                }
                else
                {
                    var correlationId = PluginGrpcHelpers.CreateCorrelationId();
                    var pipeline = CreatePipeline(
                        record,
                        Address!,
                        options,
                        catalog,
                        pageAssetCache,
                        pageAssetFetcher,
                        loggerFactory,
                        correlationId);
                    chapters = await pipeline.GetChaptersAsync(MediaId.Create(mediaId), cancellationToken);
                }

                return Results.Ok(chapters.Select(chapter => new
                {
                    Id = chapter.ChapterId,
                    chapter.Number,
                    chapter.Title,
                    UploaderGroups = chapter.UploaderGroups ?? []
                }));
            }
            catch (Exception ex)
            {
                return PipelineErrorContract.ToResult(ex, "paged.chapters");
            }
        });

        app.MapPost("/pipeline/paged/search/enrich", async (
            string? pluginId,
            HttpRequest request,
            PluginResolutionService pluginResolution,
            IWasmPluginRuntimeHost wasmRuntimeHost,
            PluginProcessManager processManager,
            CancellationToken cancellationToken) =>
        {
            var (Record, Address, IsWasm, Error) = await ResolvePluginAsync(pluginId, pluginResolution, wasmRuntimeHost, cancellationToken);
            if (Error is not null)
            {
                return Error;
            }

            var record = Record!;
            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);

            try
            {
                using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
                var media = ParseEnrichMediaRequest(document.RootElement);

                MediaSummary resolved;
                if (IsWasm)
                {
                    var enriched = await wasmRuntimeHost.EnrichSearchMetadataAsync(
                        record,
                        [media.Id.Value],
                        [media],
                        cancellationToken);
                    resolved = enriched.FirstOrDefault() ?? media;
                }
                else
                {
                    resolved = await EnrichGrpcPluginSearchMediaAsync(
                            record.Manifest.Id,
                            Address!,
                            processManager,
                            media,
                            cancellationToken)
                        ?? media;
                }

                return Results.Ok(new
                {
                    Id = resolved.Id.ToString(),
                    SourceId = resolved.SourceId,
                    Source = resolved.SourceId,
                    resolved.Title,
                    MediaType = resolved.MediaType.ToString().ToLowerInvariant(),
                    resolved.ThumbnailUrl,
                    resolved.Description,
                    Metadata = resolved.Metadata
                });
            }
            catch (Exception ex)
            {
                return PipelineErrorContract.ToResult(ex, "paged.search-enrich");
            }
        });

        app.MapGet("/pipeline/paged/page", async (
            string? mediaId,
            string? chapterId,
            int? index,
            string? pluginId,
            PluginResolutionService pluginResolution,
            IWasmPluginRuntimeHost wasmRuntimeHost,
            PluginProcessManager processManager,
            IOptions<PluginHostOptions> options,
            IMediaCatalogPort catalog,
            IPageAssetCachePort pageAssetCache,
            IPageAssetFetcherPort pageAssetFetcher,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId))
            {
                return Results.BadRequest(new { message = "mediaId and chapterId are required." });
            }

            var (Record, Address, IsWasm, Error) = await ResolvePluginAsync(pluginId, pluginResolution, wasmRuntimeHost, cancellationToken);
            if (Error is not null)
            {
                return Error;
            }

            var record = Record!;

            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);
            try
            {
                var page = await GetPageAsync(
                    record,
                    Address,
                    IsWasm,
                    wasmRuntimeHost,
                    options,
                    catalog,
                    pageAssetCache,
                    pageAssetFetcher,
                    loggerFactory,
                    MediaId.Create(mediaId),
                    chapterId,
                    index ?? 0,
                    cancellationToken);

                return Results.Ok(new
                {
                    Id = page.PageId,
                    page.Index,
                    ContentUri = page.ContentUri.ToString()
                });
            }
            catch (Exception ex)
            {
                return PipelineErrorContract.ToResult(ex, "paged.page");
            }
        });

        app.MapGet("/pipeline/paged/pages", async (
            string? mediaId,
            string? chapterId,
            int? startIndex,
            int? count,
            string? pluginId,
            PluginResolutionService pluginResolution,
            IWasmPluginRuntimeHost wasmRuntimeHost,
            PluginProcessManager processManager,
            IOptions<PluginHostOptions> options,
            IMediaCatalogPort catalog,
            IPageAssetCachePort pageAssetCache,
            IPageAssetFetcherPort pageAssetFetcher,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId))
            {
                return Results.BadRequest(new { message = "mediaId and chapterId are required." });
            }

            var safeStartIndex = Math.Max(0, startIndex ?? 0);
            var safeCount = Math.Max(1, count ?? 1);

            var (Record, Address, IsWasm, Error) = await ResolvePluginAsync(pluginId, pluginResolution, wasmRuntimeHost, cancellationToken);
            if (Error is not null)
            {
                return Error;
            }

            var record = Record!;

            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);
            try
            {
                MediaPagesResult pages;
                if (IsWasm)
                {
                    pages = await wasmRuntimeHost.GetPagesAsync(
                        record,
                        MediaId.Create(mediaId),
                        chapterId,
                        safeStartIndex,
                        safeCount,
                        cancellationToken);
                }
                else
                {
                    var correlationId = PluginGrpcHelpers.CreateCorrelationId();
                    var pipeline = CreatePipeline(
                        record,
                        Address!,
                        options,
                        catalog,
                        pageAssetCache,
                        pageAssetFetcher,
                        loggerFactory,
                        correlationId);
                    pages = await pipeline.GetPagesAsync(
                        MediaId.Create(mediaId),
                        chapterId,
                        safeStartIndex,
                        safeCount,
                        cancellationToken);
                }

                return Results.Ok(new
                {
                    Pages = pages.Pages.Select(page => new
                    {
                        Id = page.PageId,
                        page.Index,
                        ContentUri = page.ContentUri.ToString()
                    }),
                    pages.ReachedEnd
                });
            }
            catch (Exception ex)
            {
                return PipelineErrorContract.ToResult(ex, "paged.pages");
            }
        });

        app.MapGet("/pipeline/paged/page-asset", async (
            string? mediaId,
            string? chapterId,
            int? index,
            string? pluginId,
            PluginResolutionService pluginResolution,
            IWasmPluginRuntimeHost wasmRuntimeHost,
            PluginProcessManager processManager,
            IOptions<PluginHostOptions> options,
            IMediaCatalogPort catalog,
            IPageAssetCachePort pageAssetCache,
            IPageAssetFetcherPort pageAssetFetcher,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId))
            {
                return Results.BadRequest(new { message = "mediaId and chapterId are required." });
            }

            var (Record, Address, IsWasm, Error) = await ResolvePluginAsync(pluginId, pluginResolution, wasmRuntimeHost, cancellationToken);
            if (Error is not null)
            {
                return Error;
            }

            var record = Record!;

            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);
            try
            {
                var page = await GetPageAsync(
                    record,
                    Address,
                    IsWasm,
                    wasmRuntimeHost,
                    options,
                    catalog,
                    pageAssetCache,
                    pageAssetFetcher,
                    loggerFactory,
                    MediaId.Create(mediaId),
                    chapterId,
                    index ?? 0,
                    cancellationToken);

                var cacheKey = $"{record.Manifest.Id}:{page.PageId}:{page.ContentUri}";
                var asset = await pageAssetCache.GetAsync(cacheKey, cancellationToken);
                if (asset is null)
                {
                    asset = await pageAssetFetcher.FetchAsync(page.ContentUri, cancellationToken);
                    await pageAssetCache.SetAsync(cacheKey, asset, cancellationToken);
                }

                return Results.File(asset.Payload, asset.ContentType);
            }
            catch (Exception ex)
            {
                return PipelineErrorContract.ToResult(ex, "paged.page-asset");
            }
        });

        return app;
    }

    private static MediaSummary ParseEnrichMediaRequest(JsonElement root)
    {
        var mediaId = ReadJsonString(root, "id") ?? ReadJsonString(root, "mediaId");
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            throw new InvalidOperationException("Invalid enrich request: media id is required.");
        }

        var sourceId = ReadJsonString(root, "sourceId") ?? ReadJsonString(root, "source") ?? string.Empty;
        var title = ReadJsonString(root, "title") ?? string.Empty;
        var mediaType = ReadJsonString(root, "mediaType");
        var thumbnailUrl = ReadJsonString(root, "thumbnailUrl") ?? ReadJsonString(root, "thumbnail_url");
        var description = ReadJsonString(root, "description");
        var metadata = ReadJsonMetadata(root, "metadata") ?? ReadJsonMetadata(root, "attributes");

        return new MediaSummary(
            MediaId.Create(mediaId),
            sourceId,
            title,
            ParseMediaType(mediaType),
            thumbnailUrl,
            description,
            metadata);
    }

    private static SearchSuggestionRequest ParseSearchSuggestionsRequest(JsonElement root)
    {
        var controlId = ReadJsonString(root, "controlId");
        if (string.IsNullOrWhiteSpace(controlId))
        {
            throw new InvalidOperationException("Invalid suggestions request: controlId is required.");
        }

        PluginSearchQuery? searchQuery = null;
        if (TryGetObjectProperty(root, "searchQuery", out var searchQueryElement))
        {
            searchQuery = PluginSearchQuery.Parse(searchQueryElement.GetRawText());
        }

        return new SearchSuggestionRequest(
            controlId,
            ReadJsonString(root, "query") ?? string.Empty,
            searchQuery,
            ReadJsonInt32(root, "limit"));
    }

    private static async Task<MediaSummary?> EnrichGrpcPluginSearchMediaAsync(
        string pluginId,
        Uri address,
        PluginProcessManager processManager,
        MediaSummary media,
        CancellationToken cancellationToken)
    {
        var correlationId = PluginGrpcHelpers.CreateCorrelationId();
        using var httpClient = PluginGrpcHelpers.CreateHttpClient(address);
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpClient = httpClient
        });
        var client = new PluginContracts.SearchProvider.SearchProviderClient(channel);
        var headers = PluginGrpcHelpers.CreateHeaders(correlationId);
        var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30);

        var token = processManager.GetHostAuthToken(pluginId);
        if (!string.IsNullOrWhiteSpace(token))
        {
            headers.Add("x-emma-plugin-host-auth", token);
        }

        var request = new PluginContracts.EnrichSearchItemsRequest
        {
            Context = PluginGrpcHelpers.CreateRequestContext(correlationId, deadlineUtc)
        };
        request.Items.Add(MapPluginSearchSummaryContract(media));

        var response = await client.EnrichSearchItemsAsync(request, headers: headers, cancellationToken: cancellationToken);
        var enriched = response.Results.FirstOrDefault();
        return enriched is null ? media : MapPluginSearchSummary(enriched);
    }

    private static async Task<IReadOnlyList<SearchSuggestionItem>> GetGrpcPluginSearchSuggestionsAsync(
        string pluginId,
        Uri address,
        PluginProcessManager processManager,
        SearchSuggestionRequest request,
        CancellationToken cancellationToken)
    {
        var correlationId = PluginGrpcHelpers.CreateCorrelationId();
        using var httpClient = PluginGrpcHelpers.CreateHttpClient(address);
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpClient = httpClient
        });
        var client = new PluginContracts.SearchProvider.SearchProviderClient(channel);
        var headers = PluginGrpcHelpers.CreateHeaders(correlationId);
        var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30);

        var token = processManager.GetHostAuthToken(pluginId);
        if (!string.IsNullOrWhiteSpace(token))
        {
            headers.Add("x-emma-plugin-host-auth", token);
        }

        var grpcRequest = new PluginContracts.SearchSuggestionsRequest
        {
            ControlId = request.ControlId,
            Query = request.Query,
            Limit = request.Limit ?? 0,
            Context = PluginGrpcHelpers.CreateRequestContext(correlationId, deadlineUtc)
        };

        if (request.SearchQuery is { } searchQuery)
        {
            grpcRequest.Search = BuildGrpcSearchRequest(searchQuery, correlationId, deadlineUtc);
        }

        var response = await client.SearchSuggestionsAsync(grpcRequest, headers: headers, cancellationToken: cancellationToken);
        return [.. response.Suggestions.Select(static suggestion => new SearchSuggestionItem(
            suggestion.Value ?? string.Empty,
            suggestion.Label ?? string.Empty,
            string.IsNullOrWhiteSpace(suggestion.Description) ? null : suggestion.Description))];
    }

    private static PluginContracts.SearchRequest BuildGrpcSearchRequest(
        PluginSearchQuery query,
        string correlationId,
        DateTimeOffset deadlineUtc)
    {
        var request = new PluginContracts.SearchRequest
        {
            Query = query.Query ?? string.Empty,
            Context = PluginGrpcHelpers.CreateRequestContext(correlationId, deadlineUtc)
        };

        if (query.MediaTypes.Count > 0)
        {
            request.MediaTypes.AddRange(query.MediaTypes);
        }

        foreach (var filter in query.Filters)
        {
            var grpcFilter = new PluginContracts.SearchFilter
            {
                Id = filter.Id,
                Operation = filter.Operation ?? string.Empty
            };
            grpcFilter.Values.AddRange(filter.Values);
            request.Filters.Add(grpcFilter);
        }

        foreach (var addition in query.QueryAdditions)
        {
            request.QueryAdditions.Add(new PluginContracts.SearchQueryAddition
            {
                Id = addition.Id,
                Value = addition.Value,
                Type = addition.Type ?? string.Empty
            });
        }

        if (!string.IsNullOrWhiteSpace(query.Sort))
        {
            request.Sort = query.Sort;
        }

        if (query.Page is int page && page >= 0)
        {
            request.Page = page;
        }

        if (query.PageSize is int pageSize && pageSize > 0)
        {
            request.PageSize = pageSize;
        }

        return request;
    }

    private static MediaSummary MapPluginSearchSummary(PluginContracts.MediaSummary item)
    {
        IReadOnlyDictionary<string, string>? metadata = null;
        if (item.Metadata?.Count > 0)
        {
            var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in item.Metadata)
            {
                var key = entry.Key?.Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                mapped[key] = entry.Value?.Trim() ?? string.Empty;
            }

            if (mapped.Count > 0)
            {
                metadata = mapped;
            }
        }

        return new MediaSummary(
            MediaId.Create(item.Id ?? string.Empty),
            item.Source ?? string.Empty,
            item.Title ?? string.Empty,
            ParseMediaType(item.MediaType),
            string.IsNullOrWhiteSpace(item.ThumbnailUrl) ? null : item.ThumbnailUrl,
            string.IsNullOrWhiteSpace(item.Description) ? null : item.Description,
            metadata);
    }

    private static PluginContracts.MediaSummary MapPluginSearchSummaryContract(MediaSummary media)
    {
        var result = new PluginContracts.MediaSummary
        {
            Id = media.Id.Value,
            Source = media.SourceId,
            Title = media.Title,
            MediaType = media.MediaType.ToString().ToLowerInvariant(),
            ThumbnailUrl = media.ThumbnailUrl ?? string.Empty,
            Description = media.Description ?? string.Empty
        };

        if (media.Metadata is { Count: > 0 })
        {
            result.Metadata.AddRange(media.Metadata.Select(static entry => new PluginContracts.KeyValue
            {
                Key = entry.Key,
                Value = entry.Value
            }));
        }

        return result;
    }

    private static string? ReadJsonString(JsonElement element, string propertyName)
    {
        if (!TryGetJsonProperty(element, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.ToString(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static int? ReadJsonInt32(JsonElement element, string propertyName)
    {
        if (!TryGetJsonProperty(element, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(property.GetString(), out var number) => number,
            _ => null
        };
    }

    private static IReadOnlyDictionary<string, string>? ReadJsonMetadata(JsonElement element, string propertyName)
    {
        if (!TryGetJsonProperty(element, propertyName, out var property))
        {
            return null;
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (property.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in property.EnumerateObject())
            {
                var key = entry.Name.Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                metadata[key] = entry.Value.ValueKind switch
                {
                    JsonValueKind.String => entry.Value.GetString() ?? string.Empty,
                    JsonValueKind.Number => entry.Value.ToString(),
                    JsonValueKind.True => bool.TrueString,
                    JsonValueKind.False => bool.FalseString,
                    JsonValueKind.Null => string.Empty,
                    _ => entry.Value.ToString()
                };
            }
        }
        else if (property.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in property.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var key = ReadJsonString(item, "key") ?? ReadJsonString(item, "name") ?? ReadJsonString(item, "label");
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                metadata[key.Trim()] = ReadJsonString(item, "value")
                    ?? ReadJsonString(item, "text")
                    ?? ReadJsonString(item, "data")
                    ?? string.Empty;
            }
        }

        return metadata.Count == 0 ? null : metadata;
    }

    private static bool TryGetObjectProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (!TryGetJsonProperty(element, propertyName, out value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetJsonProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate.Value;
                return true;
            }
        }

        value = default;
        return false;
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

    private static PagedMediaPipeline CreatePipeline(
        PluginRecord record,
        Uri address,
        IOptions<PluginHostOptions> options,
        IMediaCatalogPort catalog,
        IPageAssetCachePort pageAssetCache,
        IPageAssetFetcherPort pageAssetFetcher,
        ILoggerFactory loggerFactory,
        string correlationId)
    {
        var endpoint = new PluginGrpcEndpoint(record, address, correlationId);
        var searchPort = new PluginSearchPort(
            endpoint,
            options,
            loggerFactory.CreateLogger<PluginSearchPort>());
        var pagePort = new PluginPageProviderPort(
            endpoint,
            options,
            loggerFactory.CreateLogger<PluginPageProviderPort>());

        var timeoutSeconds = Math.Max(1, options.Value.ProbeTimeoutSeconds);
        var pipelineOptions = new PagedMediaPipelineOptions(
            TimeSpan.FromSeconds(timeoutSeconds),
            1,
            TimeSpan.FromMilliseconds(200));

        var cache = _metadataCaches.GetOrAdd(record.Manifest.Id, _ => new InMemoryCachePort());

        return new PagedMediaPipeline(
            searchPort,
            pagePort,
            new ManifestPolicyEvaluator(ManifestPolicyMapping.ToDefinition(record.Manifest)),
            cache,
            pipelineOptions,
            pageAssetCache,
            pageAssetFetcher,
            catalog);
    }

    private static async ValueTask<MediaPage> GetPageAsync(
        PluginRecord record,
        Uri? address,
        bool isWasm,
        IWasmPluginRuntimeHost wasmRuntimeHost,
        IOptions<PluginHostOptions> options,
        IMediaCatalogPort catalog,
        IPageAssetCachePort pageAssetCache,
        IPageAssetFetcherPort pageAssetFetcher,
        ILoggerFactory loggerFactory,
        MediaId mediaId,
        string chapterId,
        int index,
        CancellationToken cancellationToken)
    {
        if (isWasm)
        {
            return await wasmRuntimeHost.GetPageAsync(
                record,
                mediaId,
                chapterId,
                index,
                cancellationToken);
        }

        var correlationId = PluginGrpcHelpers.CreateCorrelationId();
        var pipeline = CreatePipeline(
            record,
            address!,
            options,
            catalog,
            pageAssetCache,
            pageAssetFetcher,
            loggerFactory,
            correlationId);
        return await pipeline.GetPageAsync(mediaId, chapterId, index, cancellationToken);
    }

    private static async ValueTask<(PluginRecord? Record, Uri? Address, bool IsWasm, IResult? Error)> ResolvePluginAsync(
        string? pluginId,
        PluginResolutionService pluginResolution,
        IWasmPluginRuntimeHost wasmRuntimeHost,
        CancellationToken cancellationToken)
    {
        var (record, address, error) = await pluginResolution.ResolveAsync(pluginId, cancellationToken);
        if (error is not null || record is null)
        {
            var result = error is null
                ? Results.Problem("Plugin resolution failed.")
                : Results.Problem(detail: error.Message, statusCode: error.StatusCode);

            return (null, null, false, result);
        }

        var isWasm = wasmRuntimeHost.IsWasmPlugin(record.Manifest);
        if (!isWasm && address is null)
        {
            return (null, null, false, Results.Problem("Plugin resolution failed."));
        }

        return (record, address, isWasm, null);
    }

}

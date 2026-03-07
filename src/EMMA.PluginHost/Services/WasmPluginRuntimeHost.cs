using System.Collections.Concurrent;
using System.Text.Json;
using EMMA.Domain;
using EMMA.PluginHost.Plugins;

namespace EMMA.PluginHost.Services;

public interface IWasmPluginRuntimeHost
{
    bool IsWasmPlugin(PluginManifest manifest);
    Task<PluginHandshakeStatus> HandshakeAsync(PluginManifest manifest, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaSummary>> SearchAsync(PluginRecord record, string query, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(PluginRecord record, MediaId mediaId, CancellationToken cancellationToken);
    Task<MediaPage> GetPageAsync(PluginRecord record, MediaId mediaId, string chapterId, int pageIndex, CancellationToken cancellationToken);
    Task<MediaPagesResult> GetPagesAsync(PluginRecord record, MediaId mediaId, string chapterId, int startIndex, int count, CancellationToken cancellationToken);
}

public interface IWasmComponentInvoker
{
    Task<string> InvokeAsync(
        string componentPath,
        string operation,
        IReadOnlyList<string> operationArgs,
        CancellationToken cancellationToken);
}

public sealed class WasmPluginRuntimeHost(
    IPluginEntrypointResolver entrypointResolver,
    IWasmComponentInvoker invoker,
    ILogger<WasmPluginRuntimeHost> logger) : IWasmPluginRuntimeHost
{
    private const string HandshakeOperation = "handshake";
    private const string CapabilitiesOperation = "capabilities";
    private const string SearchOperation = "search";
    private const string ChaptersOperation = "chapters";
    private const string PageOperation = "page";

    private readonly IPluginEntrypointResolver _entrypointResolver = entrypointResolver;
    private readonly IWasmComponentInvoker _invoker = invoker;
    private readonly ILogger<WasmPluginRuntimeHost> _logger = logger;
    private readonly ConcurrentDictionary<string, BridgePayloadCacheEntry> _bridgePayloadCache = new();

    private static readonly TimeSpan BridgePayloadCacheTtl = TimeSpan.FromMinutes(2);

    private readonly record struct BridgePayloadCacheEntry(string Payload, DateTimeOffset FetchedAtUtc);

    public bool IsWasmPlugin(PluginManifest manifest)
    {
        return _entrypointResolver.TryResolveWasmComponent(manifest, out _);
    }

    public async Task<PluginHandshakeStatus> HandshakeAsync(PluginManifest manifest, CancellationToken cancellationToken)
    {
        if (!_entrypointResolver.TryResolveWasmComponent(manifest, out var componentPath))
        {
            return new PluginHandshakeStatus(
                false,
                "WASM component not found.",
                null,
                DateTimeOffset.UtcNow,
                [],
                0,
                0,
                [],
                []);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var handshakeJson = await RunComponentAsync(componentPath, HandshakeOperation, [], cancellationToken);
        var health = DeserializeJson<WasmHealth>(handshakeJson);
        var capabilitiesJson = await RunComponentAsync(componentPath, CapabilitiesOperation, [], cancellationToken);
        var capabilities = DeserializeJson<IReadOnlyList<string>>(capabilitiesJson) ?? [];

        if (health is null)
        {
            return new PluginHandshakeStatus(
                false,
                "WASM component handshake response is invalid.",
                null,
                DateTimeOffset.UtcNow,
                [],
                0,
                0,
                [],
                []);
        }

        var message = string.IsNullOrWhiteSpace(health.Message)
            ? "WASM component runtime ready"
            : health.Message;

        return new PluginHandshakeStatus(
            true,
            message,
            health.Version,
            DateTimeOffset.UtcNow,
            capabilities,
            manifest.Capabilities?.CpuBudgetMs ?? 0,
            manifest.Capabilities?.MemoryMb ?? 0,
            manifest.Permissions?.Domains?.ToArray() ?? [],
            manifest.Permissions?.Paths?.ToArray() ?? []);
    }

    public async Task<IReadOnlyList<MediaSummary>> SearchAsync(
        PluginRecord record,
        string query,
        CancellationToken cancellationToken)
    {
        var componentPath = ResolveComponentPath(record.Manifest);
        var args = new List<string>
        {
            query ?? string.Empty
        };

        args = await EnrichOperationArgsAsync(record.Manifest, componentPath, SearchOperation, args, cancellationToken);

        var searchJson = await RunComponentAsync(componentPath, SearchOperation, args, cancellationToken);

        var searchItems = DeserializeJson<IReadOnlyList<WasmSearchItem>>(searchJson);
        if (searchItems is null)
        {
            var truncated = searchJson?.Length > 500 ? string.Concat(searchJson.AsSpan(0, 500), "...") : searchJson;
            throw new InvalidOperationException($"Failed to deserialize WASM search response. Raw response: {truncated}");
        }

        if (searchItems.Count == 0)
        {
            var truncated = searchJson?.Length > 500 ? string.Concat(searchJson.AsSpan(0, 500), "...") : searchJson;
            throw new InvalidOperationException(
                $"WASM search returned no results for query '{query}'. Raw response: {truncated}");
        }

        return [.. searchItems.Select(item => new MediaSummary(
            MediaId.Create(item.Id),
            item.Source ?? record.Manifest.Id,
            item.Title,
            string.Equals(item.MediaType, "video", StringComparison.OrdinalIgnoreCase)
                ? MediaType.Video
                : MediaType.Paged,
            string.IsNullOrWhiteSpace(item.ThumbnailUrl) ? null : item.ThumbnailUrl,
            string.IsNullOrWhiteSpace(item.Description) ? null : item.Description))];
    }

    public async Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(
        PluginRecord record,
        MediaId mediaId,
        CancellationToken cancellationToken)
    {
        var componentPath = ResolveComponentPath(record.Manifest);
        var args = new List<string>
        {
            mediaId.Value
        };

        args = await EnrichOperationArgsAsync(record.Manifest, componentPath, ChaptersOperation, args, cancellationToken);

        var chaptersJson = await RunComponentAsync(componentPath, ChaptersOperation, args, cancellationToken);
        var chapters = DeserializeJson<IReadOnlyList<WasmChapterItem>>(chaptersJson);
        if (chapters is null || chapters.Count == 0)
        {
            return [];
        }

        return [.. chapters.Select(chapter => new MediaChapter(chapter.Id, chapter.Number, chapter.Title))];
    }

    public async Task<MediaPage> GetPageAsync(
        PluginRecord record,
        MediaId mediaId,
        string chapterId,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var componentPath = ResolveComponentPath(record.Manifest);
        var args = new List<string>
        {
            mediaId.Value,
            chapterId,
            pageIndex.ToString()
        };

        args = await EnrichOperationArgsAsync(record.Manifest, componentPath, PageOperation, args, cancellationToken);

        var pageJson = await RunComponentAsync(
            componentPath,
            PageOperation,
            args,
            cancellationToken);

        var page = DeserializeJson<WasmPageItem>(pageJson);
        if (page is null || !Uri.TryCreate(page.ContentUri, UriKind.Absolute, out var contentUri))
        {
            throw new KeyNotFoundException($"PAGE_NOT_FOUND:{chapterId}:{pageIndex}");
        }

        return new MediaPage(page.Id, page.Index, contentUri);
    }

    public async Task<MediaPagesResult> GetPagesAsync(
        PluginRecord record,
        MediaId mediaId,
        string chapterId,
        int startIndex,
        int count,
        CancellationToken cancellationToken)
    {
        if (startIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        }

        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var pages = new List<MediaPage>(count);
        var reachedEnd = false;

        for (var offset = 0; offset < count; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageIndex = startIndex + offset;

            try
            {
                var page = await GetPageAsync(record, mediaId, chapterId, pageIndex, cancellationToken);
                pages.Add(page);
            }
            catch (KeyNotFoundException ex) when (ex.Message.StartsWith("PAGE_NOT_FOUND:", StringComparison.Ordinal))
            {
                reachedEnd = true;
                break;
            }
        }

        return new MediaPagesResult(pages, reachedEnd);
    }

    private string ResolveComponentPath(PluginManifest manifest)
    {
        if (_entrypointResolver.TryResolveWasmComponent(manifest, out var componentPath))
        {
            return componentPath;
        }

        throw new InvalidOperationException($"WASM component not found for plugin '{manifest.Id}'.");
    }

    private async Task<string> RunComponentAsync(
        string componentPath,
        string operation,
        IReadOnlyList<string> operationArgs,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _invoker.InvokeAsync(componentPath, operation, operationArgs, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WASM operation {Operation} failed for {ComponentPath}", operation, componentPath);
            throw;
        }
    }

    private T? DeserializeJson<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        if (TryDeserialize(json, out T? parsed))
        {
            return parsed;
        }

        var lines = json
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Reverse();

        foreach (var line in lines)
        {
            if (!LooksLikeJson(line))
            {
                continue;
            }

            if (TryDeserialize(line, out parsed))
            {
                return parsed;
            }
        }

        return default;
    }

    private bool TryDeserialize<T>(string json, out T? value)
    {
        try
        {
            // Use the context's GetTypeInfo to avoid reflection
            var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>?)WasmResponseJsonContext.Default.GetTypeInfo(typeof(T));
            if (typeInfo == null)
            {
                _logger.LogWarning("No JSON type info for {Type} in WasmResponseJsonContext", typeof(T).Name);
                value = default;
                return false;
            }
            
            value = JsonSerializer.Deserialize(json, typeInfo);
            return true;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON deserialization failed for type {Type}: {Message}", typeof(T).Name, ex.Message);
            value = default;
            return false;
        }
    }

    private static bool LooksLikeJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[') || trimmed.StartsWith('"');
    }

    private async Task<List<string>> EnrichOperationArgsAsync(
        PluginManifest manifest,
        string componentPath,
        string operation,
        List<string> operationArgs,
        CancellationToken cancellationToken)
    {
        if (!TryResolveHttpBridgeOperation(manifest, operation, out var bridgeOperation)
            || bridgeOperation is null)
        {
            return operationArgs;
        }

        var payload = await FetchBridgePayloadAsync(manifest, bridgeOperation, operationArgs, cancellationToken);

        var payloadArg = await WriteBridgePayloadAsync(componentPath, operation, payload, cancellationToken);
        operationArgs.Add(payloadArg);

        return operationArgs;
    }

    private static bool TryResolveHttpBridgeOperation(
        PluginManifest manifest,
        string operation,
        out PluginManifestWasmHttpOperation? bridgeOperation)
    {
        bridgeOperation = null;

        // TODO(dotnet-wasm-http): Remove host-supplied HTTP bridge once outbound HttpClient is supported for .NET WASM components.
        var operations = manifest.Runtime?.WasmHostBridge?.Http;
        if (operations is null || operations.Count == 0)
        {
            return false;
        }

        if (!operations.TryGetValue(operation, out bridgeOperation) || bridgeOperation is null)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(bridgeOperation.UrlTemplate);
    }

    private async Task<string> FetchBridgePayloadAsync(
        PluginManifest manifest,
        PluginManifestWasmHttpOperation bridgeOperation,
        IReadOnlyList<string> operationArgs,
        CancellationToken cancellationToken)
    {
        var method = string.IsNullOrWhiteSpace(bridgeOperation.Method)
            ? HttpMethod.Get
            : new HttpMethod(bridgeOperation.Method.Trim().ToUpperInvariant());

        if (method != HttpMethod.Get)
        {
            throw new InvalidOperationException(
                $"Unsupported WASM host bridge HTTP method '{method}'. Only GET is currently supported.");
        }

        var resolvedUrl = ResolveUrlTemplate(bridgeOperation.UrlTemplate, operationArgs);
        var requestUri = ResolveBridgeRequestUri(manifest, resolvedUrl);
        var baseAddress = new Uri(requestUri.GetLeftPart(UriPartial.Authority));
        var requestTarget = requestUri.PathAndQuery + requestUri.Fragment;
        var cacheKey = $"{method}:{requestUri}";

        if (method == HttpMethod.Get)
        {
            var now = DateTimeOffset.UtcNow;
            if (_bridgePayloadCache.TryGetValue(cacheKey, out var cached)
                && now - cached.FetchedAtUtc <= BridgePayloadCacheTtl)
            {
                return cached.Payload;
            }
        }

        EnsureHostIsAllowed(manifest, requestUri);

        using var client = CreateHostHttpClient(baseAddress);
        for (var attempt = 1; attempt <= 3; attempt++) // TODO custom number of retries
        {
            using var request = new HttpRequestMessage(method, requestTarget);
            try
            {
                using var response = await client.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (method == HttpMethod.Get && !string.IsNullOrWhiteSpace(payload))
                    {
                        _bridgePayloadCache[cacheKey] = new BridgePayloadCacheEntry(
                            payload,
                            DateTimeOffset.UtcNow);
                    }

                    return payload;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var statusCode = (int)response.StatusCode;
                var transient = IsTransientStatusCode(statusCode);

                if (transient && attempt < 3)
                {
                    var retryDelay = ResolveRetryDelay(response, attempt);
                    _logger.LogWarning(
                        "WASM host bridge transient HTTP failure {StatusCode} {ReasonPhrase} for {RequestUri}, retrying in {DelayMs}ms (attempt {Attempt}/3)",
                        statusCode,
                        response.ReasonPhrase,
                        requestUri,
                        (int)retryDelay.TotalMilliseconds,
                        attempt);

                    await Task.Delay(retryDelay, cancellationToken);
                    continue;
                }

                var bodyPreview = BuildResponsePreview(body);
                if (transient)
                {
                    throw new InvalidOperationException(
                        $"WASM host bridge upstream is unavailable ({statusCode} {response.ReasonPhrase}) at '{requestUri}'. Try again later. Provider response: {bodyPreview}");
                }

                throw new InvalidOperationException(
                    $"WASM host bridge HTTP request failed: {statusCode} {response.ReasonPhrase} at '{requestUri}'. Response: {bodyPreview}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (attempt < 3)
            {
                var retryDelay = TimeSpan.FromMilliseconds(250 * attempt);
                _logger.LogWarning(
                    ex,
                    "WASM host bridge HTTP transport failure for {RequestUri}, retrying in {DelayMs}ms (attempt {Attempt}/3)",
                    requestUri,
                    (int)retryDelay.TotalMilliseconds,
                    attempt);

                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(
                    $"WASM host bridge HTTP transport error for '{requestUri}': {ex.Message}",
                    ex);
            }
        }

        throw new InvalidOperationException(
            $"WASM host bridge HTTP request failed for '{requestUri}' after retries.");
    }

    private static bool IsTransientStatusCode(int statusCode)
    {
        return statusCode is 429 or 502 or 503 or 504;
    }

    private static TimeSpan ResolveRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        return TimeSpan.FromMilliseconds(300 * attempt);
    }

    private static string BuildResponsePreview(string body)
    {
        var normalized = body
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "<empty>";
        }

        return normalized.Length > 180
            ? normalized[..180] + "..."
            : normalized;
    }

    private static Uri ResolveBridgeRequestUri(PluginManifest manifest, string resolvedUrl)
    {
        if (Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }

        if (!Uri.TryCreate(resolvedUrl, UriKind.Relative, out var relative))
        {
            throw new InvalidOperationException(
                $"WASM host bridge URL template resolved to invalid URI: '{resolvedUrl}'.");
        }

        var baseAddress = ResolveBridgeBaseAddress(manifest);
        return new Uri(baseAddress, relative);
    }

    private static Uri ResolveBridgeBaseAddress(PluginManifest manifest)
    {
        var operations = manifest.Runtime?.WasmHostBridge?.Http;
        if (operations is not null)
        {
            foreach (var operation in operations.Values)
            {
                var template = operation?.UrlTemplate;
                if (string.IsNullOrWhiteSpace(template))
                {
                    continue;
                }

                if (!Uri.TryCreate(template, UriKind.Absolute, out var absolute))
                {
                    continue;
                }

                return new Uri(absolute.GetLeftPart(UriPartial.Authority));
            }
        }

        var firstDomain = manifest.Permissions?.Domains?
            .FirstOrDefault(domain => !string.IsNullOrWhiteSpace(domain) && domain != "*")
            ?.Trim();

        if (!string.IsNullOrWhiteSpace(firstDomain)
            && Uri.TryCreate($"https://{firstDomain}", UriKind.Absolute, out var inferred))
        {
            return inferred;
        }

        throw new InvalidOperationException(
            "WASM host bridge URL template is relative, but no absolute base address could be resolved from runtime.wasmHostBridge.http or permissions.domains.");
    }

    private async Task<string> WriteBridgePayloadAsync(
        string componentPath,
        string operation,
        string payload,
        CancellationToken cancellationToken)
    {
        // Use a writable temp directory instead of the component directory
        // (which may be inside a read-only app bundle on macOS)
        var componentHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(componentPath)))
            .ToLowerInvariant()[..16];
        
        var tempRoot = Path.Combine(Path.GetTempPath(), "emma-wasm-bridge");
        var bridgeDir = Path.Combine(tempRoot, componentHash, ".hostbridge");
        Directory.CreateDirectory(bridgeDir);

        var fileName = $"{operation}-{Guid.NewGuid():N}.json";
        var filePath = Path.Combine(bridgeDir, fileName);
        await File.WriteAllTextAsync(filePath, payload, cancellationToken);

        return $"/.hostbridge/{fileName}";
    }

    private static string ResolveUrlTemplate(string template, IReadOnlyList<string> operationArgs)
    {
        var resolved = template;
        for (var index = 0; index < operationArgs.Count; index++)
        {
            resolved = resolved.Replace(
                $"{{arg{index}}}",
                Uri.EscapeDataString(operationArgs[index] ?? string.Empty),
                StringComparison.OrdinalIgnoreCase);
        }

        return resolved;
    }

    private static void EnsureHostIsAllowed(PluginManifest manifest, Uri requestUri)
    {
        var allowedDomains = manifest.Permissions?.Domains;
        if (allowedDomains is null || allowedDomains.Count == 0)
        {
            return;
        }

        var host = requestUri.Host;
        var isAllowed = allowedDomains.Any(domain =>
            !string.IsNullOrWhiteSpace(domain)
            && (string.Equals(domain, "*", StringComparison.OrdinalIgnoreCase)
                || host.Equals(domain, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase)));

        if (!isAllowed)
        {
            throw new InvalidOperationException(
                $"WASM host bridge blocked request to '{requestUri.Host}'. Domain is not listed in plugin permissions.");
        }
    }

    private static HttpClient CreateHostHttpClient(Uri baseAddress)
    {
        var client = new HttpClient
        {
            BaseAddress = baseAddress
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("EMMA-PluginHost/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        return client;
    }
}

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
    private static readonly HttpClient HostHttpClient = CreateHostHttpClient();

    private readonly IPluginEntrypointResolver _entrypointResolver = entrypointResolver;
    private readonly IWasmComponentInvoker _invoker = invoker;
    private readonly ILogger<WasmPluginRuntimeHost> _logger = logger;

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
            var truncated = searchJson?.Length > 500 ? searchJson.Substring(0, 500) + "..." : searchJson;
            throw new InvalidOperationException($"Failed to deserialize WASM search response. Raw response: {truncated}");
        }
        
        if (searchItems.Count == 0)
        {
            // Throw to make debugging easier - show what the WASM component actually returned
            var truncated = searchJson?.Length > 500 ? searchJson.Substring(0, 500) + "..." : searchJson;
            throw new InvalidOperationException($"WASM component returned 0 results. Query: '{query}'. WASM output: {truncated}");
        }

        return [.. searchItems.Select(item => new MediaSummary(
            MediaId.Create(item.Id),
            item.Source ?? record.Manifest.Id,
            item.Title,
            string.Equals(item.MediaType, "video", StringComparison.OrdinalIgnoreCase)
                ? MediaType.Video
                : MediaType.Paged))];
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
            throw new KeyNotFoundException(
                $"WASM page {pageIndex} not found for chapter {chapterId} and media {mediaId.Value}.");
        }

        return new MediaPage(page.Id, page.Index, contentUri);
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
        if (!Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var requestUri))
        {
            throw new InvalidOperationException(
                $"WASM host bridge URL template resolved to invalid absolute URI: '{resolvedUrl}'.");
        }

        EnsureHostIsAllowed(manifest, requestUri);

        using var request = new HttpRequestMessage(method, requestUri);
        using var response = await HostHttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken);
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

    private static HttpClient CreateHostHttpClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.mangadex.org") // TODO get from manifest
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("EMMA-PluginHost/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        return client;
    }
}

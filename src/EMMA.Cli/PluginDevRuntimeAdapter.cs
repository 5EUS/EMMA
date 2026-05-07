using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EMMA.Api;
using EMMA.Api.Embedded;
using EMMA.Contracts.Api.V1;
using PluginContracts = EMMA.Contracts.Plugins;
using EMMA.Domain;
using EMMA.Plugin.Common;
using Grpc.Core;
using Grpc.Net.Client;

namespace EMMA.Cli;

public sealed record PluginDevBuildPlan(
    string Name,
    string WorkingDirectory,
    string Command,
    IReadOnlyList<string> Arguments,
    string? ArtifactPath,
    string Description);

public sealed record PluginDevPackResult(string PackagePath, string ManifestPath, string ArtifactPath);

public sealed record PluginDevScenarioResult(string Name, bool Succeeded, IReadOnlyList<string> Messages);

public interface IPluginDevRuntimeAdapter
{
    string Name { get; }
    bool SupportsReload { get; }
    bool SupportsPageAsset { get; }
    bool SupportsScenarios { get; }

    Task<IReadOnlyList<SearchItem>> SearchAsync(string query, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChapterItem>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken);
    Task<PageItem?> GetPageAsync(string mediaId, string chapterId, int index, CancellationToken cancellationToken);
    Task<IReadOnlyList<PageItem>> GetPagesAsync(string mediaId, string chapterId, int startIndex, int count, CancellationToken cancellationToken);
    Task<byte[]?> GetPageAssetAsync(string mediaId, string chapterId, CancellationToken cancellationToken);
    Task<string> ReloadAsync(CancellationToken cancellationToken);
}

public sealed class HostBridgeRuntimeAdapter(EmbeddedRuntime runtime, EmbeddedPagedMediaApi api) : IPluginDevRuntimeAdapter
{
    public string Name => "host-bridge";
    public bool SupportsReload => false;
    public bool SupportsPageAsset => true;
    public bool SupportsScenarios => true;

    public async Task<IReadOnlyList<SearchItem>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var response = await api.SearchAsync(new SearchRequest
        {
            Query = query,
            Context = PluginDevRequestContext.Create("cli")
        }, cancellationToken);

        if (response.OutcomeCase == SearchResponse.OutcomeOneofCase.Error)
        {
            throw new InvalidOperationException($"Search failed: {response.Error.Code} {response.Error.Message}");
        }

        return response.Result.Items.Select(static item => new SearchItem(
            item.Id,
            item.Source,
            item.Title,
            item.MediaType.ToString().ToLowerInvariant(),
            item.ThumbnailUrl,
            null,
            null)).ToArray();
    }

    public async Task<IReadOnlyList<ChapterItem>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken)
    {
        var response = await api.GetChaptersAsync(new ChaptersRequest
        {
            MediaId = mediaId,
            Context = PluginDevRequestContext.Create("cli")
        }, cancellationToken);

        if (response.OutcomeCase == ChaptersResponse.OutcomeOneofCase.Error)
        {
            throw new InvalidOperationException($"Chapters failed: {response.Error.Code} {response.Error.Message}");
        }

        return response.Result.Items.Select(static item => new ChapterItem(item.Id, item.Number, item.Title, item.UploaderGroups.ToArray())).ToArray();
    }

    public async Task<PageItem?> GetPageAsync(string mediaId, string chapterId, int index, CancellationToken cancellationToken)
    {
        var response = await api.GetPageAsync(new PageRequest
        {
            MediaId = mediaId,
            ChapterId = chapterId,
            Index = index,
            Context = PluginDevRequestContext.Create("cli")
        }, cancellationToken);

        if (response.OutcomeCase == PageResponse.OutcomeOneofCase.Error)
        {
            throw new InvalidOperationException($"Page failed: {response.Error.Code} {response.Error.Message}");
        }

        return new PageItem(chapterId, response.Page.Index, response.Page.ContentUri);
    }

    public async Task<IReadOnlyList<PageItem>> GetPagesAsync(string mediaId, string chapterId, int startIndex, int count, CancellationToken cancellationToken)
    {
        var pagesResult = await runtime.Pipeline.GetPagesAsync(MediaId.Create(mediaId), chapterId, startIndex, count, cancellationToken);
        return pagesResult.Pages.Select(static page => new PageItem(page.PageId, page.Index, page.ContentUri.ToString())).ToArray();
    }

    public async Task<byte[]?> GetPageAssetAsync(string mediaId, string chapterId, CancellationToken cancellationToken)
    {
        var response = await api.GetPageAssetAsync(new PageAssetRequest
        {
            MediaId = mediaId,
            ChapterId = chapterId,
            Context = PluginDevRequestContext.Create("cli")
        }, cancellationToken);

        if (response.OutcomeCase == PageAssetResponse.OutcomeOneofCase.Error)
        {
            throw new InvalidOperationException($"Page asset failed: {response.Error.Code} {response.Error.Message}");
        }

        return response.Asset.Payload.ToArray();
    }

    public Task<string> ReloadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult("Host-bridge runtime has no explicit reload path yet.");
    }
}

public sealed class UnsupportedRuntimeAdapter(string name, string reason) : IPluginDevRuntimeAdapter
{
    public string Name => name;
    public bool SupportsReload => false;
    public bool SupportsPageAsset => false;
    public bool SupportsScenarios => false;

    public Task<IReadOnlyList<SearchItem>> SearchAsync(string query, CancellationToken cancellationToken)
        => Task.FromException<IReadOnlyList<SearchItem>>(new InvalidOperationException(reason));

    public Task<IReadOnlyList<ChapterItem>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken)
        => Task.FromException<IReadOnlyList<ChapterItem>>(new InvalidOperationException(reason));

    public Task<PageItem?> GetPageAsync(string mediaId, string chapterId, int index, CancellationToken cancellationToken)
        => Task.FromException<PageItem?>(new InvalidOperationException(reason));

    public Task<IReadOnlyList<PageItem>> GetPagesAsync(string mediaId, string chapterId, int startIndex, int count, CancellationToken cancellationToken)
        => Task.FromException<IReadOnlyList<PageItem>>(new InvalidOperationException(reason));

    public Task<byte[]?> GetPageAssetAsync(string mediaId, string chapterId, CancellationToken cancellationToken)
        => Task.FromException<byte[]?>(new InvalidOperationException(reason));

    public Task<string> ReloadAsync(CancellationToken cancellationToken)
        => Task.FromException<string>(new InvalidOperationException(reason));
}

public sealed class DeferredWasmRuntimeAdapter(
    string rootDirectory,
    string runtimeLibraryPath,
    IReadOnlyList<string> permittedDomains,
    Func<string?> resolveComponentPath) : IPluginDevRuntimeAdapter
{
    private readonly Lazy<WasmCliRuntimeAdapter> _inner = new(() =>
    {
        var componentPath = resolveComponentPath()
            ?? throw new InvalidOperationException("Direct WASM profile could not resolve a built .wasm component artifact. Run 'build' for the active profile first.");
        return new WasmCliRuntimeAdapter(rootDirectory, componentPath, runtimeLibraryPath, permittedDomains);
    });

    public string Name => "wasm-native-direct";
    public bool SupportsReload => true;
    public bool SupportsPageAsset => false;
    public bool SupportsScenarios => true;

    public Task<IReadOnlyList<SearchItem>> SearchAsync(string query, CancellationToken cancellationToken)
        => _inner.Value.SearchAsync(query, cancellationToken);

    public Task<IReadOnlyList<ChapterItem>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken)
        => _inner.Value.GetChaptersAsync(mediaId, cancellationToken);

    public Task<PageItem?> GetPageAsync(string mediaId, string chapterId, int index, CancellationToken cancellationToken)
        => _inner.Value.GetPageAsync(mediaId, chapterId, index, cancellationToken);

    public Task<IReadOnlyList<PageItem>> GetPagesAsync(string mediaId, string chapterId, int startIndex, int count, CancellationToken cancellationToken)
        => _inner.Value.GetPagesAsync(mediaId, chapterId, startIndex, count, cancellationToken);

    public Task<byte[]?> GetPageAssetAsync(string mediaId, string chapterId, CancellationToken cancellationToken)
        => _inner.Value.GetPageAssetAsync(mediaId, chapterId, cancellationToken);

    public Task<string> ReloadAsync(CancellationToken cancellationToken)
        => _inner.Value.ReloadAsync(cancellationToken);
}

public sealed class NativeProcessRuntimeAdapter : IPluginDevRuntimeAdapter
{
    private const string HostAuthHeaderName = "x-emma-plugin-host-auth";
    private const string CorrelationIdHeaderName = "x-correlation-id";

    private readonly string _entryPointPath;
    private readonly Uri _hostUri;
    private readonly PluginRuntimeTarget _target;
    private readonly string _authToken = Guid.NewGuid().ToString("n");
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private Process? _process;

    public NativeProcessRuntimeAdapter(
        string entryPointPath,
        Uri hostUri,
        PluginRuntimeTarget target)
    {
        _entryPointPath = entryPointPath;
        _hostUri = hostUri;
        _target = target;
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopProcess();
    }

    public string Name => $"native-{_target.ToString().ToLowerInvariant()}-local";
    public bool SupportsReload => true;
    public bool SupportsPageAsset => false;
    public bool SupportsScenarios => true;

    public async Task<IReadOnlyList<SearchItem>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        await EnsureRunningAsync(cancellationToken);

        using var httpClient = CreateGrpcHttpClient();
        using var channel = GrpcChannel.ForAddress(_hostUri, new GrpcChannelOptions { HttpClient = httpClient });
        var client = new PluginContracts.SearchProvider.SearchProviderClient(channel);
        var correlationId = Guid.NewGuid().ToString("n");
        var response = await client.SearchAsync(new PluginContracts.SearchRequest
        {
            Query = query ?? string.Empty,
            Context = CreatePluginRequestContext(correlationId)
        }, headers: CreateGrpcHeaders(correlationId), cancellationToken: cancellationToken);

        return response.Results
            .Select(MapSearchItem)
            .ToArray();
    }

    public async Task<IReadOnlyList<ChapterItem>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken)
    {
        await EnsureRunningAsync(cancellationToken);

        using var httpClient = CreateGrpcHttpClient();
        using var channel = GrpcChannel.ForAddress(_hostUri, new GrpcChannelOptions { HttpClient = httpClient });
        var client = new PluginContracts.PageProvider.PageProviderClient(channel);
        var correlationId = Guid.NewGuid().ToString("n");
        var response = await client.GetChaptersAsync(new PluginContracts.ChaptersRequest
        {
            MediaId = mediaId,
            Context = CreatePluginRequestContext(correlationId)
        }, headers: CreateGrpcHeaders(correlationId), cancellationToken: cancellationToken);

        return response.Chapters
            .Select(static item => new ChapterItem(item.Id, item.Number, item.Title, item.UploaderGroups.ToArray()))
            .ToArray();
    }

    public async Task<PageItem?> GetPageAsync(string mediaId, string chapterId, int index, CancellationToken cancellationToken)
    {
        await EnsureRunningAsync(cancellationToken);

        using var httpClient = CreateGrpcHttpClient();
        using var channel = GrpcChannel.ForAddress(_hostUri, new GrpcChannelOptions { HttpClient = httpClient });
        var client = new PluginContracts.PageProvider.PageProviderClient(channel);
        var correlationId = Guid.NewGuid().ToString("n");
        var response = await client.GetPageAsync(new PluginContracts.PageRequest
        {
            MediaId = mediaId,
            ChapterId = chapterId,
            Index = index,
            Context = CreatePluginRequestContext(correlationId)
        }, headers: CreateGrpcHeaders(correlationId), cancellationToken: cancellationToken);

        return response.Page is null
            ? null
            : new PageItem(response.Page.Id, response.Page.Index, response.Page.ContentUri);
    }

    public async Task<IReadOnlyList<PageItem>> GetPagesAsync(string mediaId, string chapterId, int startIndex, int count, CancellationToken cancellationToken)
    {
        await EnsureRunningAsync(cancellationToken);

        using var httpClient = CreateGrpcHttpClient();
        using var channel = GrpcChannel.ForAddress(_hostUri, new GrpcChannelOptions { HttpClient = httpClient });
        var client = new PluginContracts.PageProvider.PageProviderClient(channel);
        var correlationId = Guid.NewGuid().ToString("n");
        var response = await client.GetPagesAsync(new PluginContracts.PagesRequest
        {
            MediaId = mediaId,
            ChapterId = chapterId,
            StartIndex = startIndex,
            Count = count,
            Context = CreatePluginRequestContext(correlationId)
        }, headers: CreateGrpcHeaders(correlationId), cancellationToken: cancellationToken);

        return response.Pages
            .Select(static item => new PageItem(item.Id, item.Index, item.ContentUri))
            .ToArray();
    }

    public async Task<byte[]?> GetPageAssetAsync(string mediaId, string chapterId, CancellationToken cancellationToken)
    {
        await EnsureRunningAsync(cancellationToken);
        throw new NotSupportedException("Native direct runtime does not expose page-asset retrieval yet.");
    }

    public async Task<string> ReloadAsync(CancellationToken cancellationToken)
    {
        await RestartAsync(cancellationToken);
        return $"Restarted native {_target} runtime at '{_entryPointPath}'.";
    }

    private async Task EnsureRunningAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_process is not null && !_process.HasExited)
            {
                return;
            }

            StartProcess();
            await WaitForPortAsync(cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task RestartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            StopProcess();
            StartProcess();
            await WaitForPortAsync(cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void StartProcess()
    {
        if (!File.Exists(_entryPointPath))
        {
            throw new InvalidOperationException($"Native runtime entrypoint was not found: {_entryPointPath}");
        }

        _stdout.Clear();
        _stderr.Clear();

        var port = _hostUri.Port.ToString(CultureInfo.InvariantCulture);
        var startInfo = new ProcessStartInfo
        {
            FileName = _entryPointPath,
            WorkingDirectory = Path.GetDirectoryName(_entryPointPath) ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port);
        startInfo.Environment["EMMA_PLUGIN_PORT"] = port;
        startInfo.Environment["EMMA_TEST_PLUGIN_PORT"] = port;
        startInfo.Environment["EMMA_PLUGIN_HOST_AUTH_TOKEN"] = _authToken;
        startInfo.Environment["ASPNETCORE_URLS"] = _hostUri.GetLeftPart(UriPartial.Authority);

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                AppendLog(_stdout, args.Data);
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                AppendLog(_stderr, args.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start native runtime '{_entryPointPath}'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
    }

    private async Task WaitForPortAsync(CancellationToken cancellationToken)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_process is not null && _process.HasExited)
            {
                throw new InvalidOperationException(BuildStartupFailureMessage());
            }

            using var tcpClient = new TcpClient();
            try
            {
                await tcpClient.ConnectAsync(_hostUri.Host, _hostUri.Port, cancellationToken);
                return;
            }
            catch (SocketException)
            {
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new InvalidOperationException($"Timed out waiting for native {_target} runtime to listen on '{_hostUri}'. {BuildStartupFailureMessage()}");
    }

    private string BuildStartupFailureMessage()
    {
        var stderr = _stderr.ToString().Trim();
        var stdout = _stdout.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            return $"stderr: {stderr}";
        }

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            return $"stdout: {stdout}";
        }

        return "The native runtime exited or did not become reachable before the startup deadline.";
    }

    private void StopProcess()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch
        {
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private static void AppendLog(StringBuilder builder, string line)
    {
        lock (builder)
        {
            builder.AppendLine(line);
        }
    }

    private HttpClient CreateGrpcHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true
        };

        return new HttpClient(handler)
        {
            BaseAddress = _hostUri,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    private static PluginContracts.RequestContext CreatePluginRequestContext(string correlationId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        return new PluginContracts.RequestContext
        {
            CorrelationId = correlationId,
            DeadlineUtc = deadline.ToString("O")
        };
    }

    private Metadata CreateGrpcHeaders(string correlationId)
    {
        return new Metadata
        {
            { HostAuthHeaderName, _authToken },
            { CorrelationIdHeaderName, correlationId }
        };
    }

    private static SearchItem MapSearchItem(PluginContracts.MediaSummary item)
    {
        var metadata = item.Metadata.Count == 0
            ? null
            : item.Metadata.Select(static entry => new MetadataItem(entry.Key, entry.Value)).ToArray();

        return new SearchItem(
            item.Id,
            item.Source,
            item.Title,
            item.MediaType,
            string.IsNullOrWhiteSpace(item.ThumbnailUrl) ? null : item.ThumbnailUrl,
            string.IsNullOrWhiteSpace(item.Description) ? null : item.Description,
            metadata);
    }
}

public sealed class WasmCliRuntimeAdapter : IPluginDevRuntimeAdapter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _rootDirectory;
    private readonly string _componentPath;
    private readonly string _runtimeLibraryPath;
    private readonly IReadOnlyList<string> _permittedDomains;

    public WasmCliRuntimeAdapter(string rootDirectory, string componentPath, string runtimeLibraryPath, IReadOnlyList<string> permittedDomains)
    {
        _rootDirectory = rootDirectory;
        _componentPath = componentPath;
        _runtimeLibraryPath = runtimeLibraryPath;
        _permittedDomains = permittedDomains;
        NativeWasmRuntimeBindings.Configure(_runtimeLibraryPath);
    }

    public string Name => "wasm-native-direct";
    public bool SupportsReload => true;
    public bool SupportsPageAsset => false;
    public bool SupportsScenarios => true;

    public async Task<IReadOnlyList<SearchItem>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        return await InvokeTypedOperationAsync<IReadOnlyList<SearchItem>>(
            nestedOperation: PluginOperationNames.Search,
            mediaId: null,
            mediaType: PluginMediaTypes.Paged,
            argsJson: JsonSerializer.Serialize(new { query }),
            cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<ChapterItem>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken)
    {
        return await InvokeTypedOperationAsync<IReadOnlyList<ChapterItem>>(
            nestedOperation: PluginOperationNames.Chapters,
            mediaId: mediaId,
            mediaType: PluginMediaTypes.Paged,
            argsJson: null,
            cancellationToken) ?? [];
    }

    public async Task<PageItem?> GetPageAsync(string mediaId, string chapterId, int index, CancellationToken cancellationToken)
    {
        return await InvokeTypedOperationAsync<PageItem>(
            nestedOperation: PluginOperationNames.Page,
            mediaId: mediaId,
            mediaType: PluginMediaTypes.Paged,
            argsJson: JsonSerializer.Serialize(new { chapterId, pageIndex = index }),
            cancellationToken);
    }

    public async Task<IReadOnlyList<PageItem>> GetPagesAsync(string mediaId, string chapterId, int startIndex, int count, CancellationToken cancellationToken)
    {
        return await InvokeTypedOperationAsync<IReadOnlyList<PageItem>>(
            nestedOperation: PluginOperationNames.Pages,
            mediaId: mediaId,
            mediaType: PluginMediaTypes.Paged,
            argsJson: JsonSerializer.Serialize(new { chapterId, startIndex, count }),
            cancellationToken) ?? [];
    }

    public Task<byte[]?> GetPageAssetAsync(string mediaId, string chapterId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("Direct WASM runtime does not expose page-asset retrieval yet.");
    }

    public Task<string> ReloadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult("WASM direct runtime is process-per-invocation; each command already runs against the latest project state.");
    }

    private async Task<string> InvokeAsync(string operation, IReadOnlyList<string> operationArgs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(_componentPath))
        {
            throw new InvalidOperationException($"Resolved WASM component was not found: {_componentPath}");
        }

        return await Task.Run(() => NativeWasmRuntimeBindings.Invoke(_componentPath, operation, operationArgs, _permittedDomains), cancellationToken);
    }

    private async Task<T?> InvokeTypedOperationAsync<T>(
        string nestedOperation,
        string? mediaId,
        string? mediaType,
        string? argsJson,
        CancellationToken cancellationToken)
    {
        var resultJson = await InvokeAsync(
            PluginOperationNames.Invoke,
            [
                nestedOperation,
                mediaId ?? string.Empty,
                mediaType ?? string.Empty,
                argsJson ?? string.Empty
            ],
            cancellationToken);

        var operationResult = Deserialize<OperationResult>(resultJson)
            ?? throw new InvalidOperationException($"Direct WASM runtime returned an invalid invoke envelope for '{nestedOperation}'.");

        if (operationResult.isError)
        {
            throw new InvalidOperationException(operationResult.error ?? $"Direct WASM invoke failed for '{nestedOperation}'.");
        }

        if (string.IsNullOrWhiteSpace(operationResult.payloadJson))
        {
            return default;
        }

        return Deserialize<T>(operationResult.payloadJson);
    }

    private static T? Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(json, SerializerOptions);
    }
}

public static class NativeWasmRuntimeBindings
{
    private static readonly Lock ResolverLock = new();
    private static bool _configured;
    private static string? _configuredLibraryPath;

    public static void Configure(string runtimeLibraryPath)
    {
        if (string.IsNullOrWhiteSpace(runtimeLibraryPath))
        {
            throw new InvalidOperationException("A native WASM runtime library path is required for direct component execution.");
        }

        lock (ResolverLock)
        {
            if (_configured && string.Equals(_configuredLibraryPath, runtimeLibraryPath, StringComparison.Ordinal))
            {
                return;
            }

            NativeLibrary.SetDllImportResolver(typeof(NativeWasmRuntimeBindings).Assembly, (libraryName, _, _) =>
            {
                if (!string.Equals(libraryName, "emma_wasm_runtime", StringComparison.Ordinal))
                {
                    return IntPtr.Zero;
                }

                return NativeLibrary.TryLoad(runtimeLibraryPath, out var handle)
                    ? handle
                    : IntPtr.Zero;
            });

            _configured = true;
            _configuredLibraryPath = runtimeLibraryPath;
        }
    }

    public static string Invoke(string componentPath, string operation, IReadOnlyList<string> operationArgs, IReadOnlyList<string> permittedDomains)
    {
        var argsJson = JsonSerializer.Serialize(new
        {
            args = operationArgs,
            permittedDomains
        });
        var componentPtr = Marshal.StringToCoTaskMemUTF8(componentPath);
        var operationPtr = Marshal.StringToCoTaskMemUTF8(operation);
        var argsPtr = Marshal.StringToCoTaskMemUTF8(argsJson);

        try
        {
            var code = InvokeNative(componentPtr, operationPtr, argsPtr, 30_000u, out var outJson, out var outError);
            try
            {
                var json = PtrToString(outJson);
                var error = PtrToString(outError);
                if (code != 0)
                {
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(error)
                            ? $"Native WASM runtime invocation failed with code {code}."
                            : error);
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    throw new InvalidOperationException($"Native WASM runtime returned empty output for operation '{operation}'.");
                }

                return json;
            }
            finally
            {
                FreeString(outJson);
                FreeString(outError);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(componentPtr);
            Marshal.FreeCoTaskMem(operationPtr);
            Marshal.FreeCoTaskMem(argsPtr);
        }
    }

    private static string? PtrToString(IntPtr ptr)
    {
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }

    [DllImport("emma_wasm_runtime", EntryPoint = "emma_wasm_component_invoke", CallingConvention = CallingConvention.Cdecl)]
    private static extern int InvokeNative(
        IntPtr componentPath,
        IntPtr operation,
        IntPtr operationArgsJson,
        uint timeoutMs,
        out IntPtr outJson,
        out IntPtr outError);

    [DllImport("emma_wasm_runtime", EntryPoint = "emma_wasm_runtime_free_string", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FreeString(IntPtr value);
}

public sealed class PluginDevBuildService
{
    public PluginDevBuildPlan? GetBuildPlan(PluginDevSession session)
    {
        var projectPath = session.Discovery.ProjectFilePath;
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return null;
        }

        return session.Profile.RuntimeTarget switch
        {
            PluginRuntimeTarget.Wasm => CreateWasmBuildPlan(session.Discovery.RootDirectory, projectPath),
            PluginRuntimeTarget.Linux => CreateNativeBuildPlan(session.Discovery.RootDirectory, projectPath, PluginRuntimeTarget.Linux),
            PluginRuntimeTarget.Windows => CreateNativeBuildPlan(session.Discovery.RootDirectory, projectPath, PluginRuntimeTarget.Windows),
            _ => null
        };
    }

    private static PluginDevBuildPlan CreateWasmBuildPlan(string rootDirectory, string projectPath)
    {
        var publishDirectory = Path.Combine(rootDirectory, "artifacts", "build-wasm", "publish");
        var projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException($"Unable to resolve project directory for '{projectPath}'.");
        var quotedProjectDirectory = PluginDevProcessRunner.QuoteForShell(projectDirectory);
        var quotedProjectPath = PluginDevProcessRunner.QuoteForShell(projectPath);
        var quotedPublishDirectory = PluginDevProcessRunner.QuoteForShell(publishDirectory);

        return new PluginDevBuildPlan(
            "wasm-build",
            rootDirectory,
            "sh",
            [
                "-lc",
                $"set -euo pipefail; rm -rf {quotedProjectDirectory}/bin {quotedProjectDirectory}/obj {quotedPublishDirectory}; mkdir -p {quotedPublishDirectory}; dotnet restore {quotedProjectPath} --no-cache --force-evaluate --runtime wasi-wasm -p:PluginTransport=Wasm >/dev/null; publish_none_log={quotedPublishDirectory}/publish-nativecodegen-none.log; if ! WASI_SDK_PATH=\"${{WASI_SDK_PATH:-}}\" dotnet publish {quotedProjectPath} -c Release -r wasi-wasm --self-contained true -p:PublishAot=false -p:NativeCodeGen=none -p:DebugType=None -p:DebugSymbols=false -p:WasmSingleFileBundle=true -p:PluginTransport=Wasm -o {quotedPublishDirectory} 2>&1 | tee \"$publish_none_log\"; then if grep -q 'native/.*\\.wasm\" because it was not found' \"$publish_none_log\"; then echo 'WASM publish produced no native artifact with NativeCodeGen=none; retrying with NativeCodeGen=llvm...'; WASI_SDK_PATH=\"${{WASI_SDK_PATH:-}}\" dotnet publish {quotedProjectPath} -c Release -r wasi-wasm --self-contained true -p:PublishAot=false -p:NativeCodeGen=llvm -p:DebugType=None -p:DebugSymbols=false -p:WasmSingleFileBundle=true -p:PluginTransport=Wasm -o {quotedPublishDirectory}; else exit 1; fi; fi"
            ],
            publishDirectory,
            "Normalized WASM publish plan for CLI-driven plugin development.");
    }

    private static PluginDevBuildPlan CreateNativeBuildPlan(string rootDirectory, string projectPath, PluginRuntimeTarget target)
    {
        var runtimeIdentifier = target == PluginRuntimeTarget.Windows ? "win-x64" : "linux-x64";
        var publishDirectory = Path.Combine(rootDirectory, "artifacts", $"build-{runtimeIdentifier}", "publish");

        return new PluginDevBuildPlan(
            $"{target.ToString().ToLowerInvariant()}-native-build",
            rootDirectory,
            "dotnet",
            [
                "publish",
                projectPath,
                "-c",
                "Release",
                "-r",
                runtimeIdentifier,
                "--self-contained",
                "true",
                "-p:PluginTransport=AspNet",
                "-o",
                publishDirectory
            ],
            publishDirectory,
            $"Normalized {target} native publish plan for CLI-driven plugin development.");
    }

    public async Task<string> BuildAsync(PluginDevSession session, CancellationToken cancellationToken)
    {
        var plan = GetBuildPlan(session)
            ?? throw new InvalidOperationException("No normalized build plan is available for the active profile.");

        var result = await PluginDevProcessRunner.RunAsync(plan.WorkingDirectory, plan.Command, plan.Arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Build failed.\n{result.StandardError}");
        }

        return string.IsNullOrWhiteSpace(result.StandardOutput)
            ? $"Build completed for profile '{session.Profile.Name}'."
            : result.StandardOutput.Trim();
    }

    public PluginDevPackResult PackWasm(PluginDevSession session)
    {
        if (session.Profile.RuntimeTarget != PluginRuntimeTarget.Wasm)
        {
            throw new InvalidOperationException("The normalized pack flow is only implemented for the WASM profile in Phase 3.");
        }

        var manifestPath = session.Discovery.ManifestPath;
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            throw new InvalidOperationException("Cannot pack a WASM plugin without a discovered plugin manifest.");
        }

        var artifactPath = ResolveWasmArtifactPath(session)
            ?? throw new InvalidOperationException("No WASM artifact could be resolved for packing.");

        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var pluginId = manifestDoc.RootElement.GetProperty("id").GetString() ?? "plugin";
        var version = manifestDoc.RootElement.GetProperty("version").GetString() ?? "0.0.0";

        var packageRoot = Path.Combine(session.Discovery.RootDirectory, "artifacts", "pack", $"{version}-wasm");
        var manifestOutDir = Path.Combine(packageRoot, "manifest");
        var pluginOutDir = Path.Combine(packageRoot, pluginId, "wasm");
        var zipPath = Path.Combine(session.Discovery.RootDirectory, "artifacts", "pack", $"{pluginId}_{version}_wasm.zip");

        if (Directory.Exists(packageRoot))
        {
            Directory.Delete(packageRoot, recursive: true);
        }

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        Directory.CreateDirectory(manifestOutDir);
        Directory.CreateDirectory(pluginOutDir);

        var manifestOutPath = Path.Combine(manifestOutDir, $"{pluginId}.json");
        File.Copy(manifestPath, manifestOutPath, overwrite: true);

        var artifactOutPath = Path.Combine(pluginOutDir, "plugin.wasm");
        File.Copy(artifactPath, artifactOutPath, overwrite: true);

        ZipFile.CreateFromDirectory(packageRoot, zipPath);
        return new PluginDevPackResult(zipPath, manifestOutPath, artifactOutPath);
    }

    public string? ResolveWasmArtifactPath(PluginDevSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.Profile.ArtifactPath))
        {
            var explicitArtifact = ResolveWasmFile(session.Profile.ArtifactPath);
            if (!string.IsNullOrWhiteSpace(explicitArtifact))
            {
                return explicitArtifact;
            }
        }

        foreach (var candidate in session.Discovery.ArtifactCandidates.Where(static candidate => candidate.Target == PluginRuntimeTarget.Wasm))
        {
            var resolved = ResolveWasmFile(candidate.Path);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    public string? ResolveWasmFile(string path)
    {
        if (File.Exists(path) && path.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        var candidates = Directory.EnumerateFiles(path, "*.wasm", SearchOption.AllDirectories)
            .Where(static file => !file.EndsWith("dotnet.wasm", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return candidates.FirstOrDefault();
    }

    public string? ResolveNativeEntrypointPath(PluginDevSession session)
    {
        if (session.Profile.RuntimeTarget is not (PluginRuntimeTarget.Linux or PluginRuntimeTarget.Windows))
        {
            return null;
        }

        var projectName = session.Discovery.ProjectFilePath is null
            ? null
            : Path.GetFileNameWithoutExtension(session.Discovery.ProjectFilePath);
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(session.Profile.ArtifactPath))
        {
            var explicitEntrypoint = ResolveNativeEntrypointPath(session.Profile.ArtifactPath, projectName, session.Profile.RuntimeTarget);
            if (!string.IsNullOrWhiteSpace(explicitEntrypoint))
            {
                return explicitEntrypoint;
            }
        }

        foreach (var candidate in session.Discovery.ArtifactCandidates.Where(candidate => candidate.Target == session.Profile.RuntimeTarget))
        {
            var resolved = ResolveNativeEntrypointPath(candidate.Path, projectName, session.Profile.RuntimeTarget);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    private static string? ResolveNativeEntrypointPath(string path, string projectName, PluginRuntimeTarget target)
    {
        var executableName = target == PluginRuntimeTarget.Windows ? $"{projectName}.exe" : projectName;

        if (File.Exists(path))
        {
            var fileName = Path.GetFileName(path);
            return string.Equals(fileName, executableName, StringComparison.OrdinalIgnoreCase)
                ? path
                : null;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        var directCandidate = Path.Combine(path, executableName);
        if (File.Exists(directCandidate))
        {
            return directCandidate;
        }

        var candidates = Directory.EnumerateFiles(path, executableName, SearchOption.AllDirectories)
            .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return candidates.FirstOrDefault();
    }
}

public static class PluginDevRuntimeLibraryResolver
{
    public static string Resolve(string workingDirectory)
    {
        var root = FindRepoRoot(workingDirectory)
            ?? FindRepoRoot(AppContext.BaseDirectory)
            ?? FindRepoRoot(Path.GetDirectoryName(typeof(PluginDevRuntimeLibraryResolver).Assembly.Location) ?? string.Empty)
            ?? throw new InvalidOperationException("Unable to locate the EMMA repository root while resolving the native WASM runtime library.");

        var libraryFileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "emma_wasm_runtime.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "libemma_wasm_runtime.dylib"
                : "libemma_wasm_runtime.so";

        var platformDir = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "win-x64"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "osx-arm64"
                : "linux-x64";

        var path = Path.Combine(root, "artifacts", "wasm-runtime-native", platformDir, libraryFileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Native WASM runtime library was not found: {path}");
        }

        return path;
    }

    private static string? FindRepoRoot(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return null;
        }

        var current = new DirectoryInfo(workingDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EMMA.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}

public sealed class PluginDevScenarioRunner
{
    public async Task<PluginDevScenarioResult> RunAsync(PluginDevSession session, string scenarioName, string? query, CancellationToken cancellationToken)
    {
        var normalizedScenario = (scenarioName ?? string.Empty).Trim().ToLowerInvariant();
        return normalizedScenario switch
        {
            "paged-smoke" => await RunPagedSmokeAsync(session, string.IsNullOrWhiteSpace(query) ? "naruto" : query.Trim(), cancellationToken),
            _ => new PluginDevScenarioResult(normalizedScenario, false, [$"Unknown scenario '{scenarioName}'. Supported scenarios: paged-smoke"]) 
        };
    }

    private static async Task<PluginDevScenarioResult> RunPagedSmokeAsync(PluginDevSession session, string query, CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        var runtime = session.RuntimeAdapter;

        var searchItems = await runtime.SearchAsync(query, cancellationToken);
        messages.Add($"Search('{query}') returned {searchItems.Count} item(s).");
        if (searchItems.Count == 0)
        {
            return new PluginDevScenarioResult("paged-smoke", false, messages);
        }

        var firstItem = searchItems[0];
        messages.Add($"Selected media '{firstItem.title}' ({firstItem.id}).");

        var chapters = await runtime.GetChaptersAsync(firstItem.id, cancellationToken);
        messages.Add($"Chapters returned {chapters.Count} item(s).");
        if (chapters.Count == 0)
        {
            return new PluginDevScenarioResult("paged-smoke", false, messages);
        }

        var firstChapter = chapters[0];
        messages.Add($"Selected chapter '{firstChapter.title}' ({firstChapter.id}).");

        var page = await runtime.GetPageAsync(firstItem.id, firstChapter.id, 0, cancellationToken);
        if (page is null)
        {
            messages.Add("Page(0) returned no page.");
            return new PluginDevScenarioResult("paged-smoke", false, messages);
        }

        messages.Add($"Page(0) resolved content URI '{page.contentUri}'.");
        return new PluginDevScenarioResult("paged-smoke", true, messages);
    }
}

public static class PluginDevRequestContext
{
    public static ApiRequestContext Create(string clientId)
    {
        return new ApiRequestContext
        {
            CorrelationId = Guid.NewGuid().ToString("n"),
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O"),
            ClientId = clientId
        };
    }
}

public sealed record PluginDevProcessResult(int ExitCode, string StandardOutput, string StandardError);

public static class PluginDevProcessRunner
{
    public static async Task<PluginDevProcessResult> RunAsync(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new PluginDevProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    public static string QuoteForShell(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'")}'";
    }
}
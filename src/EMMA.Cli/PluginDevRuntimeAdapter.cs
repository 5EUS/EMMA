using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

public sealed record PluginDevPackResult(string PackagePath, string ManifestPath, string ArtifactPath, string PackageDirectory);

public sealed record PluginDevScenarioResult(string Name, bool Succeeded, IReadOnlyList<string> Messages);

public sealed record PluginDevVideoTrack(
    string Id,
    string Label,
    string? Language = null,
    string? Codec = null,
    bool IsDefault = false);

public sealed record PluginDevVideoStream(
    string Id,
    string Label,
    string PlaylistUri,
    IReadOnlyDictionary<string, string>? RequestHeaders = null,
    string? RequestCookies = null,
    string? StreamType = null,
    bool IsLive = false,
    bool DrmProtected = false,
    string? DrmScheme = null,
    IReadOnlyList<PluginDevVideoTrack>? AudioTracks = null,
    IReadOnlyList<PluginDevVideoTrack>? SubtitleTracks = null,
    string? DefaultAudioTrackId = null,
    string? DefaultSubtitleTrackId = null);

public sealed record PluginDevVideoSegment(string ContentType, string PayloadBase64, int SizeBytes);

public sealed record PluginDevScenarioDefinition(
    string Name,
    string DisplayName,
    string Description,
    string? DefaultQuery,
    bool SupportsQuery = true,
    string QueryLabel = "Query");

public sealed record PluginDevConfiguredScenario(
    string Name,
    string DisplayName,
    string Description,
    string? DefaultQuery,
    bool SupportsQuery,
    string QueryLabel,
    IReadOnlyList<PluginDevScenarioStep> Steps);

public sealed record PluginDevScenarioStep(
    string Op,
    string? Save,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    IReadOnlySet<string> NoWarn);

public sealed record PluginDevRuntimeLogLine(string Level, string Message);

public interface IPluginDevRuntimeAdapter
{
    string Name { get; }
    bool SupportsReload { get; }
    bool SupportsPageAsset { get; }
    bool SupportsScenarios { get; }

    Task<IReadOnlyList<SearchItem>> SearchAsync(string query, CancellationToken cancellationToken);
    Task<IReadOnlyList<SearchItem>> EnrichSearchItemsAsync(IReadOnlyList<SearchItem> items, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChapterItem>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken);
    Task<PageItem?> GetPageAsync(string mediaId, string chapterId, int index, CancellationToken cancellationToken);
    Task<IReadOnlyList<PageItem>> GetPagesAsync(string mediaId, string chapterId, int startIndex, int count, CancellationToken cancellationToken);
    Task<IReadOnlyList<PluginDevVideoStream>> GetVideoStreamsAsync(string mediaId, CancellationToken cancellationToken);
    Task<PluginDevVideoSegment?> GetVideoSegmentAsync(string mediaId, string streamId, int sequence, CancellationToken cancellationToken);
    Task<byte[]?> GetPageAssetAsync(string mediaId, string chapterId, CancellationToken cancellationToken);
    Task<string> ReloadAsync(CancellationToken cancellationToken);
}

public interface IPluginDevRuntimeLogSource
{
    IReadOnlyList<PluginDevRuntimeLogLine> DrainRuntimeLogs();
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

    public Task<IReadOnlyList<SearchItem>> EnrichSearchItemsAsync(IReadOnlyList<SearchItem> items, CancellationToken cancellationToken)
        => Task.FromException<IReadOnlyList<SearchItem>>(new NotSupportedException("Host-bridge runtime does not expose search metadata enrichment yet."));

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

    public Task<IReadOnlyList<PluginDevVideoStream>> GetVideoStreamsAsync(string mediaId, CancellationToken cancellationToken)
        => Task.FromException<IReadOnlyList<PluginDevVideoStream>>(new NotSupportedException("Host-bridge runtime does not expose video stream inspection yet."));

    public Task<PluginDevVideoSegment?> GetVideoSegmentAsync(string mediaId, string streamId, int sequence, CancellationToken cancellationToken)
        => Task.FromException<PluginDevVideoSegment?>(new NotSupportedException("Host-bridge runtime does not expose video segment inspection yet."));

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

    public Task<IReadOnlyList<SearchItem>> EnrichSearchItemsAsync(IReadOnlyList<SearchItem> items, CancellationToken cancellationToken)
        => Task.FromException<IReadOnlyList<SearchItem>>(new InvalidOperationException(reason));

    public Task<IReadOnlyList<ChapterItem>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken)
        => Task.FromException<IReadOnlyList<ChapterItem>>(new InvalidOperationException(reason));

    public Task<PageItem?> GetPageAsync(string mediaId, string chapterId, int index, CancellationToken cancellationToken)
        => Task.FromException<PageItem?>(new InvalidOperationException(reason));

    public Task<IReadOnlyList<PageItem>> GetPagesAsync(string mediaId, string chapterId, int startIndex, int count, CancellationToken cancellationToken)
        => Task.FromException<IReadOnlyList<PageItem>>(new InvalidOperationException(reason));

    public Task<IReadOnlyList<PluginDevVideoStream>> GetVideoStreamsAsync(string mediaId, CancellationToken cancellationToken)
        => Task.FromException<IReadOnlyList<PluginDevVideoStream>>(new InvalidOperationException(reason));

    public Task<PluginDevVideoSegment?> GetVideoSegmentAsync(string mediaId, string streamId, int sequence, CancellationToken cancellationToken)
        => Task.FromException<PluginDevVideoSegment?>(new InvalidOperationException(reason));

    public Task<byte[]?> GetPageAssetAsync(string mediaId, string chapterId, CancellationToken cancellationToken)
        => Task.FromException<byte[]?>(new InvalidOperationException(reason));

    public Task<string> ReloadAsync(CancellationToken cancellationToken)
        => Task.FromException<string>(new InvalidOperationException(reason));
}

public sealed class DeferredWasmRuntimeAdapter(
    string rootDirectory,
    string runtimeLibraryPath,
    IReadOnlyList<string> permittedDomains,
    Func<string?> resolveComponentPath) : IPluginDevRuntimeAdapter, IPluginDevRuntimeLogSource
{
    private readonly Lock _gate = new();
    private WasmCliRuntimeAdapter? _inner;

    private WasmCliRuntimeAdapter GetOrCreateInner()
    {
        lock (_gate)
        {
            if (_inner is not null)
            {
                return _inner;
            }

            var componentPath = resolveComponentPath()
                ?? throw new InvalidOperationException("Direct WASM profile could not resolve a built .wasm component artifact. Run 'build' for the active profile first.");
            _inner = new WasmCliRuntimeAdapter(rootDirectory, componentPath, runtimeLibraryPath, permittedDomains);
            return _inner;
        }
    }

    public string Name => "wasm-native-direct";
    public bool SupportsReload => true;
    public bool SupportsPageAsset => false;
    public bool SupportsScenarios => true;

    public Task<IReadOnlyList<SearchItem>> SearchAsync(string query, CancellationToken cancellationToken)
        => GetOrCreateInner().SearchAsync(query, cancellationToken);

    public Task<IReadOnlyList<SearchItem>> EnrichSearchItemsAsync(IReadOnlyList<SearchItem> items, CancellationToken cancellationToken)
        => GetOrCreateInner().EnrichSearchItemsAsync(items, cancellationToken);

    public Task<IReadOnlyList<ChapterItem>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken)
        => GetOrCreateInner().GetChaptersAsync(mediaId, cancellationToken);

    public Task<PageItem?> GetPageAsync(string mediaId, string chapterId, int index, CancellationToken cancellationToken)
        => GetOrCreateInner().GetPageAsync(mediaId, chapterId, index, cancellationToken);

    public Task<IReadOnlyList<PageItem>> GetPagesAsync(string mediaId, string chapterId, int startIndex, int count, CancellationToken cancellationToken)
        => GetOrCreateInner().GetPagesAsync(mediaId, chapterId, startIndex, count, cancellationToken);

    public Task<IReadOnlyList<PluginDevVideoStream>> GetVideoStreamsAsync(string mediaId, CancellationToken cancellationToken)
        => GetOrCreateInner().GetVideoStreamsAsync(mediaId, cancellationToken);

    public Task<PluginDevVideoSegment?> GetVideoSegmentAsync(string mediaId, string streamId, int sequence, CancellationToken cancellationToken)
        => GetOrCreateInner().GetVideoSegmentAsync(mediaId, streamId, sequence, cancellationToken);

    public Task<byte[]?> GetPageAssetAsync(string mediaId, string chapterId, CancellationToken cancellationToken)
        => GetOrCreateInner().GetPageAssetAsync(mediaId, chapterId, cancellationToken);

    public Task<string> ReloadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult("WASM direct runtime is process-per-invocation; each command already runs against the latest project state.");
    }

    public IReadOnlyList<PluginDevRuntimeLogLine> DrainRuntimeLogs()
    {
        lock (_gate)
        {
            return _inner?.DrainRuntimeLogs() ?? [];
        }
    }
}

public sealed class NativeProcessRuntimeAdapter : IPluginDevRuntimeAdapter, IPluginDevRuntimeLogSource
{
    private const string HostAuthHeaderName = "x-emma-plugin-host-auth";
    private const string CorrelationIdHeaderName = "x-correlation-id";
    private const string EnrichSearchItemsPath = "/dev/search/enrich";
    private static readonly TimeSpan EntrypointResolveTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan EntrypointResolveRetryDelay = TimeSpan.FromMilliseconds(150);

    private readonly string _entryPointPath;
    private readonly string _entryPointFileName;
    private readonly string _entryPointDirectory;
    private readonly Uri _hostUri;
    private readonly PluginRuntimeTarget _target;
    private readonly PluginDevLoggingOptions _logging;
    private readonly string _authToken = Guid.NewGuid().ToString("n");
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private Process? _process;

    public NativeProcessRuntimeAdapter(
        string entryPointPath,
        Uri hostUri,
        PluginRuntimeTarget target,
        PluginDevLoggingOptions logging)
    {
        _entryPointPath = entryPointPath;
        _entryPointFileName = Path.GetFileName(entryPointPath);
        _entryPointDirectory = Path.GetDirectoryName(entryPointPath) ?? Environment.CurrentDirectory;
        _hostUri = hostUri;
        _target = target;
        _logging = logging;
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

    public async Task<IReadOnlyList<SearchItem>> EnrichSearchItemsAsync(IReadOnlyList<SearchItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        await EnsureRunningAsync(cancellationToken);
        using var httpClient = CreateAuthorizedHttpClient();
        using var response = await httpClient.PostAsJsonAsync(EnrichSearchItemsPath, new EnrichSearchItemsRequest(items), cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(payload)
                ? $"Search enrichment failed with HTTP {(int)response.StatusCode}."
                : payload.Trim());
        }

        return JsonSerializer.Deserialize(payload, PluginDevJsonContexts.Config.IReadOnlyListSearchItem)
            ?? [];
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

    public async Task<IReadOnlyList<PluginDevVideoStream>> GetVideoStreamsAsync(string mediaId, CancellationToken cancellationToken)
    {
        await EnsureRunningAsync(cancellationToken);

        using var httpClient = CreateGrpcHttpClient();
        using var channel = GrpcChannel.ForAddress(_hostUri, new GrpcChannelOptions { HttpClient = httpClient });
        var client = new PluginContracts.VideoProvider.VideoProviderClient(channel);
        var correlationId = Guid.NewGuid().ToString("n");
        var response = await client.GetStreamsAsync(new PluginContracts.StreamRequest
        {
            MediaId = mediaId,
            Context = CreatePluginRequestContext(correlationId)
        }, headers: CreateGrpcHeaders(correlationId), cancellationToken: cancellationToken);

        return response.Streams
            .Select(static stream => new PluginDevVideoStream(
                stream.Id ?? string.Empty,
                stream.Label ?? string.Empty,
                stream.PlaylistUri ?? string.Empty,
                stream.RequestHeaders?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase),
                string.IsNullOrWhiteSpace(stream.RequestCookies) ? null : stream.RequestCookies,
                string.IsNullOrWhiteSpace(stream.StreamType) ? null : stream.StreamType,
                stream.IsLive,
                stream.DrmProtected,
                string.IsNullOrWhiteSpace(stream.DrmScheme) ? null : stream.DrmScheme,
                stream.AudioTracks.Select(static track => new PluginDevVideoTrack(
                    track.Id ?? string.Empty,
                    track.Label ?? string.Empty,
                    string.IsNullOrWhiteSpace(track.Language) ? null : track.Language,
                    string.IsNullOrWhiteSpace(track.Codec) ? null : track.Codec,
                    track.IsDefault)).ToArray(),
                stream.SubtitleTracks.Select(static track => new PluginDevVideoTrack(
                    track.Id ?? string.Empty,
                    track.Label ?? string.Empty,
                    string.IsNullOrWhiteSpace(track.Language) ? null : track.Language,
                    string.IsNullOrWhiteSpace(track.Codec) ? null : track.Codec,
                    track.IsDefault)).ToArray(),
                string.IsNullOrWhiteSpace(stream.DefaultAudioTrackId) ? null : stream.DefaultAudioTrackId,
                string.IsNullOrWhiteSpace(stream.DefaultSubtitleTrackId) ? null : stream.DefaultSubtitleTrackId))
            .ToArray();
    }

    public async Task<PluginDevVideoSegment?> GetVideoSegmentAsync(string mediaId, string streamId, int sequence, CancellationToken cancellationToken)
    {
        await EnsureRunningAsync(cancellationToken);

        using var httpClient = CreateGrpcHttpClient();
        using var channel = GrpcChannel.ForAddress(_hostUri, new GrpcChannelOptions { HttpClient = httpClient });
        var client = new PluginContracts.VideoProvider.VideoProviderClient(channel);
        var correlationId = Guid.NewGuid().ToString("n");
        var response = await client.GetSegmentAsync(new PluginContracts.SegmentRequest
        {
            MediaId = mediaId,
            StreamId = streamId,
            Sequence = sequence,
            Context = CreatePluginRequestContext(correlationId)
        }, headers: CreateGrpcHeaders(correlationId), cancellationToken: cancellationToken);

        var payload = response.Payload.ToByteArray();
        return new PluginDevVideoSegment(
            string.IsNullOrWhiteSpace(response.ContentType) ? "application/octet-stream" : response.ContentType,
            Convert.ToBase64String(payload),
            payload.Length);
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

    public IReadOnlyList<PluginDevRuntimeLogLine> DrainRuntimeLogs()
    {
        var lines = new List<PluginDevRuntimeLogLine>();
        DrainBufferedLog(_stdout, "info", lines);
        DrainBufferedLog(_stderr, "error", lines);
        return lines;
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
        var resolvedEntryPointPath = WaitForCurrentEntrypointPath();
        if (string.IsNullOrWhiteSpace(resolvedEntryPointPath) || !File.Exists(resolvedEntryPointPath))
        {
            throw new InvalidOperationException($"Native runtime entrypoint was not found: {_entryPointPath}");
        }

        _stdout.Clear();
        _stderr.Clear();

        var port = _hostUri.Port.ToString(CultureInfo.InvariantCulture);
        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedEntryPointPath,
            WorkingDirectory = Path.GetDirectoryName(resolvedEntryPointPath) ?? _entryPointDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port);
        startInfo.Environment["EMMA_PLUGIN_PORT"] = port;
        startInfo.Environment["EMMA_TEST_PLUGIN_PORT"] = port;
        startInfo.Environment["EMMA_PLUGIN_HOST_AUTH_TOKEN"] = _authToken;
        startInfo.Environment["EMMA_PLUGIN_DEV_MODE"] = _logging.Plugin ? "1" : "0";
        startInfo.Environment["Logging__LogLevel__EMMA"] = _logging.Plugin ? "Information" : "None";
        startInfo.Environment["Logging__LogLevel__Microsoft.Hosting"] = _logging.AspNetHost ? "Information" : "None";
        startInfo.Environment["Logging__LogLevel__Microsoft.AspNetCore"] = _logging.AspNetHost ? "Information" : "None";
        startInfo.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = _logging.AspNetHost ? "Information" : "None";
        startInfo.Environment["Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics"] = _logging.AspNetHost ? "Information" : "None";
        startInfo.Environment["Logging__LogLevel__Microsoft.AspNetCore.Server.Kestrel"] = _logging.AspNetHost ? "Information" : "None";
        startInfo.Environment["Logging__LogLevel__System.Net.Http.HttpClient"] = _logging.HttpClient ? "Information" : "None";
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
            throw new InvalidOperationException($"Failed to start native runtime '{resolvedEntryPointPath}'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
    }

    private string? WaitForCurrentEntrypointPath()
    {
        var deadlineUtc = DateTime.UtcNow + EntrypointResolveTimeout;
        string? resolvedEntryPointPath;
        do
        {
            resolvedEntryPointPath = ResolveCurrentEntrypointPath();
            if (!string.IsNullOrWhiteSpace(resolvedEntryPointPath) && File.Exists(resolvedEntryPointPath))
            {
                return resolvedEntryPointPath;
            }

            Thread.Sleep(EntrypointResolveRetryDelay);
        }
        while (DateTime.UtcNow < deadlineUtc);

        return ResolveCurrentEntrypointPath();
    }

    private string? ResolveCurrentEntrypointPath()
    {
        if (File.Exists(_entryPointPath))
        {
            return _entryPointPath;
        }

        if (!Directory.Exists(_entryPointDirectory))
        {
            return null;
        }

        var directCandidate = Path.Combine(_entryPointDirectory, _entryPointFileName);
        if (File.Exists(directCandidate))
        {
            return directCandidate;
        }

        var discoveredCandidate = Directory.EnumerateFiles(_entryPointDirectory, _entryPointFileName, SearchOption.AllDirectories)
            .OrderByDescending(static file => File.GetLastWriteTimeUtc(file))
            .ThenBy(static file => file, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return discoveredCandidate;
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

    private static void DrainBufferedLog(StringBuilder builder, string level, List<PluginDevRuntimeLogLine> lines)
    {
        string[] drainedLines;
        lock (builder)
        {
            drainedLines = builder
                .ToString()
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            builder.Clear();
        }

        foreach (var line in drainedLines)
        {
            lines.Add(new PluginDevRuntimeLogLine(level, line));
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

    private HttpClient CreateAuthorizedHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true
        };

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = _hostUri,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(HostAuthHeaderName, _authToken);
        return httpClient;
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

    private sealed record EnrichSearchItemsRequest(IReadOnlyList<SearchItem> Items);
}

public sealed class WasmCliRuntimeAdapter : IPluginDevRuntimeAdapter, IPluginDevRuntimeLogSource
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _rootDirectory;
    private readonly string _componentPath;
    private readonly string _runtimeLibraryPath;
    private readonly IReadOnlyList<string> _permittedDomains;
    private readonly Lock _runtimeLogLock = new();
    private readonly List<PluginDevRuntimeLogLine> _runtimeLogs = [];

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

    public async Task<IReadOnlyList<SearchItem>> EnrichSearchItemsAsync(IReadOnlyList<SearchItem> items, CancellationToken cancellationToken)
    {
        var itemIds = items
            .Select(static item => item.id)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (itemIds.Length == 0)
        {
            return [];
        }

        return await InvokeTypedOperationAsync<IReadOnlyList<SearchItem>>(
            nestedOperation: "enrich-search-metadata",
            mediaId: null,
            mediaType: null,
            argsJson: JsonSerializer.Serialize(new { itemIds, baseItems = items }),
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

    public async Task<IReadOnlyList<PluginDevVideoStream>> GetVideoStreamsAsync(string mediaId, CancellationToken cancellationToken)
    {
        return await InvokeTypedOperationAsync<IReadOnlyList<PluginDevVideoStream>>(
            nestedOperation: PluginOperationNames.VideoStreams,
            mediaId: mediaId,
            mediaType: PluginMediaTypes.Video,
            argsJson: null,
            cancellationToken) ?? [];
    }

    public async Task<PluginDevVideoSegment?> GetVideoSegmentAsync(string mediaId, string streamId, int sequence, CancellationToken cancellationToken)
    {
        return await InvokeTypedOperationAsync<PluginDevVideoSegment>(
            nestedOperation: PluginOperationNames.VideoSegment,
            mediaId: mediaId,
            mediaType: PluginMediaTypes.Video,
            argsJson: JsonSerializer.Serialize(new { streamId, sequence }),
            cancellationToken);
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

        return await Task.Run(() =>
        {
            try
            {
                return NativeWasmRuntimeBindings.Invoke(_componentPath, operation, operationArgs, _permittedDomains);
            }
            finally
            {
                CaptureRuntimeLogs();
            }
        }, cancellationToken);
    }

    public IReadOnlyList<PluginDevRuntimeLogLine> DrainRuntimeLogs()
    {
        lock (_runtimeLogLock)
        {
            if (_runtimeLogs.Count == 0)
            {
                return [];
            }

            var drained = _runtimeLogs.ToArray();
            _runtimeLogs.Clear();
            return drained;
        }
    }

    private void CaptureRuntimeLogs()
    {
        var captured = NativeWasmRuntimeBindings.TakeLastStdio();
        if (captured.Count == 0)
        {
            return;
        }

        lock (_runtimeLogLock)
        {
            _runtimeLogs.AddRange(captured);
        }
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

    public static IReadOnlyList<PluginDevRuntimeLogLine> TakeLastStdio()
    {
        var ptr = TakeLastStdioNative();
        if (ptr == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            var json = PtrToString(ptr);
            if (string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            return JsonSerializer.Deserialize<PluginDevRuntimeLogLine[]>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
        }
        finally
        {
            FreeString(ptr);
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

    [DllImport("emma_wasm_runtime", EntryPoint = "emma_wasm_runtime_take_last_stdio", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr TakeLastStdioNative();
}

public sealed class PluginDevBuildService
{
    public PluginDevBuildPlan? GetBuildPlan(PluginDevSession session)
    {
        var plan = session.Profile.RuntimeTarget switch
        {
            PluginRuntimeTarget.Wasm => CreateWasmBuildPlan(session.Discovery.RootDirectory, ResolveProjectPathForTarget(session, PluginRuntimeTarget.Wasm), session.Profile.WasiSdkPath),
            PluginRuntimeTarget.Linux => CreateNativeBuildPlan(session.Discovery.RootDirectory, ResolveProjectPathForTarget(session, PluginRuntimeTarget.Linux), PluginRuntimeTarget.Linux),
            PluginRuntimeTarget.Windows => CreateNativeBuildPlan(session.Discovery.RootDirectory, ResolveProjectPathForTarget(session, PluginRuntimeTarget.Windows), PluginRuntimeTarget.Windows),
            _ => null
        };

        if (plan is null)
        {
            return null;
        }

        if (OperatingSystem.IsMacOS() && session.Profile.RuntimeTarget == PluginRuntimeTarget.Wasm)
        {
            return CreateMacWasmDockerBuildPlan(session, plan) ?? plan;
        }

        return plan;
    }

    private static PluginDevBuildPlan? CreateMacWasmDockerBuildPlan(PluginDevSession session, PluginDevBuildPlan fallbackPlan)
    {
        var rootDirectory = session.Discovery.RootDirectory;
        var scriptPath = Path.Combine(rootDirectory, "scripts", "build-pack-plugin-docker.sh");
        if (!File.Exists(scriptPath))
        {
            return null;
        }

        var manifestPath = session.Discovery.ManifestPath;
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            return null;
        }

        var command = BuildMacDockerBuildCommand(session, scriptPath, manifestPath);
        return new PluginDevBuildPlan(
            "wasm-build-docker",
            rootDirectory,
            "bash",
            ["-lc", command],
            fallbackPlan.ArtifactPath,
            "Normalized WASM Docker build plan selected automatically on macOS.");
    }

    private static string BuildMacDockerBuildCommand(PluginDevSession session, string scriptPath, string manifestPath)
    {
        var envAssignments = new List<string>();

        var dockerImage = Environment.GetEnvironmentVariable("EMMA_DOCKER_IMAGE");
        if (!string.IsNullOrWhiteSpace(dockerImage))
        {
            envAssignments.Add($"DOCKER_IMAGE={PluginDevProcessRunner.QuoteForShell(dockerImage)}");
        }

        var dockerPlatform = Environment.GetEnvironmentVariable("EMMA_DOCKER_PLATFORM");
        if (!string.IsNullOrWhiteSpace(dockerPlatform))
        {
            envAssignments.Add($"DOCKER_PLATFORM={PluginDevProcessRunner.QuoteForShell(dockerPlatform)}");
        }

        var dockerTargets = Environment.GetEnvironmentVariable("EMMA_DOCKER_TARGETS");
        if (string.IsNullOrWhiteSpace(dockerTargets))
        {
            dockerTargets = "wasm";
        }

        envAssignments.Add($"TARGETS={PluginDevProcessRunner.QuoteForShell(dockerTargets)}");

        var wasiSdkHostPath = Environment.GetEnvironmentVariable("EMMA_WASI_SDK_HOST_PATH");
        if (string.IsNullOrWhiteSpace(wasiSdkHostPath))
        {
            wasiSdkHostPath = session.Profile.WasiSdkPath;
        }

        if (!string.IsNullOrWhiteSpace(wasiSdkHostPath))
        {
            envAssignments.Add($"WASI_SDK_HOST_PATH={PluginDevProcessRunner.QuoteForShell(wasiSdkHostPath)}");
        }

        var envPrefix = envAssignments.Count == 0
            ? string.Empty
            : string.Join(" ", envAssignments) + " ";

        return $"{envPrefix}{PluginDevProcessRunner.QuoteForShell(scriptPath)} {PluginDevProcessRunner.QuoteForShell(manifestPath)}";
    }

    private static PluginDevBuildPlan? CreateWasmBuildPlan(string rootDirectory, string? projectPath, string? wasiSdkPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return null;
        }

        var publishDirectory = Path.Combine(rootDirectory, "artifacts", "build-wasm", "publish");
        var projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException($"Unable to resolve project directory for '{projectPath}'.");
        var quotedProjectDirectory = PluginDevProcessRunner.QuoteForShell(projectDirectory);
        var quotedProjectPath = PluginDevProcessRunner.QuoteForShell(projectPath);
        var quotedPublishDirectory = PluginDevProcessRunner.QuoteForShell(publishDirectory);
        var wasiSdkPrefix = string.IsNullOrWhiteSpace(wasiSdkPath)
            ? "WASI_SDK_PATH=\"${WASI_SDK_PATH:-}\""
            : $"WASI_SDK_PATH={PluginDevProcessRunner.QuoteForShell(wasiSdkPath)}";

        return new PluginDevBuildPlan(
            "wasm-build",
            rootDirectory,
            "bash",
            [
                "-lc",
                $"set -euo pipefail; rm -rf {quotedProjectDirectory}/bin {quotedProjectDirectory}/obj {quotedPublishDirectory}; mkdir -p {quotedPublishDirectory}; restore_log={quotedPublishDirectory}/restore.log; if ! dotnet restore {quotedProjectPath} --no-cache --force-evaluate --runtime wasi-wasm >\"$restore_log\" 2>&1; then cat \"$restore_log\"; exit 1; fi; publish_none_log={quotedPublishDirectory}/publish-nativecodegen-none.log; if ! {wasiSdkPrefix} dotnet publish {quotedProjectPath} -c Release -r wasi-wasm --self-contained true -p:PublishAot=false -p:NativeCodeGen=none -p:DebugType=None -p:DebugSymbols=false -p:WasmSingleFileBundle=true -o {quotedPublishDirectory} 2>&1 | tee \"$publish_none_log\"; then if grep -q 'native/.*\\.wasm\" because it was not found' \"$publish_none_log\"; then echo 'WASM publish produced no native artifact with NativeCodeGen=none; retrying with NativeCodeGen=llvm...'; {wasiSdkPrefix} dotnet publish {quotedProjectPath} -c Release -r wasi-wasm --self-contained true -p:PublishAot=false -p:NativeCodeGen=llvm -p:DebugType=None -p:DebugSymbols=false -p:WasmSingleFileBundle=true -o {quotedPublishDirectory}; else cat \"$publish_none_log\"; exit 1; fi; fi"
            ],
            publishDirectory,
            "Normalized WASM publish plan for CLI-driven plugin development.");
    }

    private static PluginDevBuildPlan? CreateNativeBuildPlan(string rootDirectory, string? projectPath, PluginRuntimeTarget target)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return null;
        }

        var runtimeIdentifier = target == PluginRuntimeTarget.Windows ? "win-x64" : "linux-x64";
        var publishDirectory = Path.Combine(rootDirectory, "artifacts", $"build-{runtimeIdentifier}", "publish");
        var quotedProjectPath = PluginDevProcessRunner.QuoteForShell(projectPath);
        var quotedPublishDirectory = PluginDevProcessRunner.QuoteForShell(publishDirectory);

        return new PluginDevBuildPlan(
            $"{target.ToString().ToLowerInvariant()}-native-build",
            rootDirectory,
            "bash",
            [
                "-lc",
                $"set -euo pipefail; rm -rf {quotedPublishDirectory}; mkdir -p {quotedPublishDirectory}; dotnet publish {quotedProjectPath} -c Release -r {runtimeIdentifier} --self-contained false -p:UseAppHost=true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o {quotedPublishDirectory}; find {quotedPublishDirectory} -maxdepth 1 -type f \\( -name '*.pdb' -o -name '*.dbg' -o -name '*.xml' \\) -delete || true; rm -f {quotedPublishDirectory}/createdump"
            ],
            publishDirectory,
            $"Normalized {target} native publish plan for CLI-driven plugin development.");
    }

    private static string? ResolveProjectPathForTarget(PluginDevSession session, PluginRuntimeTarget target)
    {
        var rootDirectory = session.Discovery.RootDirectory;
        var manifestStem = string.IsNullOrWhiteSpace(session.Discovery.ManifestPath)
            ? null
            : Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(session.Discovery.ManifestPath));

        if (target == PluginRuntimeTarget.Wasm)
        {
            if (!string.IsNullOrWhiteSpace(manifestStem))
            {
                var exactWasmProject = Path.Combine(rootDirectory, $"{manifestStem}.Wasm.csproj");
                if (File.Exists(exactWasmProject))
                {
                    return exactWasmProject;
                }
            }

            return Directory.EnumerateFiles(rootDirectory, "*.Wasm.csproj", SearchOption.TopDirectoryOnly)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(manifestStem))
        {
            var exactHostProject = Path.Combine(rootDirectory, $"{manifestStem}.csproj");
            if (File.Exists(exactHostProject))
            {
                return exactHostProject;
            }
        }

        return session.Discovery.ProjectFilePath;
    }

    public async Task<string> BuildAsync(PluginDevSession session, CancellationToken cancellationToken)
    {
        var plan = GetBuildPlan(session)
            ?? throw new InvalidOperationException("No normalized build plan is available for the active profile.");

        var result = await PluginDevProcessRunner.RunAsync(plan.WorkingDirectory, plan.Command, plan.Arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            var userFacingOutput = PluginDevBuildException.FormatProcessOutput(result);
            var detailedOutput = PluginDevBuildException.FormatDetailedProcessOutput(result);
            throw new PluginDevBuildException(
                plan.Name,
                result.ExitCode,
                userFacingOutput,
                detailedOutput);
        }

        return string.IsNullOrWhiteSpace(result.StandardOutput)
            ? $"Build completed for profile '{session.Profile.Name}'."
            : result.StandardOutput.Trim();
    }

    public string? SyncBuildArtifacts(PluginDevSession session)
    {
        var sync = session.Profile.Sync;
        if (!sync.Enabled || !sync.OnBuild)
        {
            return null;
        }

        var plan = GetBuildPlan(session)
            ?? throw new InvalidOperationException("Cannot sync build outputs because no normalized build plan is available for the active profile.");
        if (string.IsNullOrWhiteSpace(plan.ArtifactPath))
        {
            throw new InvalidOperationException($"Cannot sync build outputs for plan '{plan.Name}' because it does not declare an artifact path.");
        }

        var sourcePath = Path.GetFullPath(plan.ArtifactPath);
        var destinationPath = sync.DestinationPath
            ?? throw new InvalidOperationException("Cannot sync build outputs because no sync destination path is configured.");
        destinationPath = Path.GetFullPath(destinationPath);

        var manifestPath = session.Discovery.ManifestPath;
        var pluginId = TryReadPluginId(manifestPath);
        var syncMessages = new List<string>();

        var installLayout = ResolveInstalledSyncLayout(destinationPath, session.Profile.RuntimeTarget);

        if (session.Profile.RuntimeTarget == PluginRuntimeTarget.Wasm)
        {
            var wasmArtifactPath = ResolveWasmArtifactPath(session)
                ?? throw new InvalidOperationException("Cannot sync build outputs because no built WASM component artifact could be resolved.");

            var pluginRootPath = installLayout?.PluginRootPath;
            var artifactDestinationDirectory = installLayout?.ArtifactDirectoryPath ?? destinationPath;

            if (sync.CleanDestination && !string.IsNullOrWhiteSpace(pluginRootPath) && Directory.Exists(pluginRootPath))
            {
                Directory.Delete(pluginRootPath, recursive: true);
            }
            else if (sync.CleanDestination && Directory.Exists(destinationPath))
            {
                Directory.Delete(destinationPath, recursive: true);
            }

            Directory.CreateDirectory(artifactDestinationDirectory);
            var artifactDestinationPath = Path.Combine(artifactDestinationDirectory, "plugin.wasm");
            File.Copy(wasmArtifactPath, artifactDestinationPath, overwrite: true);
            syncMessages.Add($"Synced WASM artifact '{wasmArtifactPath}' to '{artifactDestinationPath}'.");

            TrySyncManifest(manifestPath, pluginId, installLayout?.ManifestsDirectoryPath, syncMessages);
            return string.Join("\n", syncMessages);
        }

        if (File.Exists(sourcePath))
        {
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Copy(sourcePath, destinationPath, overwrite: true);
            syncMessages.Add($"Synced build artifact '{sourcePath}' to '{destinationPath}'.");
            TrySyncManifest(manifestPath, pluginId, installLayout?.ManifestsDirectoryPath, syncMessages);
            return string.Join("\n", syncMessages);
        }

        if (!Directory.Exists(sourcePath))
        {
            throw new InvalidOperationException($"Cannot sync build outputs because the built artifact path does not exist: {sourcePath}");
        }

        var directoryDestinationPath = installLayout?.PluginRootPath ?? destinationPath;

        if (sync.CleanDestination && Directory.Exists(directoryDestinationPath))
        {
            Directory.Delete(directoryDestinationPath, recursive: true);
        }

        CopyDirectoryContents(sourcePath, directoryDestinationPath);
        syncMessages.Add($"Synced build outputs from '{sourcePath}' to '{directoryDestinationPath}'.");
        TrySyncManifest(manifestPath, pluginId, installLayout?.ManifestsDirectoryPath, syncMessages);
        return string.Join("\n", syncMessages);
    }

    public PluginDevPackResult PackCurrentProfile(PluginDevSession session)
    {
        return session.Profile.RuntimeTarget switch
        {
            PluginRuntimeTarget.Wasm => PackWasm(session),
            PluginRuntimeTarget.Linux => PackNative(session, "linux-x64"),
            PluginRuntimeTarget.Windows => PackNative(session, "win-x64"),
            _ => throw new InvalidOperationException($"Packing is not implemented for runtime target '{session.Profile.RuntimeTarget}'.")
        };
    }

    public string GetPackDirectory(PluginDevSession session)
    {
        return Path.Combine(session.Discovery.RootDirectory, "artifacts", "pack");
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
        return new PluginDevPackResult(zipPath, manifestOutPath, artifactOutPath, Path.GetDirectoryName(zipPath) ?? packageRoot);
    }

    public PluginDevPackResult PackNative(PluginDevSession session, string targetMoniker)
    {
        if (session.Profile.RuntimeTarget is not (PluginRuntimeTarget.Linux or PluginRuntimeTarget.Windows))
        {
            throw new InvalidOperationException("The normalized native pack flow is only implemented for Linux and Windows profiles.");
        }

        var manifestPath = session.Discovery.ManifestPath;
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            throw new InvalidOperationException("Cannot pack a native plugin without a discovered plugin manifest.");
        }

        var artifactDirectory = ResolveNativeArtifactDirectory(session)
            ?? throw new InvalidOperationException("No published native artifact directory could be resolved for packing. Run 'build' for the active profile first.");

        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var pluginId = manifestDoc.RootElement.GetProperty("id").GetString() ?? "plugin";
        var version = manifestDoc.RootElement.GetProperty("version").GetString() ?? "0.0.0";

        var packDirectory = GetPackDirectory(session);
        var packageRoot = Path.Combine(packDirectory, $"{version}-{targetMoniker}");
        var manifestOutDir = Path.Combine(packageRoot, "manifest");
        var pluginOutDir = Path.Combine(packageRoot, pluginId);
        var zipPath = Path.Combine(packDirectory, $"{pluginId}_{version}_{targetMoniker}.zip");

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
        CopyDirectoryContents(artifactDirectory, pluginOutDir);

        ZipFile.CreateFromDirectory(packageRoot, zipPath);
        return new PluginDevPackResult(zipPath, manifestOutPath, pluginOutDir, packDirectory);
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

    public string? ResolveNativeArtifactDirectory(PluginDevSession session)
    {
        if (session.Profile.RuntimeTarget is not (PluginRuntimeTarget.Linux or PluginRuntimeTarget.Windows))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(session.Profile.ArtifactPath) && Directory.Exists(session.Profile.ArtifactPath))
        {
            return session.Profile.ArtifactPath;
        }

        foreach (var candidate in session.Discovery.ArtifactCandidates.Where(candidate => candidate.Target == session.Profile.RuntimeTarget && Directory.Exists(candidate.Path)))
        {
            return candidate.Path;
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

    private static void CopyDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static void TrySyncManifest(string? manifestPath, string? pluginId, string? manifestsDirectoryPath, List<string> messages)
    {
        if (string.IsNullOrWhiteSpace(manifestPath)
            || string.IsNullOrWhiteSpace(pluginId)
            || string.IsNullOrWhiteSpace(manifestsDirectoryPath)
            || !File.Exists(manifestPath))
        {
            return;
        }

        Directory.CreateDirectory(manifestsDirectoryPath);
        var manifestDestinationPath = Path.Combine(manifestsDirectoryPath, pluginId + ".json");
        File.Copy(manifestPath, manifestDestinationPath, overwrite: true);
        messages.Add($"Synced manifest '{manifestPath}' to '{manifestDestinationPath}'.");
    }

    private static string? TryReadPluginId(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            return null;
        }

        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return manifestDocument.RootElement.TryGetProperty("id", out var idProperty)
            ? idProperty.GetString()
            : null;
    }

    private static PluginDevInstalledSyncLayout? ResolveInstalledSyncLayout(string destinationPath, PluginRuntimeTarget runtimeTarget)
    {
        var normalizedDestinationPath = Path.GetFullPath(destinationPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var destinationDirectoryInfo = new DirectoryInfo(normalizedDestinationPath);

        DirectoryInfo? pluginRootDirectory = destinationDirectoryInfo;
        if (runtimeTarget == PluginRuntimeTarget.Wasm
            && string.Equals(destinationDirectoryInfo.Name, "wasm", StringComparison.OrdinalIgnoreCase)
            && destinationDirectoryInfo.Parent is not null)
        {
            pluginRootDirectory = destinationDirectoryInfo.Parent;
        }

        if (pluginRootDirectory?.Parent is null
            || !string.Equals(pluginRootDirectory.Parent.Name, "plugins", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var pluginsDirectory = pluginRootDirectory.Parent;
        var emmaRootDirectory = pluginsDirectory.Parent;
        if (emmaRootDirectory is null)
        {
            return null;
        }

        var manifestsDirectoryPath = Path.Combine(emmaRootDirectory.FullName, "manifests");
        var artifactDirectoryPath = runtimeTarget == PluginRuntimeTarget.Wasm
            ? Path.Combine(pluginRootDirectory.FullName, "wasm")
            : pluginRootDirectory.FullName;

        return new PluginDevInstalledSyncLayout(pluginRootDirectory.FullName, artifactDirectoryPath, manifestsDirectoryPath);
    }

    private sealed record PluginDevInstalledSyncLayout(
        string PluginRootPath,
        string ArtifactDirectoryPath,
        string ManifestsDirectoryPath);
}

public static class PluginDevRuntimeLibraryResolver
{
    private const string RuntimeLibraryPathEnvironmentVariable = "EMMA_WASM_RUNTIME_LIBRARY_PATH";

    public static string Resolve(string workingDirectory)
    {
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

        var overridePath = Environment.GetEnvironmentVariable(RuntimeLibraryPathEnvironmentVariable)?.Trim();
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var resolvedOverridePath = Path.GetFullPath(overridePath);
            if (!File.Exists(resolvedOverridePath))
            {
                throw new InvalidOperationException($"Native WASM runtime library override '{RuntimeLibraryPathEnvironmentVariable}' points to a missing file: {resolvedOverridePath}");
            }

            return resolvedOverridePath;
        }

        foreach (var candidate in EnumeratePackagedRuntimeCandidates(platformDir, libraryFileName))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var root = FindRepoRoot(workingDirectory)
            ?? FindRepoRoot(AppContext.BaseDirectory)
            ?? FindRepoRoot(Path.GetDirectoryName(typeof(PluginDevRuntimeLibraryResolver).Assembly.Location) ?? string.Empty)
            ?? throw new InvalidOperationException(
                "Unable to locate the EMMA repository root or a packaged native WASM runtime sidecar while resolving the native WASM runtime library.");

        var path = Path.Combine(root, "artifacts", "wasm-runtime-native", platformDir, libraryFileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Native WASM runtime library was not found. Checked packaged sidecars next to the CLI and repo artifact path '{path}'. "
                + $"Set {RuntimeLibraryPathEnvironmentVariable} to override the resolver when distributing the CLI separately.");
        }

        return path;
    }

    private static IEnumerable<string> EnumeratePackagedRuntimeCandidates(string platformDir, string libraryFileName)
    {
        var baseDirectories = new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(PluginDevRuntimeLibraryResolver).Assembly.Location) ?? string.Empty
        };

        foreach (var baseDirectory in baseDirectories.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
        {
            yield return Path.Combine(baseDirectory, "runtimes", "wasm-runtime-native", platformDir, libraryFileName);
            yield return Path.Combine(baseDirectory, "wasm-runtime-native", platformDir, libraryFileName);
        }
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
    private static readonly Regex TemplatePattern = new("\\{\\{(?<expr>[^{}]+)\\}\\}", RegexOptions.Compiled);
    private static readonly IReadOnlyDictionary<string, string[]> AllowedParametersByOperation = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["search"] = ["query"],
        ["enrich"] = ["from"],
        ["chapters"] = ["mediaId"],
        ["page"] = ["mediaId", "chapterId", "index"],
        ["pages"] = ["mediaId", "chapterId", "startIndex", "count"],
        ["videostreams"] = ["mediaId"],
        ["videosegment"] = ["mediaId", "streamId", "sequence"],
        ["selectfirst"] = ["from"],
        ["selectat"] = ["from", "index"],
        ["requirecount"] = ["from", "min", "max"],
        ["requirenotnull"] = ["value", "message"],
        ["set"] = ["value"],
        ["log"] = ["message"],
        ["placeholder"] = ["message"]
    };
    private static readonly IReadOnlyDictionary<string, string[]> RequiredParametersByOperation = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["search"] = ["query"],
        ["enrich"] = ["from"],
        ["chapters"] = ["mediaId"],
        ["page"] = ["mediaId", "chapterId", "index"],
        ["pages"] = ["mediaId", "chapterId", "startIndex", "count"],
        ["videostreams"] = ["mediaId"],
        ["videosegment"] = ["mediaId", "streamId", "sequence"],
        ["selectfirst"] = ["from"],
        ["selectat"] = ["from", "index"],
        ["requirecount"] = ["from"],
        ["requirenotnull"] = ["value"],
        ["set"] = ["value"],
        ["log"] = ["message"],
        ["placeholder"] = ["message"]
    };
    private static readonly IReadOnlyList<PluginDevScenarioDefinition> SupportedScenarios =
    [
        new PluginDevScenarioDefinition(
            "paged-smoke",
            "Paged Smoke",
            "Runs search, chapter lookup, and first-page fetch for a fast end-to-end paged-media sanity check.",
            "naruto",
            true,
            "Search query"),
        new PluginDevScenarioDefinition(
            "search-smoke",
            "Search Smoke",
            "Runs only the search step so provider query and result mapping can be checked in isolation.",
            "naruto",
            true,
            "Search query"),
        new PluginDevScenarioDefinition(
            "chapters-smoke",
            "Chapters Smoke",
            "Runs search and chapter lookup without fetching page content, which is useful when narrowing failures to chapter enumeration.",
            "naruto",
            true,
            "Search query"),
        new PluginDevScenarioDefinition(
            "video-smoke",
            "Video Smoke",
            "Exercises video stream lookup for the active runtime so video transport wiring can be validated even before a richer fixture library exists.",
            "demo-video-1",
            true,
            "Video media id"),
        new PluginDevScenarioDefinition(
            "audio-placeholder",
            "Audio Placeholder",
            "Reserved for future audio-media scenario coverage. This currently documents the planned surface without claiming support.",
            null,
            false,
            "Query"),
        new PluginDevScenarioDefinition(
            "text-paged-placeholder",
            "Text-Paged Placeholder",
            "Reserved for future text-based paged media scenario coverage. This currently documents the planned surface without claiming support.",
            null,
            false,
            "Query")
    ];

    public IReadOnlyList<PluginDevDiagnostic> LintConfiguredScenarios(IReadOnlyList<PluginDevConfiguredScenario> scenarios)
    {
        var diagnostics = new List<PluginDevDiagnostic>();

        foreach (var scenario in scenarios)
        {
            var knownSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "query",
                "profile",
                "pluginId",
                "runtimeTarget",
                "executionMode"
            };

            if (SupportedScenarios.Any(item => string.Equals(item.Name, scenario.Name, StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(new PluginDevDiagnostic(
                    $"scenario.lint.{scenario.Name}.shadowing_builtin",
                    $"Custom scenario '{scenario.Name}' overrides a built-in scenario name. The custom scenario will be used.",
                    PluginDevDiagnosticSeverity.Warning,
                    "scenarios"));
            }

            for (var index = 0; index < scenario.Steps.Count; index++)
            {
                var step = scenario.Steps[index];
                var normalizedOp = step.Op.Trim().ToLowerInvariant();
                var stepOrdinal = index + 1;

                if (!AllowedParametersByOperation.ContainsKey(normalizedOp))
                {
                    diagnostics.Add(new PluginDevDiagnostic(
                        $"scenario.lint.{scenario.Name}.step_{stepOrdinal}.unknown_op",
                        $"Scenario '{scenario.Name}' step {stepOrdinal} uses unknown op '{step.Op}'. Supported ops: {string.Join(", ", AllowedParametersByOperation.Keys.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase))}.",
                        PluginDevDiagnosticSeverity.Warning,
                        "scenarios"));
                    continue;
                }

                var requiredParameters = RequiredParametersByOperation[normalizedOp];
                foreach (var requiredParameter in requiredParameters)
                {
                    if (!step.Parameters.Keys.Any(key => string.Equals(key, requiredParameter, StringComparison.OrdinalIgnoreCase)))
                    {
                        diagnostics.Add(new PluginDevDiagnostic(
                            $"scenario.lint.{scenario.Name}.step_{stepOrdinal}.missing_{requiredParameter.ToLowerInvariant()}",
                            $"Scenario '{scenario.Name}' step {stepOrdinal} ('{step.Op}') is missing required parameter '{requiredParameter}'.",
                            PluginDevDiagnosticSeverity.Warning,
                            "scenarios"));
                    }
                }

                var allowedParameters = AllowedParametersByOperation[normalizedOp];
                foreach (var parameterKey in step.Parameters.Keys)
                {
                    if (!allowedParameters.Any(value => string.Equals(value, parameterKey, StringComparison.OrdinalIgnoreCase)))
                    {
                        diagnostics.Add(new PluginDevDiagnostic(
                            $"scenario.lint.{scenario.Name}.step_{stepOrdinal}.unknown_param_{parameterKey.ToLowerInvariant()}",
                            $"Scenario '{scenario.Name}' step {stepOrdinal} ('{step.Op}') uses unknown parameter '{parameterKey}'. Allowed parameters: {string.Join(", ", allowedParameters)}.",
                            PluginDevDiagnosticSeverity.Warning,
                            "scenarios"));
                    }
                }

                if (string.Equals(normalizedOp, "set", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(step.Save))
                {
                    diagnostics.Add(new PluginDevDiagnostic(
                        $"scenario.lint.{scenario.Name}.step_{stepOrdinal}.missing_save",
                        $"Scenario '{scenario.Name}' step {stepOrdinal} ('set') should declare 'save' so the assigned value can be referenced later.",
                        PluginDevDiagnosticSeverity.Warning,
                        "scenarios"));
                }

                if (!string.IsNullOrWhiteSpace(step.Save) && knownSymbols.Contains(step.Save.Trim()))
                {
                    AddScenarioLintWarning(
                        diagnostics,
                        scenario.Name,
                        stepOrdinal,
                        step,
                        "overwrites_symbol",
                        $"Scenario '{scenario.Name}' step {stepOrdinal} overwrites previously known symbol '{step.Save.Trim()}'.");
                }

                foreach (var parameter in step.Parameters)
                {
                    foreach (var expression in ExtractExpressions(parameter.Value))
                    {
                        var rootSymbol = ExtractRootSymbol(expression);
                        if (string.IsNullOrWhiteSpace(rootSymbol))
                        {
                            continue;
                        }

                        if (!knownSymbols.Contains(rootSymbol))
                        {
                            AddScenarioLintWarning(
                                diagnostics,
                                scenario.Name,
                                stepOrdinal,
                                step,
                                $"unknown_symbol_{rootSymbol.ToLowerInvariant()}",
                                $"Scenario '{scenario.Name}' step {stepOrdinal} references unknown symbol '{rootSymbol}' in parameter '{parameter.Key}'. Known symbols at this point: {string.Join(", ", knownSymbols.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase))}.");
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(step.Save))
                {
                    knownSymbols.Add(step.Save.Trim());
                }
            }
        }

        return diagnostics;
    }

    private static void AddScenarioLintWarning(
        List<PluginDevDiagnostic> diagnostics,
        string scenarioName,
        int stepOrdinal,
        PluginDevScenarioStep step,
        string warningCode,
        string message)
    {
        if (IsScenarioLintWarningSuppressed(step, warningCode))
        {
            return;
        }

        diagnostics.Add(new PluginDevDiagnostic(
            $"scenario.lint.{scenarioName}.step_{stepOrdinal}.{warningCode}",
            message,
            PluginDevDiagnosticSeverity.Warning,
            "scenarios"));
    }

    private static bool IsScenarioLintWarningSuppressed(PluginDevScenarioStep step, string warningCode)
    {
        foreach (var suppressed in step.NoWarn)
        {
            if (string.IsNullOrWhiteSpace(suppressed))
            {
                continue;
            }

            var normalized = suppressed.Trim();
            if (string.Equals(normalized, warningCode, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (warningCode.StartsWith(normalized + "_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<PluginDevScenarioDefinition> GetAvailableScenarios(PluginDevSession session)
    {
        if (!session.RuntimeAdapter.SupportsScenarios)
        {
            return [];
        }

        var custom = session.ConfiguredScenarios
            .Select(static scenario => new PluginDevScenarioDefinition(
                scenario.Name,
                scenario.DisplayName,
                scenario.Description,
                scenario.DefaultQuery,
                scenario.SupportsQuery,
                scenario.QueryLabel))
            .ToArray();
        if (custom.Length == 0)
        {
            return SupportedScenarios;
        }

        var builtIns = SupportedScenarios
            .Where(definition => !custom.Any(customScenario => string.Equals(customScenario.Name, definition.Name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return [.. custom, .. builtIns];
    }

    public async Task<PluginDevScenarioResult> RunAsync(PluginDevSession session, string scenarioName, string? query, CancellationToken cancellationToken)
    {
        var normalizedScenario = (scenarioName ?? string.Empty).Trim().ToLowerInvariant();
        var customScenario = session.ConfiguredScenarios.FirstOrDefault(scenario => string.Equals(scenario.Name, normalizedScenario, StringComparison.OrdinalIgnoreCase));
        if (customScenario is not null)
        {
            return await RunConfiguredScenarioAsync(session, customScenario, query, cancellationToken);
        }

        return normalizedScenario switch
        {
            "search-smoke" => await RunSearchSmokeAsync(session, string.IsNullOrWhiteSpace(query) ? "naruto" : query.Trim(), cancellationToken),
            "chapters-smoke" => await RunChaptersSmokeAsync(session, string.IsNullOrWhiteSpace(query) ? "naruto" : query.Trim(), cancellationToken),
            "paged-smoke" => await RunPagedSmokeAsync(session, string.IsNullOrWhiteSpace(query) ? "naruto" : query.Trim(), cancellationToken),
            "video-smoke" => await RunVideoSmokeAsync(session, string.IsNullOrWhiteSpace(query) ? "demo-video-1" : query.Trim(), cancellationToken),
            "audio-placeholder" => new PluginDevScenarioResult("audio-placeholder", false, ["Audio scenario support is intentionally placeholder-only right now."]),
            "text-paged-placeholder" => new PluginDevScenarioResult("text-paged-placeholder", false, ["Text-based paged media scenario support is intentionally placeholder-only right now."]),
            _ => new PluginDevScenarioResult(normalizedScenario, false, [$"Unknown scenario '{scenarioName}'. Supported scenarios: {string.Join(", ", GetAvailableScenarios(session).Select(static item => item.Name))}"])
        };
    }

    private static async Task<PluginDevScenarioResult> RunConfiguredScenarioAsync(PluginDevSession session, PluginDevConfiguredScenario scenario, string? queryOverride, CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        var runtime = session.RuntimeAdapter;
        var variables = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["query"] = string.IsNullOrWhiteSpace(queryOverride) ? scenario.DefaultQuery ?? string.Empty : queryOverride.Trim(),
            ["profile"] = session.Profile.Name,
            ["pluginId"] = session.Profile.PluginId,
            ["runtimeTarget"] = session.Profile.RuntimeTarget.ToString(),
            ["executionMode"] = session.Profile.ExecutionMode.ToString()
        };

        try
        {
            foreach (var step in scenario.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var op = step.Op.Trim().ToLowerInvariant();
                switch (op)
                {
                    case "search":
                    {
                        var searchQuery = ResolveRequiredString(step, "query", variables);
                        var result = await runtime.SearchAsync(searchQuery, cancellationToken);
                        SaveValue(variables, step.Save, result);
                        messages.Add($"Search('{searchQuery}') returned {result.Count} item(s).");
                        break;
                    }
                    case "enrich":
                    {
                        var input = ResolveRequiredObject(step, "from", variables);
                        var inputItems = CoerceSearchItems(input);
                        var result = await runtime.EnrichSearchItemsAsync(inputItems, cancellationToken);
                        object? savedValue = input is SearchItem ? result.FirstOrDefault() : result;
                        SaveValue(variables, step.Save, savedValue);
                        messages.Add($"Enrich resolved {result.Count} item(s) from '{ResolveRequiredString(step, "from", variables)}'.");
                        break;
                    }
                    case "chapters":
                    {
                        var mediaId = ResolveRequiredString(step, "mediaId", variables);
                        var result = await runtime.GetChaptersAsync(mediaId, cancellationToken);
                        SaveValue(variables, step.Save, result);
                        messages.Add($"Chapters('{mediaId}') returned {result.Count} item(s).");
                        break;
                    }
                    case "page":
                    {
                        var mediaId = ResolveRequiredString(step, "mediaId", variables);
                        var chapterId = ResolveRequiredString(step, "chapterId", variables);
                        var index = ResolveRequiredInt(step, "index", variables);
                        var result = await runtime.GetPageAsync(mediaId, chapterId, index, cancellationToken);
                        SaveValue(variables, step.Save, result);
                        messages.Add(result is null
                            ? $"Page('{mediaId}', '{chapterId}', {index}) returned no page."
                            : $"Page('{mediaId}', '{chapterId}', {index}) resolved '{result.contentUri}'.");
                        break;
                    }
                    case "pages":
                    {
                        var mediaId = ResolveRequiredString(step, "mediaId", variables);
                        var chapterId = ResolveRequiredString(step, "chapterId", variables);
                        var startIndex = ResolveRequiredInt(step, "startIndex", variables);
                        var count = ResolveRequiredInt(step, "count", variables);
                        var result = await runtime.GetPagesAsync(mediaId, chapterId, startIndex, count, cancellationToken);
                        SaveValue(variables, step.Save, result);
                        messages.Add($"Pages('{mediaId}', '{chapterId}', {startIndex}, {count}) returned {result.Count} item(s).");
                        break;
                    }
                    case "videostreams":
                    {
                        var mediaId = ResolveRequiredString(step, "mediaId", variables);
                        var result = await runtime.GetVideoStreamsAsync(mediaId, cancellationToken);
                        SaveValue(variables, step.Save, result);
                        messages.Add($"VideoStreams('{mediaId}') returned {result.Count} item(s).");
                        break;
                    }
                    case "videosegment":
                    {
                        var mediaId = ResolveRequiredString(step, "mediaId", variables);
                        var streamId = ResolveRequiredString(step, "streamId", variables);
                        var sequence = ResolveRequiredInt(step, "sequence", variables);
                        var result = await runtime.GetVideoSegmentAsync(mediaId, streamId, sequence, cancellationToken);
                        SaveValue(variables, step.Save, result);
                        messages.Add(result is null
                            ? $"VideoSegment('{mediaId}', '{streamId}', {sequence}) returned no payload."
                            : $"VideoSegment('{mediaId}', '{streamId}', {sequence}) returned {result.SizeBytes} byte(s) as '{result.ContentType}'.");
                        break;
                    }
                    case "selectfirst":
                    {
                        var collection = ResolveRequiredCollection(step, "from", variables);
                        if (collection.Count == 0)
                        {
                            messages.Add($"SelectFirst from '{ResolveRequiredString(step, "from", variables)}' failed because the collection was empty.");
                            return new PluginDevScenarioResult(scenario.Name, false, messages);
                        }

                        var result = collection[0];
                        SaveValue(variables, step.Save, result);
                        messages.Add($"Selected first item: {StringifyValue(result)}.");
                        break;
                    }
                    case "selectat":
                    {
                        var collection = ResolveRequiredCollection(step, "from", variables);
                        var index = ResolveRequiredInt(step, "index", variables);
                        if (index < 0 || index >= collection.Count)
                        {
                            messages.Add($"SelectAt failed because index {index} is outside the collection range 0..{collection.Count - 1}.");
                            return new PluginDevScenarioResult(scenario.Name, false, messages);
                        }

                        var result = collection[index];
                        SaveValue(variables, step.Save, result);
                        messages.Add($"Selected item at index {index}: {StringifyValue(result)}.");
                        break;
                    }
                    case "requirecount":
                    {
                        var collection = ResolveRequiredCollection(step, "from", variables);
                        var min = ResolveOptionalInt(step, "min", variables);
                        var max = ResolveOptionalInt(step, "max", variables);
                        if (min is not null && collection.Count < min.Value)
                        {
                            messages.Add($"RequireCount failed: expected at least {min.Value} item(s), but found {collection.Count}.");
                            return new PluginDevScenarioResult(scenario.Name, false, messages);
                        }

                        if (max is not null && collection.Count > max.Value)
                        {
                            messages.Add($"RequireCount failed: expected at most {max.Value} item(s), but found {collection.Count}.");
                            return new PluginDevScenarioResult(scenario.Name, false, messages);
                        }

                        messages.Add($"RequireCount passed with {collection.Count} item(s).");
                        break;
                    }
                    case "requirenotnull":
                    {
                        var value = ResolveRequiredObject(step, "value", variables);
                        if (value is null)
                        {
                            var failureMessage = ResolveOptionalString(step, "message", variables) ?? "RequireNotNull failed because the resolved value was null.";
                            messages.Add(failureMessage);
                            return new PluginDevScenarioResult(scenario.Name, false, messages);
                        }

                        SaveValue(variables, step.Save, value);
                        messages.Add($"RequireNotNull passed for '{ResolveRequiredString(step, "value", variables)}'.");
                        break;
                    }
                    case "set":
                    {
                        var value = ResolveRequiredObject(step, "value", variables);
                        if (string.IsNullOrWhiteSpace(step.Save))
                        {
                            throw new InvalidOperationException("Scenario step 'set' requires a save target.");
                        }

                        SaveValue(variables, step.Save, value);
                        messages.Add($"Set '{step.Save}' to {StringifyValue(value)}.");
                        break;
                    }
                    case "log":
                    {
                        var message = ResolveRequiredString(step, "message", variables);
                        messages.Add(message);
                        break;
                    }
                    case "placeholder":
                    {
                        var message = ResolveRequiredString(step, "message", variables);
                        messages.Add(message);
                        return new PluginDevScenarioResult(scenario.Name, false, messages);
                    }
                    default:
                        throw new InvalidOperationException($"Scenario '{scenario.Name}' uses unsupported op '{step.Op}'.");
                }
            }

            return new PluginDevScenarioResult(scenario.Name, true, messages);
        }
        catch (Exception ex)
        {
            messages.Add($"Scenario '{scenario.Name}' failed: {ex.Message}");
            return new PluginDevScenarioResult(scenario.Name, false, messages);
        }
    }

    private static async Task<PluginDevScenarioResult> RunSearchSmokeAsync(PluginDevSession session, string query, CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        var runtime = session.RuntimeAdapter;

        var searchItems = await runtime.SearchAsync(query, cancellationToken);
        messages.Add($"Search('{query}') returned {searchItems.Count} item(s).");
        if (searchItems.Count == 0)
        {
            return new PluginDevScenarioResult("search-smoke", false, messages);
        }

        var firstItem = searchItems[0];
        messages.Add($"Selected media '{firstItem.title}' ({firstItem.id}).");
        return new PluginDevScenarioResult("search-smoke", true, messages);
    }

    private static async Task<PluginDevScenarioResult> RunChaptersSmokeAsync(PluginDevSession session, string query, CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        var runtime = session.RuntimeAdapter;

        var searchItems = await runtime.SearchAsync(query, cancellationToken);
        messages.Add($"Search('{query}') returned {searchItems.Count} item(s).");
        if (searchItems.Count == 0)
        {
            return new PluginDevScenarioResult("chapters-smoke", false, messages);
        }

        var firstItem = searchItems[0];
        messages.Add($"Selected media '{firstItem.title}' ({firstItem.id}).");

        var chapters = await runtime.GetChaptersAsync(firstItem.id, cancellationToken);
        messages.Add($"Chapters returned {chapters.Count} item(s).");
        if (chapters.Count == 0)
        {
            return new PluginDevScenarioResult("chapters-smoke", false, messages);
        }

        var firstChapter = chapters[0];
        messages.Add($"Selected chapter '{firstChapter.title}' ({firstChapter.id}).");
        return new PluginDevScenarioResult("chapters-smoke", true, messages);
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

    private static async Task<PluginDevScenarioResult> RunVideoSmokeAsync(PluginDevSession session, string mediaId, CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        var streams = await session.RuntimeAdapter.GetVideoStreamsAsync(mediaId, cancellationToken);
        messages.Add($"VideoStreams('{mediaId}') returned {streams.Count} item(s).");
        if (streams.Count == 0)
        {
            messages.Add("No video streams were returned. This keeps the transport path testable even while the sample plugin still has placeholder stream data.");
            return new PluginDevScenarioResult("video-smoke", true, messages);
        }

        var first = streams[0];
        messages.Add($"First video stream: {first.Label} ({first.Id}) playlist={first.PlaylistUri}");
        return new PluginDevScenarioResult("video-smoke", true, messages);
    }

    private static IReadOnlyList<SearchItem> CoerceSearchItems(object? value)
    {
        return value switch
        {
            null => throw new InvalidOperationException("Search enrichment requires a non-null SearchItem or collection of SearchItem values."),
            SearchItem item => [item],
            IEnumerable<SearchItem> typedItems => typedItems.ToArray(),
            IEnumerable<object?> objects => objects.OfType<SearchItem>().ToArray() is { Length: > 0 } items ? items : throw new InvalidOperationException("Search enrichment requires SearchItem values."),
            _ => throw new InvalidOperationException($"Search enrichment cannot operate on value '{StringifyValue(value)}'.")
        };
    }

    private static string ResolveRequiredString(PluginDevScenarioStep step, string parameterName, IReadOnlyDictionary<string, object?> variables)
    {
        var value = ResolveOptionalString(step, parameterName, variables);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Scenario step '{step.Op}' requires a non-empty '{parameterName}' parameter.");
        }

        return value;
    }

    private static string? ResolveOptionalString(PluginDevScenarioStep step, string parameterName, IReadOnlyDictionary<string, object?> variables)
    {
        if (!TryGetParameter(step, parameterName, out var element))
        {
            return null;
        }

        var resolved = ResolveElement(element, variables);
        return resolved as string ?? StringifyValue(resolved, nullAsEmpty: true);
    }

    private static int ResolveRequiredInt(PluginDevScenarioStep step, string parameterName, IReadOnlyDictionary<string, object?> variables)
    {
        var value = ResolveOptionalInt(step, parameterName, variables);
        if (value is null)
        {
            throw new InvalidOperationException($"Scenario step '{step.Op}' requires integer parameter '{parameterName}'.");
        }

        return value.Value;
    }

    private static int? ResolveOptionalInt(PluginDevScenarioStep step, string parameterName, IReadOnlyDictionary<string, object?> variables)
    {
        if (!TryGetParameter(step, parameterName, out var element))
        {
            return null;
        }

        var resolved = ResolveElement(element, variables);
        return resolved switch
        {
            int intValue => intValue,
            long longValue => checked((int)longValue),
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.Number && jsonElement.TryGetInt32(out var jsonInt) => jsonInt,
            string stringValue when int.TryParse(stringValue, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new InvalidOperationException($"Scenario parameter '{parameterName}' on step '{step.Op}' did not resolve to an integer.")
        };
    }

    private static object? ResolveRequiredObject(PluginDevScenarioStep step, string parameterName, IReadOnlyDictionary<string, object?> variables)
    {
        if (!TryGetParameter(step, parameterName, out var element))
        {
            throw new InvalidOperationException($"Scenario step '{step.Op}' requires parameter '{parameterName}'.");
        }

        return ResolveElement(element, variables);
    }

    private static IReadOnlyList<object?> ResolveRequiredCollection(PluginDevScenarioStep step, string parameterName, IReadOnlyDictionary<string, object?> variables)
    {
        var resolved = ResolveRequiredObject(step, parameterName, variables);
        return resolved switch
        {
            IReadOnlyList<object?> objectList => objectList,
            System.Collections.IEnumerable enumerable when resolved is not string => enumerable.Cast<object?>().ToArray(),
            _ => throw new InvalidOperationException($"Scenario parameter '{parameterName}' on step '{step.Op}' did not resolve to a collection.")
        };
    }

    private static bool TryGetParameter(PluginDevScenarioStep step, string parameterName, out JsonElement element)
    {
        if (step.Parameters.TryGetValue(parameterName, out element))
        {
            return true;
        }

        foreach (var (key, value) in step.Parameters)
        {
            if (string.Equals(key, parameterName, StringComparison.OrdinalIgnoreCase))
            {
                element = value;
                return true;
            }
        }

        element = default;
        return false;
    }

    private static object? ResolveElement(JsonElement element, IReadOnlyDictionary<string, object?> variables)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => ResolveStringElement(element.GetString() ?? string.Empty, variables),
            JsonValueKind.Number when element.TryGetInt64(out var integerValue) => integerValue,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element
        };
    }

    private static object? ResolveStringElement(string rawValue, IReadOnlyDictionary<string, object?> variables)
    {
        var trimmed = rawValue.Trim();
        if (trimmed.StartsWith('$') && !trimmed.Contains("{{", StringComparison.Ordinal))
        {
            return ResolveExpression(trimmed[1..], variables);
        }

        return RenderTemplate(rawValue, variables);
    }

    private static string RenderTemplate(string template, IReadOnlyDictionary<string, object?> variables)
    {
        return TemplatePattern.Replace(template, match => StringifyValue(ResolveExpression(match.Groups["expr"].Value.Trim(), variables), nullAsEmpty: true));
    }

    private static IReadOnlyList<string> ExtractExpressions(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return [];
        }

        var raw = element.GetString() ?? string.Empty;
        var expressions = new List<string>();
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('$') && !trimmed.Contains("{{", StringComparison.Ordinal))
        {
            expressions.Add(trimmed[1..]);
        }

        foreach (Match match in TemplatePattern.Matches(raw))
        {
            var value = match.Groups["expr"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                expressions.Add(value);
            }
        }

        return expressions;
    }

    private static string? ExtractRootSymbol(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        var trimmed = expression.Trim();
        if (trimmed.StartsWith('$'))
        {
            trimmed = trimmed[1..];
        }

        var dotIndex = trimmed.IndexOf('.');
        return dotIndex >= 0 ? trimmed[..dotIndex] : trimmed;
    }

    private static object? ResolveExpression(string expression, IReadOnlyDictionary<string, object?> variables)
    {
        var segments = expression.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        if (!variables.TryGetValue(segments[0], out var current))
        {
            return null;
        }

        for (var index = 1; index < segments.Length; index++)
        {
            current = ResolveSegment(current, segments[index]);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static object? ResolveSegment(object? current, string segment)
    {
        if (current is null)
        {
            return null;
        }

        if (current is IReadOnlyDictionary<string, object?> dictionary)
        {
            return dictionary.TryGetValue(segment, out var value) ? value : null;
        }

        if (current is IEnumerable<MetadataItem> metadataItems)
        {
            return metadataItems.FirstOrDefault(item => string.Equals(item.key, segment, StringComparison.OrdinalIgnoreCase))?.value;
        }

        if (current is System.Collections.IEnumerable enumerable && segment.Equals("count", StringComparison.OrdinalIgnoreCase) && current is not string)
        {
            return enumerable.Cast<object?>().Count();
        }

        var type = current.GetType();
        var property = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase));
        if (property is not null)
        {
            return property.GetValue(current);
        }

        var field = type.GetFields(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase));
        return field?.GetValue(current);
    }

    private static void SaveValue(IDictionary<string, object?> variables, string? saveName, object? value)
    {
        if (!string.IsNullOrWhiteSpace(saveName))
        {
            variables[saveName.Trim()] = value;
        }
    }

    private static string StringifyValue(object? value, bool nullAsEmpty = false)
    {
        if (value is null)
        {
            return nullAsEmpty ? string.Empty : "<null>";
        }

        return value switch
        {
            string stringValue => stringValue,
            MetadataItem metadataItem => $"{metadataItem.key}={metadataItem.value}",
            IEnumerable<MetadataItem> metadataItems => string.Join(", ", metadataItems.Select(static item => $"{item.key}={item.value}")),
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String => jsonElement.GetString() ?? string.Empty,
            JsonElement jsonElement => jsonElement.ToString(),
            System.Collections.IEnumerable enumerable when value is not string => JsonSerializer.Serialize(enumerable.Cast<object?>().ToArray()),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.ToString() ?? string.Empty
        };
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

public sealed class PluginDevBuildException : InvalidOperationException
{
    public PluginDevBuildException(string planName, int exitCode, string userFacingOutput, string formattedOutput)
        : base($"Build failed using plan '{planName}' with exit code {exitCode}.\n{userFacingOutput}")
    {
        PlanName = planName;
        ExitCode = exitCode;
        UserFacingOutput = userFacingOutput;
        FormattedOutput = formattedOutput;
    }

    public string PlanName { get; }
    public int ExitCode { get; }
    public string UserFacingOutput { get; }
    public string FormattedOutput { get; }

    public static string FormatProcessOutput(PluginDevProcessResult result)
        => BuildRelevantExcerpt(result) is { Length: > 0 } relevantExcerpt
            ? relevantExcerpt
            : FormatDetailedProcessOutput(result);

    public static string FormatDetailedProcessOutput(PluginDevProcessResult result)
    {
        var sections = new List<string>();

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            sections.Add($"stdout:\n{result.StandardOutput.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            sections.Add($"stderr:\n{result.StandardError.Trim()}");
        }

        return sections.Count == 0
            ? "The build process did not emit any stdout or stderr output."
            : string.Join("\n\n", sections);
    }

    private static string BuildRelevantExcerpt(PluginDevProcessResult result)
    {
        var stderrExcerpt = TryBuildRelevantExcerpt("stderr", result.StandardError, out var stderrHasInterestingLines);
        var stdoutExcerpt = TryBuildRelevantExcerpt("stdout", result.StandardOutput, out var stdoutHasInterestingLines);

        if (stderrHasInterestingLines && !string.IsNullOrWhiteSpace(stderrExcerpt))
        {
            return stderrExcerpt;
        }

        if (stdoutHasInterestingLines && !string.IsNullOrWhiteSpace(stdoutExcerpt))
        {
            return stdoutExcerpt;
        }

        if (!string.IsNullOrWhiteSpace(stderrExcerpt))
        {
            return stderrExcerpt;
        }

        return stdoutExcerpt;
    }

    private static string TryBuildRelevantExcerpt(string label, string? content, out bool hasInterestingLines)
    {
        hasInterestingLines = false;

        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var lines = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        var exactErrorBlock = FindTrailingExactErrorBlock(lines);
        if (exactErrorBlock.Length > 0)
        {
            hasInterestingLines = true;
            return string.Join(Environment.NewLine, exactErrorBlock);
        }

        var lastInterestingIndex = FindLastInterestingLine(lines);
        if (lastInterestingIndex < 0)
        {
            return $"{label}:\n{TakeTailBlock(lines)}";
        }

        var startIndex = FindBlockStart(lines, lastInterestingIndex);
        var block = TrimBlock(lines, startIndex, lastInterestingIndex + 1);
        if (block.Length == 0)
        {
            return string.Empty;
        }

        hasInterestingLines = true;
        return $"{label}:\n{string.Join(Environment.NewLine, block)}";
    }

    private static string[] FindTrailingExactErrorBlock(string[] lines)
    {
        var block = new List<string>();

        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var line = lines[index].Trim();
            if (line.Length == 0)
            {
                if (block.Count == 0)
                {
                    continue;
                }

                continue;
            }

            if (IsExactCompilerErrorLine(line))
            {
                block.Add(line);
                continue;
            }

            if (block.Count > 0)
            {
                break;
            }
        }

        block.Reverse();
        return [.. block];
    }

    private static bool IsExactCompilerErrorLine(string line)
    {
        return line.Contains(": error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("error CS", StringComparison.OrdinalIgnoreCase)
            || line.Contains(" error CS", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindLastInterestingLine(string[] lines)
    {
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var line = lines[index].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (IsInterestingBuildFailureLine(line))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsInterestingBuildFailureLine(string line)
    {
        return line.Contains(": error", StringComparison.OrdinalIgnoreCase)
            || line.Contains(" error ", StringComparison.OrdinalIgnoreCase)
            || line.Contains("error:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
            || line.Contains("undefined", StringComparison.OrdinalIgnoreCase)
            || line.Contains("unable to", StringComparison.OrdinalIgnoreCase)
            || line.Contains("could not", StringComparison.OrdinalIgnoreCase)
            || line.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindBlockStart(string[] lines, int endIndex)
    {
        var startIndex = Math.Max(0, endIndex - 11);
        for (var index = endIndex; index >= startIndex; index--)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                return Math.Min(endIndex, index + 1);
            }
        }

        return startIndex;
    }

    private static string[] TrimBlock(string[] lines, int startIndex, int endExclusive)
    {
        var block = lines[startIndex..endExclusive]
            .SkipWhile(string.IsNullOrWhiteSpace)
            .ToArray();

        var trimmedLength = block.Length;
        while (trimmedLength > 0 && string.IsNullOrWhiteSpace(block[trimmedLength - 1]))
        {
            trimmedLength -= 1;
        }

        return trimmedLength == block.Length ? block : block[..trimmedLength];
    }

    private static string TakeTailBlock(string[] lines)
    {
        var tail = TrimBlock(lines, Math.Max(0, lines.Length - 12), lines.Length);
        return tail.Length == 0
            ? string.Empty
            : string.Join(Environment.NewLine, tail);
    }
}

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

        startInfo.Environment["DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION"] = "1";
        startInfo.Environment["DOTNET_CLI_CONTEXT_ANSI_PASS_THRU"] = "true";

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
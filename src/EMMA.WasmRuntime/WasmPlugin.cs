using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using EMMA.Domain;
using EMMA.WasmRuntime.Native;
using EMMA.WasmRuntime.Wire;

namespace EMMA.WasmRuntime;

/// <summary>
/// One open plugin. Every call gets a fresh instance inside the host, so calls are independent and may
/// run concurrently. Calls cannot be interrupted once started; the host's CPU budget and call timeout
/// bound them, and the cancellation token is honoured before a call starts.
/// </summary>
public sealed class WasmPlugin : IDisposable
{
    private static readonly IReadOnlyList<PluginLogEntry> NoLogs = [];

    private readonly PluginHandle handle;

    /// <summary>Initializes a new instance of the <see cref="WasmPlugin"/> class.</summary>
    /// <param name="pointer">The <c>EmmaPlugin*</c> to own.</param>
    /// <param name="searchAssist">Whether the plugin exports search-assist.</param>
    internal WasmPlugin(nint pointer, bool searchAssist)
    {
        this.handle = new PluginHandle(pointer);
        this.SearchAssist = searchAssist;
    }

    /// <summary>Gets a value indicating whether the plugin exports the optional search-assist interface.</summary>
    public bool SearchAssist { get; }

    /// <summary>Asks the plugin to describe itself.</summary>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>The declaration.</returns>
    public Task<PluginCall<PluginInfo>> DescribeAsync(CancellationToken cancellationToken) =>
        this.InvokeAsync("describe", "{}"u8.ToArray(), WireJsonContext.Default.WireEnvelopeWireInfo, WireReader.Info, cancellationToken);

    /// <summary>Probes the plugin's liveness and upstream reachability.</summary>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>The health report.</returns>
    public Task<PluginCall<PluginHealth>> HealthAsync(CancellationToken cancellationToken) =>
        this.InvokeAsync("health", "{}"u8.ToArray(), WireJsonContext.Default.WireEnvelopeWireHealth, health => new PluginHealth(health.Ok, health.Detail), cancellationToken);

    /// <summary>Searches the plugin's catalog.</summary>
    /// <param name="query">The request.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>A page of raw nodes.</returns>
    public Task<PluginCall<PluginNodePage>> SearchAsync(Query query, CancellationToken cancellationToken) =>
        this.InvokeAsync("search", JsonSerializer.SerializeToUtf8Bytes(WireWriter.Query(query), WireJsonContext.Default.WireQuery), WireJsonContext.Default.WireEnvelopeWireNodePage, WireReader.Page, cancellationToken);

    /// <summary>Lists the children of a node, or the browse root.</summary>
    /// <param name="parent">The parent id, or <see langword="null"/> for the root.</param>
    /// <param name="request">Cursor and limit.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>A page of raw nodes.</returns>
    public Task<PluginCall<PluginNodePage>> ChildrenAsync(LocalNodeId? parent, PageRequest request, CancellationToken cancellationToken) =>
        this.InvokeAsync("children", JsonSerializer.SerializeToUtf8Bytes(WireWriter.Children(parent, request), WireJsonContext.Default.WireChildrenRequest), WireJsonContext.Default.WireEnvelopeWireNodePage, WireReader.Page, cancellationToken);

    /// <summary>Fetches nodes by id; unknown ids are omitted.</summary>
    /// <param name="ids">The plugin-local ids.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>The raw nodes found.</returns>
    public Task<PluginCall<IReadOnlyList<PluginNode>>> GetAsync(IReadOnlyList<LocalNodeId> ids, CancellationToken cancellationToken) =>
        this.InvokeAsync("get", Ids(ids), WireJsonContext.Default.WireEnvelopeIReadOnlyListWireNode, Nodes, cancellationToken);

    /// <summary>Resolves a node to its assets for a role.</summary>
    /// <param name="request">What to resolve.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>The assets.</returns>
    public Task<PluginCall<IReadOnlyList<Asset>>> ResolveAsync(ResolveRequest request, CancellationToken cancellationToken) =>
        this.InvokeAsync("resolve", JsonSerializer.SerializeToUtf8Bytes(WireWriter.Resolve(request), WireJsonContext.Default.WireResolveRequest), WireJsonContext.Default.WireEnvelopeIReadOnlyListWireAsset, Assets, cancellationToken);

    /// <summary>Suggests completions for partly typed search text. The plugin must export search-assist.</summary>
    /// <param name="text">The partial text.</param>
    /// <param name="limit">The most suggestions wanted.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>The suggestions; <see cref="ErrorCode.Unsupported"/> when the plugin does not assist.</returns>
    public Task<PluginCall<IReadOnlyList<string>>> SuggestAsync(string text, int limit, CancellationToken cancellationToken) =>
        this.InvokeAsync("suggest", JsonSerializer.SerializeToUtf8Bytes(new WireSuggestRequest(text, (uint)Math.Max(limit, 0)), WireJsonContext.Default.WireSuggestRequest), WireJsonContext.Default.WireEnvelopeIReadOnlyListString, texts => texts, cancellationToken);

    /// <summary>Adds detail to nodes the host already holds. The plugin must export search-assist.</summary>
    /// <param name="ids">The plugin-local ids.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>The enriched nodes; <see cref="ErrorCode.Unsupported"/> when the plugin does not assist.</returns>
    public Task<PluginCall<IReadOnlyList<PluginNode>>> EnrichAsync(IReadOnlyList<LocalNodeId> ids, CancellationToken cancellationToken) =>
        this.InvokeAsync("enrich", Ids(ids), WireJsonContext.Default.WireEnvelopeIReadOnlyListWireNode, Nodes, cancellationToken);

    /// <summary>Reads bytes from a proxied asset.</summary>
    /// <param name="token">The token the plugin issued.</param>
    /// <param name="range">The range, or <see langword="null"/> for everything.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>The bytes.</returns>
    public async Task<PluginCall<Chunk>> ReadAsync(string token, ByteRange? range, CancellationToken cancellationToken)
    {
        byte[] request = JsonSerializer.SerializeToUtf8Bytes(WireWriter.Read(token, range), WireJsonContext.Default.WireReadRequest);
        RawCall raw = await this.CallAsync("read", request, cancellationToken).ConfigureAwait(false);
        PluginCall<WireChunkInfo> info = Interpret(raw, WireJsonContext.Default.WireEnvelopeWireChunkInfo, value => value);
        return new PluginCall<Chunk>(
            info.Outcome.Map(value => new Chunk(raw.Blob, value.Total is { } total ? (long)Math.Min(total, long.MaxValue) : null)),
            info.Trapped,
            info.Logs);
    }

    /// <inheritdoc/>
    public void Dispose() => this.handle.Dispose();

    private static byte[] Ids(IReadOnlyList<LocalNodeId> ids) =>
        JsonSerializer.SerializeToUtf8Bytes(new WireIdsRequest([.. ids.Select(id => id.Value)]), WireJsonContext.Default.WireIdsRequest);

    private static IReadOnlyList<PluginNode> Nodes(IReadOnlyList<WireNode> nodes) => [.. nodes.Select(WireReader.Node)];

    private static IReadOnlyList<Asset> Assets(IReadOnlyList<WireAsset> assets) => [.. assets.Select(WireReader.Asset)];

    private static PluginCall<TResult> Interpret<TWire, TResult>(
        RawCall raw,
        JsonTypeInfo<WireEnvelope<TWire>> typeInfo,
        Func<TWire, TResult> map)
    {
        if (raw.Failure is not null)
        {
            return new PluginCall<TResult>(Outcome.Failure<TResult>(ErrorCode.Internal, raw.Failure), raw.Panicked, NoLogs);
        }

        try
        {
            WireEnvelope<TWire> envelope = JsonSerializer.Deserialize(raw.Json, typeInfo)
                ?? throw new WireFormatException("The host returned an empty envelope.");
            IReadOnlyList<PluginLogEntry> logs = [.. envelope.Logs.Select(Log)];
            if (envelope.Error is not null)
            {
                return new PluginCall<TResult>(Outcome.Failure<TResult>(WireReader.Error(envelope.Error)), envelope.Trapped, logs);
            }

            TWire value = envelope.Ok ?? throw new WireFormatException("The host returned neither a result nor an error.");
            return new PluginCall<TResult>(Outcome.Success(map(value)), envelope.Trapped, logs);
        }
        catch (Exception ex) when (ex is JsonException or WireFormatException or FormatException)
        {
            return new PluginCall<TResult>(
                Outcome.Failure<TResult>(ErrorCode.Internal, $"The plugin returned data that does not follow the contract: {ex.Message}"),
                false,
                NoLogs);
        }
    }

    private static PluginLogEntry Log(WireLog log) => new(
        log.Level switch
        {
            "trace" => PluginLogLevel.Trace,
            "debug" => PluginLogLevel.Debug,
            "warn" => PluginLogLevel.Warn,
            "error" => PluginLogLevel.Error,
            _ => PluginLogLevel.Info,
        },
        log.Message);

    private async Task<PluginCall<TResult>> InvokeAsync<TWire, TResult>(
        string operation,
        byte[] request,
        JsonTypeInfo<WireEnvelope<TWire>> typeInfo,
        Func<TWire, TResult> map,
        CancellationToken cancellationToken)
    {
        RawCall raw = await this.CallAsync(operation, request, cancellationToken).ConfigureAwait(false);
        return Interpret(raw, typeInfo, map);
    }

    private Task<RawCall> CallAsync(string operation, byte[] request, CancellationToken cancellationToken)
    {
        byte[] name = Encoding.UTF8.GetBytes(operation);
        return Task.Run(() => this.CallNative(name, request), cancellationToken);
    }

    private RawCall CallNative(byte[] operation, byte[] request)
    {
        int code = NativeMethods.PluginCall(this.handle, operation, (nuint)operation.Length, request, (nuint)request.Length, out EmmaBuffer json, out EmmaBuffer blob, out EmmaBuffer error);
        byte[] jsonBytes = json.Take();
        byte[] blobBytes = blob.Take();
        byte[] errorBytes = error.Take();
        if (code == NativeMethods.Ok)
        {
            return new RawCall(jsonBytes, blobBytes, null, false);
        }

        string failure = errorBytes.Length == 0 ? "The Wasm host failed the call." : Encoding.UTF8.GetString(errorBytes);
        return new RawCall([], [], failure, code == NativeMethods.ErrorPanic);
    }

    private sealed record RawCall(byte[] Json, byte[] Blob, string? Failure, bool Panicked);
}

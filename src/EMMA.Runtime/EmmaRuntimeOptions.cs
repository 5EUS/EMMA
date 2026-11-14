using EMMA.Application.Pipelines;
using EMMA.Application.Ports;
using EMMA.Infrastructure.Cache;
using EMMA.PluginHost.Runtime;
using EMMA.PluginHost.Signing;

namespace EMMA.Runtime;

/// <summary>What a host chooses when it builds the runtime. Everything else is fixed by EMMA.</summary>
/// <param name="DataDirectory">The directory EMMA keeps its state in: database, plugins, delegation files and downloads. Caches live here too unless <see cref="CacheDirectory"/> says otherwise.</param>
public sealed record EmmaRuntimeOptions(string DataDirectory)
{
    /// <summary>
    /// Gets the directory for purgeable cache files (asset bytes, thumbnails). Defaults to <c>cache</c> inside the data directory.
    /// An app that has an OS cache directory (not backed up, may be purged) should pass it here; losing it costs only speed.
    /// </summary>
    public string? CacheDirectory { get; init; }

    /// <summary>Gets the asset cache budgets and maximum age. The directory is always <see cref="CacheDirectory"/>; the default is <see cref="AssetCacheOptions.Default"/>.</summary>
    public AssetCacheOptions? AssetCache { get; init; }

    /// <summary>Gets which plugin signatures are accepted. The default trusts no repository, so nothing can be installed.</summary>
    public TrustPolicy Trust { get; init; } = TrustPolicy.TrustNothing;

    /// <summary>Gets where plugin log lines go. The default discards them.</summary>
    public IPluginLogPort PluginLog { get; init; } = new DiscardingPluginLog();

    /// <summary>
    /// Gets the directory holding the native libraries EMMA loads (<c>libemma_wasm</c>, <c>libe_sqlite3</c>), for a host
    /// that is a library loaded into someone else's process. Leave <see langword="null"/> when they sit next to the executable.
    /// </summary>
    public string? NativeLibraryDirectory { get; init; }

    /// <summary>Gets the time source.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Gets the retry and timeout policy for plugin calls.</summary>
    public PluginCallOptions CallOptions { get; init; } = PluginCallOptions.Default;

    /// <summary>Gets the failure policy for loaded Wasm plugins.</summary>
    public WasmGatewayOptions Gateway { get; init; } = new();

    /// <summary>Gets a replacement for the Wasm plugin gateway, for tests and for embedding a different transport. When set, the native Wasm host is never loaded.</summary>
    public IPluginGateway? GatewayOverride { get; init; }

    /// <summary>Gets a replacement for the HTTP asset fetcher, for tests.</summary>
    public IAssetFetcherPort? FetcherOverride { get; init; }
}

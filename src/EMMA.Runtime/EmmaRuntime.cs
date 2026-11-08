using EMMA.Application.Pipelines;
using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.Infrastructure.Cache;
using EMMA.Infrastructure.Http;
using EMMA.Infrastructure.Settings;
using EMMA.PluginHost.Install;
using EMMA.PluginHost.Policy;
using EMMA.PluginHost.Runtime;
using EMMA.PluginHost.Signing;
using EMMA.Storage;
using EMMA.WasmRuntime;

namespace EMMA.Runtime;

/// <summary>
/// EMMA assembled: storage, the plugin host and the pipelines, wired once. Every host (HTTP API, native
/// library, CLI) builds one of these and talks to the pipelines, so they cannot drift apart. Construction does
/// no I/O; call <see cref="InitializeAsync"/> before use.
/// </summary>
public sealed partial class EmmaRuntime : IAsyncDisposable
{
    private readonly StorageInitializer storage;
    private readonly BoundedAssetCache cache;
    private readonly HttpClient? http;
    private readonly LazyWasmTransport? transport;
    private readonly WasmPluginGateway? wasmGateway;

    /// <summary>Initializes a new instance of the <see cref="EmmaRuntime"/> class.</summary>
    /// <param name="options">The host's choices.</param>
    public EmmaRuntime(EmmaRuntimeOptions options)
    {
        string data = Path.GetFullPath(options.DataDirectory);
        if (options.NativeLibraryDirectory is { } libraries)
        {
            SqliteNativeLibrary.AddSearchDirectory(libraries);
            WasmRuntimeHost.AddLibrarySearchDirectory(libraries);
        }

        this.Time = options.Time;
        this.Trust = options.Trust;
        this.HostOptions = new PluginHostOptions(Path.Combine(data, "plugins"));

        StorageOptions storageOptions = new(Path.Combine(data, "emma.db"));
        this.storage = new StorageInitializer(storageOptions, this.Time);
        this.Registry = new SqlitePluginRegistry(storageOptions);
        this.Nodes = new SqliteNodeStore(storageOptions, this.Time);
        this.ProgressStore = new SqliteProgressStore(storageOptions);
        this.Library = new SqliteLibraryStore(storageOptions);
        this.Policies = new ManifestCapabilityPolicyProvider();
        this.Verifier = new PluginSignatureVerifier(
            options.Trust,
            new FileDelegationSource(Path.Combine(data, "delegations")),
            new SqliteDelegationVersionStore(storageOptions, this.Time),
            this.Time);
        this.Installer = new PluginPackageInstaller(this.HostOptions, options.Trust, this.Verifier, this.Registry, this.Time);

        this.cache = new BoundedAssetCache(AssetCacheOptions.Default with { DiskRoot = Path.Combine(data, "cache", "assets") }, this.Time);
        IAssetFetcherPort fetcher;
        if (options.FetcherOverride is { } custom)
        {
            fetcher = custom;
        }
        else
        {
            // The client owns the handler and the runtime owns the client; the analyzer cannot see the hand-over.
#pragma warning disable CA2000
            this.http = new HttpClient(HttpAssetFetcher.CreateHandler());
#pragma warning restore CA2000
            fetcher = new HttpAssetFetcher(this.http, this.Time);
        }

        PluginCallExecutor executor = new(options.CallOptions, this.Time);

        if (options.GatewayOverride is { } gateway)
        {
            this.Gateway = gateway;
        }
        else
        {
            this.transport = new LazyWasmTransport();
            PluginLoader loader = new(this.HostOptions, options.Trust, this.Verifier, this.Registry, new EmptyPluginSettings(), this.transport, this.Time);
            this.wasmGateway = new WasmPluginGateway(loader, this.Registry, options.PluginLog, this.Time, options.Gateway);
            this.Gateway = this.wasmGateway;
        }

        this.Catalog = new CatalogPipeline(this.Gateway, this.Nodes, executor);
        this.Assets = new AssetPipeline(this.Gateway, this.Policies, this.Nodes, fetcher, this.cache, executor, AssetPipelineOptions.Default, this.Time);
        this.Progress = new ProgressPipeline(this.ProgressStore, this.Time);
    }

    /// <summary>Gets the time source.</summary>
    public TimeProvider Time { get; }

    /// <summary>Gets which plugin signatures are accepted.</summary>
    public TrustPolicy Trust { get; }

    /// <summary>Gets where plugins are installed and the package limits.</summary>
    public PluginHostOptions HostOptions { get; }

    /// <summary>Gets the record of installed plugins.</summary>
    public IPluginRegistryPort Registry { get; }

    /// <summary>Gets the nodes the host has seen.</summary>
    public INodeStorePort Nodes { get; }

    /// <summary>Gets the stored progress. Hosts use <see cref="Progress"/>; this is for the tests that seed it.</summary>
    public IProgressPort ProgressStore { get; }

    /// <summary>Gets the saved nodes of each user.</summary>
    public ILibraryPort Library { get; }

    /// <summary>Gets what each plugin may reach.</summary>
    public ManifestCapabilityPolicyProvider Policies { get; }

    /// <summary>Gets the signature verifier.</summary>
    public PluginSignatureVerifier Verifier { get; }

    /// <summary>Gets the package installer. Prefer <see cref="InstallAsync"/>, which also refreshes the running state.</summary>
    public PluginPackageInstaller Installer { get; }

    /// <summary>Gets how plugins are reached.</summary>
    public IPluginGateway Gateway { get; }

    /// <summary>Gets search, browsing and lookup.</summary>
    public CatalogPipeline Catalog { get; }

    /// <summary>Gets asset resolution and bytes.</summary>
    public AssetPipeline Assets { get; }

    /// <summary>Gets progress.</summary>
    public ProgressPipeline Progress { get; }

    /// <summary>Creates and migrates the database, prepares the asset cache and builds each plugin's permissions.</summary>
    /// <param name="cancellationToken">Cancels the preparation.</param>
    /// <returns>The plugins that were quarantined because their stored manifest could not be read.</returns>
    public async Task<IReadOnlyList<PluginId>> InitializeAsync(CancellationToken cancellationToken)
    {
        await this.storage.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await this.cache.InitializeAsync(cancellationToken).ConfigureAwait(false);

        // A plugin whose stored manifest cannot be read is denied everything by the policy snapshot; take it out of service too.
        IReadOnlyList<PluginId> unreadable = await this.Policies.RefreshAsync(this.Registry, cancellationToken).ConfigureAwait(false);
        foreach (PluginId plugin in unreadable)
        {
            await this.Registry.SetStatusAsync(plugin, PluginStatus.Quarantined, this.Time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }

        return unreadable;
    }

    /// <summary>Installs a package and makes it available to this runtime straight away.</summary>
    /// <param name="package">The package; it must be seekable.</param>
    /// <param name="repositoryId">The repository it came from, as known to the host.</param>
    /// <param name="cancellationToken">Cancels the install.</param>
    /// <returns>The installed plugin, or why it was refused.</returns>
    public async Task<Outcome<InstalledPlugin>> InstallAsync(Stream package, string repositoryId, CancellationToken cancellationToken)
    {
        Outcome<InstalledPlugin> installed = await this.Installer.InstallAsync(package, repositoryId, cancellationToken).ConfigureAwait(false);
        if (installed.IsSuccess)
        {
            await this.PluginChangedAsync(installed.Value.Id, cancellationToken).ConfigureAwait(false);
        }

        return installed;
    }

    /// <summary>Enables, disables or quarantines a plugin, effective immediately.</summary>
    /// <param name="plugin">The plugin.</param>
    /// <param name="status">The new status.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns><see langword="true"/> when the plugin is installed.</returns>
    public async Task<bool> SetStatusAsync(PluginId plugin, PluginStatus status, CancellationToken cancellationToken)
    {
        bool found = await this.Registry.SetStatusAsync(plugin, status, this.Time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (found)
        {
            await this.PluginChangedAsync(plugin, cancellationToken).ConfigureAwait(false);
        }

        return found;
    }

    /// <summary>Removes a plugin: closes it, forgets it and deletes its files.</summary>
    /// <param name="plugin">The plugin.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    /// <returns><see langword="true"/> when the plugin was installed.</returns>
    public async Task<bool> UninstallAsync(PluginId plugin, CancellationToken cancellationToken)
    {
        await this.UnloadAsync(plugin, cancellationToken).ConfigureAwait(false);
        bool found = await this.Registry.RemoveAsync(plugin, cancellationToken).ConfigureAwait(false);

        // A plugin id is a validated token with no separators, so this stays inside the install root.
        string directory = Path.Combine(this.HostOptions.InstallRoot, plugin.Value);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        await this.Policies.RefreshAsync(this.Registry, cancellationToken).ConfigureAwait(false);
        return found;
    }

    /// <summary>Tells the runtime a plugin's files or status changed: rebuilds permissions and closes the loaded instance so the next call reloads it.</summary>
    /// <param name="plugin">The plugin.</param>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    /// <returns>A task that completes when the runtime has caught up.</returns>
    public async Task PluginChangedAsync(PluginId plugin, CancellationToken cancellationToken)
    {
        await this.UnloadAsync(plugin, cancellationToken).ConfigureAwait(false);
        await this.Policies.RefreshAsync(this.Registry, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (this.wasmGateway is not null)
        {
            await this.wasmGateway.DisposeAsync().ConfigureAwait(false);
        }

        this.transport?.Dispose();
        this.http?.Dispose();
    }

    private Task UnloadAsync(PluginId plugin, CancellationToken cancellationToken) =>
        this.wasmGateway?.UnloadAsync(plugin, cancellationToken) ?? Task.CompletedTask;
}

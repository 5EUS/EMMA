using EMMA.Application.Pipelines;
using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.Infrastructure.Cache;
using EMMA.Infrastructure.Downloads;
using EMMA.Infrastructure.Http;
using EMMA.Infrastructure.Secrets;
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
    private readonly SqliteNodeListingStore listings;
    private readonly BoundedResolutionCache resolutions;
    private readonly DownloadFileStore downloadFiles;
    private readonly FileSecretBox secretBox;
    private readonly HttpClient? http;
    private readonly LazyWasmTransport? transport;
    private readonly WasmPluginGateway? wasmGateway;

    /// <summary>Initializes a new instance of the <see cref="EmmaRuntime"/> class.</summary>
    /// <param name="options">The host's choices.</param>
    public EmmaRuntime(EmmaRuntimeOptions options)
    {
        string data = Path.GetFullPath(options.DataDirectory);
        ConfigureNativeLibraries(options.NativeLibraryDirectory);
        this.Time = options.Time;
        this.Trust = options.Trust;
        this.HostOptions = new PluginHostOptions(Path.Combine(data, "plugins"));

        StorageOptions storageOptions = new(Path.Combine(data, "emma.db"));
        this.storage = new StorageInitializer(storageOptions, this.Time);
        this.Registry = new SqlitePluginRegistry(storageOptions);
        this.Nodes = new SqliteNodeStore(storageOptions, this.Time);
        this.ProgressStore = new SqliteProgressStore(storageOptions);
        this.LibraryStore = new SqliteLibraryStore(storageOptions, this.Time);
        IAppSettingsPort appSettings = new SqliteAppSettings(storageOptions);
        this.secretBox = new FileSecretBox(Path.Combine(data, "secret.key"));
        IPluginSettingsPort settings = new SqlitePluginSettings(storageOptions, this.secretBox, this.Time);
        this.Policies = new ManifestCapabilityPolicyProvider();
        this.Verifier = new PluginSignatureVerifier(
            options.Trust,
            new FileDelegationSource(Path.Combine(data, "delegations")),
            new SqliteDelegationVersionStore(storageOptions, this.Time),
            this.Time);
        this.Installer = new PluginPackageInstaller(this.HostOptions, options.Trust, this.Verifier, this.Registry, this.Time);

        string cacheRoot = options.CacheDirectory is { } cacheDirectory ? Path.GetFullPath(cacheDirectory) : Path.Combine(data, "cache");
        this.cache = new BoundedAssetCache(
            (options.AssetCache ?? AssetCacheOptions.Default) with { DiskRoot = Path.Combine(cacheRoot, "assets") },
            this.Time,
            new SqliteAssetCacheIndex(storageOptions));
        this.listings = new SqliteNodeListingStore(storageOptions, this.Time);
        this.resolutions = new BoundedResolutionCache(options.MaxResolutions, this.Time);
        (IAssetFetcherPort fetcher, this.http) = CreateFetcher(options.FetcherOverride, this.Time);

        PluginCallExecutor executor = new(options.CallOptions, this.Time);

        if (options.GatewayOverride is { } gateway)
        {
            this.Gateway = gateway;
        }
        else
        {
            this.transport = new LazyWasmTransport();
            PluginLoader loader = new(this.HostOptions, options.Trust, this.Verifier, this.Registry, settings, this.transport, this.Time);
            this.wasmGateway = new WasmPluginGateway(loader, this.Registry, options.PluginLog, this.Time, options.Gateway);
            this.Gateway = this.wasmGateway;
        }

        this.Catalog = new CatalogPipeline(this.Gateway, this.Nodes, this.listings, executor);
        this.Assets = new AssetPipeline(this.Gateway, this.Policies, this.Nodes, fetcher, this.cache, this.resolutions, executor, options.AssetPipeline ?? AssetPipelineOptions.Default, this.Time);
        this.Progress = new ProgressPipeline(this.ProgressStore, appSettings, this.Time);
        this.Library = new LibraryPipeline(this.LibraryStore, this.Nodes, this.Time);
        this.Refresh = new RefreshPipeline(this.Library, this.Nodes, this.Catalog, this.cache, this.resolutions);
        this.downloadFiles = new DownloadFileStore(Path.Combine(data, "downloads"));
        this.Downloads = new DownloadPipeline(new SqliteDownloadStore(storageOptions), this.downloadFiles, this.Catalog, this.Assets, this.Nodes, appSettings, this.Time);
        this.AppSettings = new AppSettingsPipeline(appSettings, this.Time);
        this.PluginSettings = new PluginSettingsPipeline(this.Gateway, settings);
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

    /// <summary>Gets the stored libraries. Hosts use <see cref="Library"/>; this is for the tests that seed it.</summary>
    public ILibraryPort LibraryStore { get; }

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

    /// <summary>Gets progress and history.</summary>
    public ProgressPipeline Progress { get; }

    /// <summary>Gets each user's libraries and the nodes saved in them.</summary>
    public LibraryPipeline Library { get; }

    /// <summary>Gets refreshing of saved nodes: new children, and a node's details again.</summary>
    public RefreshPipeline Refresh { get; }

    /// <summary>
    /// Gets the download queue and its executor. The executor does nothing until a host calls
    /// <see cref="DownloadPipeline.Start"/> (a server or an app) or <see cref="DownloadPipeline.RunUntilIdleAsync"/> (a command line).
    /// </summary>
    public DownloadPipeline Downloads { get; }

    /// <summary>Gets what the app remembers for each user, such as how a series is sorted.</summary>
    public AppSettingsPipeline AppSettings { get; }

    /// <summary>Gets the user's plugin settings. Prefer <see cref="SetPluginSettingsAsync"/> for changes, which also reopens the plugin.</summary>
    public PluginSettingsPipeline PluginSettings { get; }

    /// <summary>Creates and migrates the database, prepares the asset cache and builds each plugin's permissions.</summary>
    /// <param name="cancellationToken">Cancels the preparation.</param>
    /// <returns>The plugins that were quarantined because their stored manifest could not be read.</returns>
    public async Task<IReadOnlyList<PluginId>> InitializeAsync(CancellationToken cancellationToken)
    {
        await this.storage.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await this.cache.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await this.downloadFiles.InitializeAsync().ConfigureAwait(false);

        return await this.RefreshPoliciesAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>Gets where each of a plugin's declared settings stands.</summary>
    /// <param name="plugin">The plugin, which must be enabled.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One state per declared setting, or why the plugin could not be asked.</returns>
    public Task<Outcome<IReadOnlyList<SettingState>>> GetPluginSettingsAsync(PluginId plugin, CancellationToken cancellationToken) =>
        this.PluginSettings.GetAsync(plugin, cancellationToken);

    /// <summary>Changes a plugin's settings, effective on its next call: the plugin is closed and reopens with the new values.</summary>
    /// <param name="plugin">The plugin, which must be enabled.</param>
    /// <param name="changes">The settings to change; see <see cref="PluginSettingsPipeline.SetAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The settings as they now stand, or why nothing was changed.</returns>
    public async Task<Outcome<IReadOnlyList<SettingState>>> SetPluginSettingsAsync(
        PluginId plugin,
        IReadOnlyList<SettingChange> changes,
        CancellationToken cancellationToken)
    {
        Outcome<IReadOnlyList<SettingState>> states = await this.PluginSettings.SetAsync(plugin, changes, cancellationToken).ConfigureAwait(false);
        if (states.IsSuccess && changes.Count > 0)
        {
            await this.UnloadAsync(plugin, cancellationToken).ConfigureAwait(false);
        }

        return states;
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

    /// <summary>
    /// Picks up changes made behind the runtime's back, such as a plugin installed, replaced or removed by the command
    /// line while an app or server is running: rebuilds every plugin's permissions from the registry and closes every
    /// loaded plugin so each reopens, from its files as they are now, on its next call.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reload.</param>
    /// <returns>The plugins that were quarantined because their stored manifest could not be read.</returns>
    public async Task<IReadOnlyList<PluginId>> ReloadPluginsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<PluginId> unreadable = await this.RefreshPoliciesAsync(cancellationToken).ConfigureAwait(false);
        if (this.wasmGateway is not null)
        {
            await this.wasmGateway.UnloadAllAsync(cancellationToken).ConfigureAwait(false);
        }

        // Every plugin reopens, so the links and tokens it handed out are no longer ones to trust.
        await this.resolutions.ClearAsync(cancellationToken).ConfigureAwait(false);
        return unreadable;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await this.Downloads.DisposeAsync().ConfigureAwait(false);
        if (this.wasmGateway is not null)
        {
            await this.wasmGateway.DisposeAsync().ConfigureAwait(false);
        }

        this.transport?.Dispose();
        this.http?.Dispose();
        this.secretBox.Dispose();
    }

    private static (IAssetFetcherPort Fetcher, HttpClient? Http) CreateFetcher(IAssetFetcherPort? custom, TimeProvider time)
    {
        if (custom is not null)
        {
            return (custom, null);
        }

        // The client owns the handler and the runtime owns the client; the analyzer cannot see the hand-over.
#pragma warning disable CA2000, IDISP004
        HttpClient client = new(HttpAssetFetcher.CreateHandler());
#pragma warning restore CA2000, IDISP004
        HttpAssetFetcher fetcher = new(client, time);
        return (fetcher, client);
    }

    private static void ConfigureNativeLibraries(string? directory)
    {
        // Always isolate SQLite from any other copy in the process; a host rarely knows whether there is one.
        SqliteNativeLibrary.Register();
        if (directory is not null)
        {
            SqliteNativeLibrary.AddSearchDirectory(directory);
            WasmRuntimeHost.AddLibrarySearchDirectory(directory);
        }
    }

    // A plugin whose stored manifest cannot be read is denied everything by the policy snapshot; take it out of service too.
    private async Task<IReadOnlyList<PluginId>> RefreshPoliciesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<PluginId> unreadable = await this.Policies.RefreshAsync(this.Registry, cancellationToken).ConfigureAwait(false);
        foreach (PluginId plugin in unreadable)
        {
            await this.Registry.SetStatusAsync(plugin, PluginStatus.Quarantined, this.Time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }

        return unreadable;
    }

    private async Task UnloadAsync(PluginId plugin, CancellationToken cancellationToken)
    {
        if (this.wasmGateway is not null)
        {
            await this.wasmGateway.UnloadAsync(plugin, cancellationToken).ConfigureAwait(false);
        }

        await this.resolutions.RemovePluginAsync(plugin, cancellationToken).ConfigureAwait(false);
    }
}

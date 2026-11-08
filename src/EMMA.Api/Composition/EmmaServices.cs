using EMMA.Api.Configuration;
using EMMA.Application.Pipelines;
using EMMA.Application.Ports;
using EMMA.Infrastructure.Cache;
using EMMA.Infrastructure.Http;
using EMMA.Infrastructure.Settings;
using EMMA.PluginHost.Install;
using EMMA.PluginHost.Instances;
using EMMA.PluginHost.Policy;
using EMMA.PluginHost.Runtime;
using EMMA.PluginHost.Signing;
using EMMA.Storage;
using EMMA.WasmRuntime;

namespace EMMA.Api.Composition;

/// <summary>The composition root: the one place that decides which implementation backs each port.</summary>
internal static class EmmaServices
{
    /// <summary>Registers EMMA's storage, plugin host and pipelines.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="settings">The validated settings.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddEmma(this IServiceCollection services, ApiSettings settings)
    {
        services.AddSingleton(TimeProvider.System);
        AddStorage(services, settings);
        AddAssets(services, settings);
        AddPluginHost(services, settings);

        services.AddSingleton(PluginCallOptions.Default);
        services.AddSingleton(AssetPipelineOptions.Default);
        services.AddSingleton<PluginCallExecutor>();
        services.AddSingleton<CatalogPipeline>();
        services.AddSingleton<AssetPipeline>();
        services.AddSingleton<ProgressPipeline>();
        services.AddSingleton<HostInitializer>();
        return services;
    }

    private static void AddStorage(IServiceCollection services, ApiSettings settings)
    {
        services.AddSingleton(new StorageOptions(settings.DatabasePath));
        services.AddSingleton<StorageInitializer>();
        services.AddSingleton<IPluginRegistryPort, SqlitePluginRegistry>();
        services.AddSingleton<INodeStorePort, SqliteNodeStore>();
        services.AddSingleton<IProgressPort, SqliteProgressStore>();
        services.AddSingleton<ILibraryPort, SqliteLibraryStore>();
        services.AddSingleton<IDelegationVersionPort, SqliteDelegationVersionStore>();
    }

    private static void AddAssets(IServiceCollection services, ApiSettings settings)
    {
        services.AddSingleton(AssetCacheOptions.Default with { DiskRoot = settings.AssetCacheRoot });
        services.AddSingleton<BoundedAssetCache>();
        services.AddSingleton<IAssetCachePort>(provider => provider.GetRequiredService<BoundedAssetCache>());
        services.AddHttpClient<IAssetFetcherPort, HttpAssetFetcher>()
            .ConfigurePrimaryHttpMessageHandler(HttpAssetFetcher.CreateHandler);
    }

    private static void AddPluginHost(IServiceCollection services, ApiSettings settings)
    {
        services.AddSingleton(new PluginHostOptions(settings.InstallRoot));
        services.AddSingleton(TrustPolicy.Create(settings.Trust.Repositories, settings.Trust.RequireSigned));
        services.AddSingleton<IDelegationSource>(new FileDelegationSource(settings.DelegationsDirectory));
        services.AddSingleton<PluginSignatureVerifier>();
        services.AddSingleton<IPluginSettingsPort, EmptyPluginSettings>();
        services.AddSingleton<IPluginLogPort, LoggingPluginLog>();
        services.AddSingleton<ManifestCapabilityPolicyProvider>();
        services.AddSingleton<ICapabilityPolicyProvider>(provider => provider.GetRequiredService<ManifestCapabilityPolicyProvider>());

        // The native host is created on first use, so a build without it still starts and says what is missing.
        services.AddSingleton(_ => WasmRuntimeHost.Create());
        services.AddSingleton<IPluginTransport, WasmPluginTransport>();
        services.AddSingleton<PluginLoader>();
        services.AddSingleton(new WasmGatewayOptions());
        services.AddSingleton<WasmPluginGateway>();
        services.AddSingleton<IPluginGateway>(provider => provider.GetRequiredService<WasmPluginGateway>());
    }
}

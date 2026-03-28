using EMMA.Application.Ports;
using EMMA.Infrastructure.Cache;
using EMMA.Infrastructure.Http;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using EMMA.PluginHost.Platform;
using EMMA.PluginHost.Sandboxing;
using EMMA.PluginHost.Services;
using EMMA.Storage;

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.Configure<PluginHostOptions>(builder.Configuration.GetSection("PluginHost"));
builder.Services.AddOptions<PluginSignatureOptions>()
    .Bind(builder.Configuration.GetSection("PluginSignature"))
    .PostConfigure(options =>
    {
        typeof(PluginSignatureOptions).GetProperty(nameof(PluginSignatureOptions.RequireSignedPlugins))!
            .SetValue(options, ResolveRequireSignedPlugins(builder.Configuration));

        var configuredKey = ResolvePluginSignatureHmacKey();
        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            typeof(PluginSignatureOptions).GetProperty(nameof(PluginSignatureOptions.HmacKeyBase64))!
                .SetValue(options, configuredKey);
        }
    });
builder.Services.AddSingleton<PluginRegistry>();
builder.Services.AddSingleton<PluginManifestLoader>();
builder.Services.AddSingleton<PluginPermissionSanitizer>();
builder.Services.AddSingleton<IPluginEntrypointResolver, PluginEntrypointResolver>();
builder.Services.AddSingleton<PluginProcessManager>();
builder.Services.AddSingleton<PluginEndpointAllocator>();
builder.Services.AddSingleton<PluginResolutionService>();
builder.Services.AddSingleton<PluginRepositoryStore>();
builder.Services.AddSingleton<PluginRepositoryCatalogClient>();
builder.Services.AddSingleton<PluginRepositoryService>();
builder.Services.AddSingleton<PluginRepositoryInstallOrchestrator>();
builder.Services.AddSingleton<IWasmComponentInvoker, NativeInProcessWasmComponentInvoker>();
builder.Services.AddSingleton<IWasmPluginRuntimeHost, WasmPluginRuntimeHost>();
builder.Services.AddSingleton<IPluginSignatureVerifier, HmacPluginSignatureVerifier>();
builder.Services.AddSingleton(StorageOptions.Default);
builder.Services.AddSingleton<StorageInitializer>();
builder.Services.AddSingleton<TempAssetCleanupService>();
builder.Services.AddSingleton(PageAssetCacheOptions.Default);
builder.Services.AddSingleton<IMediaCatalogPort, SqliteMediaCatalogPort>();
builder.Services.AddSingleton<IPageAssetCachePort>(sp =>
    new BoundedPageAssetCache(sp.GetRequiredService<PageAssetCacheOptions>()));
builder.Services.AddSingleton<IPageAssetFetcherPort, HttpPageAssetFetcher>();
builder.Services.AddSingleton<IPluginSandboxManager>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PluginHostOptions>>();
    return HostPlatformPolicy.Current switch
    {
        HostPlatform.Android => new AndroidPluginSandboxManager(options, sp.GetRequiredService<ILogger<AndroidPluginSandboxManager>>()),
        HostPlatform.AppleMobile => new IosPluginSandboxManager(options, sp.GetRequiredService<ILogger<IosPluginSandboxManager>>()),
        HostPlatform.Windows => new WindowsPluginSandboxManager(options, sp.GetRequiredService<ILogger<WindowsPluginSandboxManager>>()),
        HostPlatform.Linux => new LinuxPluginSandboxManager(options, sp.GetRequiredService<ILogger<LinuxPluginSandboxManager>>()),
        HostPlatform.MacOS => new MacOsPluginSandboxManager(options, sp.GetRequiredService<ILogger<MacOsPluginSandboxManager>>()),
        _ => throw new PlatformNotSupportedException("Unsupported host platform for plugin sandbox manager.")
    };
});
builder.Services.AddSingleton<PluginHandshakeService>();
builder.Services.AddHostedService<PluginHandshakeHostedService>();
builder.Services.AddHostedService<PluginBudgetWatcher>();
builder.Services.AddHostedService<PluginLifecycleHostedService>();
builder.Services.AddHostedService<PluginIdleCleanupHostedService>();

var app = builder.Build();

var storageInitializer = app.Services.GetRequiredService<StorageInitializer>();
await storageInitializer.InitializeAsync(CancellationToken.None);

app.MapGrpcService<PluginControlService>();
app.MapPagedPipelineEndpoints();
app.MapVideoPipelineEndpoints();
app.MapPluginHostEndpoints();
app.MapGet("/", () => "EMMA plugin host is running.");

app.Run();

static bool ResolveRequireSignedPlugins(IConfiguration configuration)
{
    var overrideValue = Environment.GetEnvironmentVariable("EMMA_REQUIRE_SIGNED_PLUGINS")
        ?? Environment.GetEnvironmentVariable("PluginSignature__RequireSignedPlugins");

    if (!string.IsNullOrWhiteSpace(overrideValue))
    {
        if (bool.TryParse(overrideValue, out var parsedBool))
        {
            return parsedBool;
        }

        return overrideValue.Trim() switch
        {
            "1" or "yes" or "on" => true,
            "0" or "no" or "off" => false,
            _ => true
        };
    }

    var configuredValue = configuration["PluginSignature:RequireSignedPlugins"];
    if (!string.IsNullOrWhiteSpace(configuredValue))
    {
        if (bool.TryParse(configuredValue, out var parsedBool))
        {
            return parsedBool;
        }

        return configuredValue.Trim() switch
        {
            "1" or "yes" or "on" => true,
            "0" or "no" or "off" => false,
            _ => true
        };
    }

    return !IsDevelopmentMode();
}

static string? ResolvePluginSignatureHmacKey()
{
    return Environment.GetEnvironmentVariable("EMMA_PLUGIN_SIGNATURE_HMAC_KEY_BASE64")
        ?? Environment.GetEnvironmentVariable("PluginSignature__HmacKeyBase64");
}

static bool IsDevelopmentMode()
{
    var explicitDev = Environment.GetEnvironmentVariable("EMMA_PLUGIN_DEV_MODE");
    if (!string.IsNullOrWhiteSpace(explicitDev))
    {
        if (bool.TryParse(explicitDev, out var parsedBool))
        {
            return parsedBool;
        }

        return explicitDev.Trim() switch
        {
            "1" or "yes" or "on" => true,
            _ => false
        };
    }

    var aspnetEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
    return string.Equals(aspnetEnvironment, "Development", StringComparison.OrdinalIgnoreCase);
}

public partial class Program
{
}

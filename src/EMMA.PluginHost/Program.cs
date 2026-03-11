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
builder.Services.Configure<PluginSignatureOptions>(builder.Configuration.GetSection("PluginSignature"));
builder.Services.AddSingleton<PluginRegistry>();
builder.Services.AddSingleton<PluginManifestLoader>();
builder.Services.AddSingleton<PluginPermissionSanitizer>();
builder.Services.AddSingleton<IPluginEntrypointResolver, PluginEntrypointResolver>();
builder.Services.AddSingleton<PluginProcessManager>();
builder.Services.AddSingleton<PluginEndpointAllocator>();
builder.Services.AddSingleton<PluginResolutionService>();
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
app.MapPluginHostEndpoints();
app.MapGet("/", () => "EMMA plugin host is running.");

app.Run();

public partial class Program
{
}

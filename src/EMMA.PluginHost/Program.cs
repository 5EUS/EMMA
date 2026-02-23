using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using EMMA.PluginHost.Sandboxing;
using EMMA.PluginHost.Services;

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.Configure<PluginHostOptions>(builder.Configuration.GetSection("PluginHost"));
builder.Services.AddSingleton<PluginRegistry>();
builder.Services.AddSingleton<PluginManifestLoader>();
builder.Services.AddSingleton<PluginProcessManager>();
builder.Services.AddSingleton<IPluginSandboxManager>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PluginHostOptions>>();

    if (OperatingSystem.IsAndroid())
    {
        return new AndroidPluginSandboxManager(options, sp.GetRequiredService<ILogger<AndroidPluginSandboxManager>>());
    }

    if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsTvOS())
    {
        return new IosPluginSandboxManager(options, sp.GetRequiredService<ILogger<IosPluginSandboxManager>>());
    }

    if (OperatingSystem.IsWindows())
    {
        return new WindowsPluginSandboxManager(options, sp.GetRequiredService<ILogger<WindowsPluginSandboxManager>>());
    }

    if (OperatingSystem.IsLinux())
    {
        return new LinuxPluginSandboxManager(options, sp.GetRequiredService<ILogger<LinuxPluginSandboxManager>>());
    }

    if (OperatingSystem.IsMacOS())
    {
        return new MacOsPluginSandboxManager(options, sp.GetRequiredService<ILogger<MacOsPluginSandboxManager>>());
    }

    return new NoOpPluginSandboxManager(options, sp.GetRequiredService<ILogger<NoOpPluginSandboxManager>>());
});
builder.Services.AddSingleton<PluginHandshakeService>();
builder.Services.AddHostedService<PluginHandshakeHostedService>();
builder.Services.AddHostedService<PluginBudgetWatcher>();
builder.Services.AddHostedService<PluginLifecycleHostedService>();

var app = builder.Build();

app.MapGrpcService<PluginControlService>();
app.MapPluginHostEndpoints();
app.MapPagedPipelineEndpoints();
app.MapProbeEndpoints();
app.MapGet("/", () => "EMMA plugin host is running.");

app.Run();

public partial class Program
{
}

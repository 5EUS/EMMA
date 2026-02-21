using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using EMMA.PluginHost.Sandboxing;
using EMMA.PluginHost.Services;
using System.Runtime.InteropServices;

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.Configure<PluginHostOptions>(builder.Configuration.GetSection("PluginHost"));
builder.Services.AddSingleton<PluginRegistry>();
builder.Services.AddSingleton<PluginManifestLoader>();
builder.Services.AddSingleton<IPluginSandboxManager>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PluginHostOptions>>();

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) // TODO RuntimeInformation does not report correctly with iOS and Android
    {
        return new WindowsPluginSandboxManager(options, sp.GetRequiredService<ILogger<WindowsPluginSandboxManager>>());
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        return new LinuxPluginSandboxManager(options, sp.GetRequiredService<ILogger<LinuxPluginSandboxManager>>());
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        return new MacOsPluginSandboxManager(options, sp.GetRequiredService<ILogger<MacOsPluginSandboxManager>>());
    }

    return new NoOpPluginSandboxManager(options, sp.GetRequiredService<ILogger<NoOpPluginSandboxManager>>());
});
builder.Services.AddSingleton<PluginHandshakeService>();
builder.Services.AddHostedService<PluginHandshakeHostedService>();
builder.Services.AddHostedService<PluginBudgetWatcher>();

var app = builder.Build();

app.MapGrpcService<PluginControlService>();
app.MapPluginHostEndpoints();
app.MapProbeEndpoints();
app.MapGet("/", () => "EMMA plugin host is running.");

app.Run();

public partial class Program
{
}

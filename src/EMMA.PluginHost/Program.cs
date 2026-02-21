using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using EMMA.PluginHost.Services;

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.Configure<PluginHostOptions>(builder.Configuration.GetSection("PluginHost"));
builder.Services.AddSingleton<PluginRegistry>();
builder.Services.AddSingleton<PluginManifestLoader>();
builder.Services.AddSingleton<PluginHandshakeService>();
builder.Services.AddHostedService<PluginHandshakeHostedService>();

var app = builder.Build();

app.MapGrpcService<PluginControlService>();
app.MapGet("/plugins", (PluginRegistry registry) => registry.GetSnapshot());
app.MapPost("/plugins/refresh", async (PluginHandshakeService handshake, PluginRegistry registry, CancellationToken cancellationToken) =>
{
	await handshake.RescanAsync(cancellationToken);
	return Results.Ok(registry.GetSnapshot());
});
app.MapGet("/", () => "EMMA plugin host is running.");

app.Run();

using EMMA.PluginHost.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();

var app = builder.Build();

app.MapGrpcService<PluginControlService>();
app.MapGet("/", () => "EMMA plugin host is running.");

app.Run();

public partial class Program
{
}

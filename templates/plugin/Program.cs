using System.Net;
using System.Net.Sockets;
using EMMA.PluginTemplate.Infrastructure;
using EMMA.PluginTemplate.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace EMMA.PluginTemplate;

public static class Program
{
    public static void Main(string[] args)
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var port = PluginEnvironment.GetPort(args, 5005);

        var builder = WebApplication.CreateBuilder(args);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
            });

            if (Socket.OSSupportsIPv6)
            {
                options.Listen(IPAddress.IPv6Loopback, port, listen =>
                {
                    listen.Protocols = HttpProtocols.Http2;
                });
            }
        });

        builder.Services.AddGrpc();
        builder.Services.AddHttpClient<HttpJsonClient>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("EMMA-PluginTemplate/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });

        var app = builder.Build();

        app.MapGrpcService<PluginControlService>();
        app.MapGrpcService<SearchProviderService>();
        app.MapGrpcService<PageProviderService>();
        app.MapGrpcService<VideoProviderService>();
        app.MapGet("/", () => "EMMA plugin template is running.");

        app.Run();
    }
}

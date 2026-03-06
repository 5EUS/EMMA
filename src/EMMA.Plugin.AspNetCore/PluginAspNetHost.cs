using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace EMMA.Plugin.AspNetCore;

public sealed record PluginAspNetHostOptions(
    int DefaultPort,
    IReadOnlyList<string> PortEnvironmentVariables,
    string PortArgumentName = "--port",
    string RootMessage = "EMMA plugin is running.");

public static class PluginAspNetHost
{
    public static WebApplication Create(
        string[] args,
        PluginAspNetHostOptions options,
        Action<IServiceCollection> configureServices)
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var builder = WebApplication.CreateBuilder(args);
        var port = ResolvePort(args, options);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, port, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
            });

            if (Socket.OSSupportsIPv6)
            {
                kestrel.Listen(IPAddress.IPv6Loopback, port, listen =>
                {
                    listen.Protocols = HttpProtocols.Http2;
                });
            }
        });

        configureServices(builder.Services);

        return builder.Build();
    }

    public static void MapDefaultEndpoints(WebApplication app, PluginAspNetHostOptions options)
    {
        app.MapGet("/", () => options.RootMessage);
    }

    private static int ResolvePort(string[] args, PluginAspNetHostOptions options)
    {
        foreach (var envName in options.PortEnvironmentVariables)
        {
            var envValue = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(envValue) && int.TryParse(envValue, out var parsedEnvPort))
            {
                return parsedEnvPort;
            }
        }

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], options.PortArgumentName, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var parsedArgPort))
            {
                return parsedArgPort;
            }
        }

        return options.DefaultPort;
    }
}

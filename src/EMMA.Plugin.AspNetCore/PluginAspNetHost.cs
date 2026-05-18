using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace EMMA.Plugin.AspNetCore;

/// <summary>
/// Describes the port and root-endpoint configuration for an ASP.NET Core plugin host.
/// </summary>
/// <param name="DefaultPort">The fallback port used when no override is present.</param>
/// <param name="PortEnvironmentVariables">The environment variables inspected for port overrides.</param>
/// <param name="PortArgumentName">The command-line argument name used for port overrides.</param>
/// <param name="RootMessage">The message returned from the informational root endpoint.</param>
public sealed record PluginAspNetHostOptions(
    int DefaultPort,
    IReadOnlyList<string> PortEnvironmentVariables,
    string PortArgumentName = "--port",
    string RootMessage = "EMMA plugin is running.");

/// <summary>
/// Creates and configures an ASP.NET Core host for EMMA plugin transports.
/// </summary>
public static class PluginAspNetHost
{
    /// <summary>
    /// Creates a Kestrel-backed plugin host configured for EMMA gRPC traffic.
    /// </summary>
    /// <param name="args">The command-line arguments provided to the plugin process.</param>
    /// <param name="options">The host port and root endpoint configuration.</param>
    /// <param name="configureServices">Registers services required by the plugin host.</param>
    /// <returns>A built web application configured to listen on the resolved loopback port.</returns>
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

    /// <summary>
    /// Maps the default informational root endpoint for the plugin host.
    /// </summary>
    /// <param name="app">The application to attach the endpoint to.</param>
    /// <param name="options">The host options that provide the root response message.</param>
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

        if (!string.IsNullOrWhiteSpace(options.PortArgumentName))
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], options.PortArgumentName, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(args[i + 1], out var parsedArgPort))
                {
                    return parsedArgPort;
                }
            }
        }

        return options.DefaultPort;
    }
}

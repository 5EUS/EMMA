using System.Reflection;
using EMMA.Plugin.Common;
using EMMA.Contracts.Plugins;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace EMMA.Plugin.AspNetCore;

/// <summary>
/// Configures request-authentication and correlation settings for the plugin SDK host.
/// </summary>
public sealed class PluginSdkSecurityOptions
{
    /// <summary>
    /// Gets or sets the header name used for host authentication.
    /// </summary>
    public string HostAuthHeaderName { get; set; } = "x-emma-plugin-host-auth";

    /// <summary>
    /// Gets or sets the environment variable that stores the expected host authentication token.
    /// </summary>
    public string HostAuthTokenEnvironmentVariable { get; set; } = "EMMA_PLUGIN_HOST_AUTH_TOKEN";

    /// <summary>
    /// Gets or sets the header name used for correlation identifiers.
    /// </summary>
    public string CorrelationIdHeaderName { get; set; } = "x-correlation-id";
}

/// <summary>
/// Configures the control-service payload returned by the plugin SDK host.
/// </summary>
public sealed class PluginSdkControlOptions
{
    /// <summary>
    /// Gets or sets the health status string.
    /// </summary>
    public string Status { get; set; } = "ok";

    /// <summary>
    /// Gets or sets the health message.
    /// </summary>
    public string Message { get; set; } = "EMMA plugin ready";

    /// <summary>
    /// Gets or sets an optional version override.
    /// </summary>
    public string? VersionOverride { get; set; }

    /// <summary>
    /// Gets or sets the CPU budget in milliseconds.
    /// </summary>
    public int CpuBudgetMs { get; set; }

    /// <summary>
    /// Gets or sets the memory budget in megabytes.
    /// </summary>
    public int MemoryMb { get; set; }

    /// <summary>
    /// Gets the advertised capability names.
    /// </summary>
    public IList<string> Capabilities { get; } = ["health", "capabilities"];

    /// <summary>
    /// Gets the allowed network domains.
    /// </summary>
    public IList<string> Domains { get; } = [];

    /// <summary>
    /// Gets the allowed filesystem paths.
    /// </summary>
    public IList<string> Paths { get; } = [];
}

/// <summary>
/// Defines a reusable set of default control-service values.
/// </summary>
/// <param name="Message">The default control message.</param>
/// <param name="CpuBudgetMs">The default CPU budget in milliseconds.</param>
/// <param name="MemoryMb">The default memory budget in megabytes.</param>
/// <param name="Capabilities">The default capability names.</param>
/// <param name="Domains">The default allowed domains.</param>
/// <param name="Paths">The default allowed paths.</param>
public sealed record PluginSdkControlDefaults(
    string Message,
    int CpuBudgetMs,
    int MemoryMb,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string>? Domains = null,
    IReadOnlyList<string>? Paths = null);

/// <summary>
/// Describes how manifest defaults and host defaults should be resolved for a plugin SDK host.
/// </summary>
/// <param name="PluginManifestFileName">The manifest file name to load.</param>
/// <param name="Fallback">The fallback manifest defaults.</param>
/// <param name="PluginProjectFolderName">The optional plugin project folder name used when probing for the manifest.</param>
/// <param name="DefaultPort">The fallback host port.</param>
/// <param name="DevelopmentPortEnvironmentVariables">The environment variables inspected for development-mode ports.</param>
/// <param name="ProductionPortEnvironmentVariables">The environment variables inspected for production-mode ports.</param>
/// <param name="DevelopmentPortArgumentName">The CLI argument used for development-mode port overrides.</param>
/// <param name="ProductionPortArgumentName">The CLI argument used for production-mode port overrides.</param>
/// <param name="RootMessage">The message returned from the informational root endpoint.</param>
public sealed record PluginSdkManifestDefaultsOptions(
    string PluginManifestFileName,
    PluginManifestDefaults Fallback,
    string? PluginProjectFolderName = null,
    int DefaultPort = 5000,
    IReadOnlyList<string>? DevelopmentPortEnvironmentVariables = null,
    IReadOnlyList<string>? ProductionPortEnvironmentVariables = null,
    string DevelopmentPortArgumentName = "--port",
    string ProductionPortArgumentName = "",
    string RootMessage = "EMMA plugin is running.");

internal sealed record PluginDevEnrichSearchRequest(IReadOnlyList<SearchItem>? Items);
internal sealed record PluginDevVideoStreamsRequest(string MediaId);
internal sealed record PluginDevVideoSegmentRequest(string MediaId, string StreamId, int Sequence);

/// <summary>
/// Applies manifest-derived and explicit defaults to <see cref="PluginSdkControlOptions"/> instances.
/// </summary>
public static class PluginSdkControlOptionsExtensions
{
    /// <summary>
    /// Copies manifest-derived control defaults onto the current control options instance.
    /// </summary>
    /// <param name="options">The control options instance to populate.</param>
    /// <param name="defaults">The manifest defaults to apply.</param>
    /// <returns>The same options instance for fluent configuration.</returns>
    public static PluginSdkControlOptions ApplyManifestDefaults(
        this PluginSdkControlOptions options,
        PluginManifestDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.CpuBudgetMs = defaults.CpuBudgetMs;
        options.MemoryMb = defaults.MemoryMb;

        options.Domains.Clear();
        foreach (var domain in defaults.Domains)
        {
            options.Domains.Add(domain);
        }

        options.Paths.Clear();
        foreach (var path in defaults.Paths)
        {
            options.Paths.Add(path);
        }

        return options;
    }

    /// <summary>
    /// Copies explicit SDK control defaults onto the current control options instance.
    /// </summary>
    /// <param name="options">The control options instance to populate.</param>
    /// <param name="defaults">The default values to apply.</param>
    /// <returns>The same options instance for fluent configuration.</returns>
    public static PluginSdkControlOptions ApplyDefaults(
        this PluginSdkControlOptions options,
        PluginSdkControlDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(defaults);

        options.Message = defaults.Message;
        options.ApplyManifestDefaults(new PluginManifestDefaults(
            defaults.CpuBudgetMs,
            defaults.MemoryMb,
            defaults.Domains?.ToArray() ?? [],
            defaults.Paths?.ToArray() ?? []));

        options.Capabilities.Clear();
        foreach (var capability in defaults.Capabilities)
        {
            options.Capabilities.Add(capability);
        }

        return options;
    }
}

/// <summary>
/// Implements the default control gRPC service for the plugin SDK host.
/// </summary>
/// <param name="options">The configured control options.</param>
public sealed class PluginDefaultControlService(IOptions<PluginSdkControlOptions> options)
    : PluginControl.PluginControlBase
{
    private readonly PluginSdkControlOptions _options = options.Value;

    /// <summary>
    /// Returns the current health payload for the plugin control service.
    /// </summary>
    /// <param name="request">The incoming health request.</param>
    /// <param name="context">The active gRPC server call context.</param>
    /// <returns>A health response populated from the configured control options.</returns>
    public override Task<HealthResponse> GetHealth(HealthRequest request, ServerCallContext context)
    {
        var version = _options.VersionOverride
            ?? typeof(PluginDefaultControlService).Assembly.GetName().Version?.ToString()
            ?? "dev";

        return Task.FromResult(new HealthResponse
        {
            Status = _options.Status,
            Version = version,
            Message = _options.Message
        });
    }

    /// <summary>
    /// Returns the plugin capability payload for the control service.
    /// </summary>
    /// <param name="request">The incoming capabilities request.</param>
    /// <param name="context">The active gRPC server call context.</param>
    /// <returns>A capabilities response built from the configured control options.</returns>
    public override Task<CapabilitiesResponse> GetCapabilities(CapabilitiesRequest request, ServerCallContext context)
    {
        var response = new CapabilitiesResponse
        {
            Budgets = new CapabilityBudgets
            {
                CpuBudgetMs = _options.CpuBudgetMs,
                MemoryMb = _options.MemoryMb
            },
            Permissions = new CapabilityPermissions()
        };

        response.Capabilities.AddRange(_options.Capabilities.Distinct(StringComparer.OrdinalIgnoreCase));
        response.Permissions.Domains.AddRange(_options.Domains.Distinct(StringComparer.OrdinalIgnoreCase));
        response.Permissions.Paths.AddRange(_options.Paths.Distinct(StringComparer.OrdinalIgnoreCase));

        return Task.FromResult(response);
    }
}

/// <summary>
/// Validates host authentication and request liveness for plugin gRPC calls.
/// </summary>
/// <param name="options">The configured security options.</param>
public sealed class PluginRpcSecurityInterceptor(IOptions<PluginSdkSecurityOptions> options) : Interceptor
{
    private readonly PluginSdkSecurityOptions _options = options.Value;

    /// <summary>
    /// Validates plugin host authentication for unary RPC calls before invoking the service handler.
    /// </summary>
    /// <typeparam name="TRequest">The unary request type.</typeparam>
    /// <typeparam name="TResponse">The unary response type.</typeparam>
    /// <param name="request">The incoming request message.</param>
    /// <param name="context">The active gRPC server call context.</param>
    /// <param name="continuation">The next handler in the gRPC pipeline.</param>
    /// <returns>The task produced by the validated request handler.</returns>
    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        EnsureAuthorizedAndActive(context);
        return continuation(request, context);
    }

    /// <summary>
    /// Validates plugin host authentication for server-streaming RPC calls before invoking the service handler.
    /// </summary>
    /// <typeparam name="TRequest">The streaming request type.</typeparam>
    /// <typeparam name="TResponse">The streaming response type.</typeparam>
    /// <param name="request">The incoming request message.</param>
    /// <param name="responseStream">The response stream used to write messages.</param>
    /// <param name="context">The active gRPC server call context.</param>
    /// <param name="continuation">The next handler in the gRPC pipeline.</param>
    /// <returns>A task that completes when the validated stream handler finishes.</returns>
    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        EnsureAuthorizedAndActive(context);
        await continuation(request, responseStream, context);
    }

    /// <summary>
    /// Validates plugin host authentication for client-streaming RPC calls before invoking the service handler.
    /// </summary>
    /// <typeparam name="TRequest">The streaming request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="requestStream">The request stream containing client messages.</param>
    /// <param name="context">The active gRPC server call context.</param>
    /// <param name="continuation">The next handler in the gRPC pipeline.</param>
    /// <returns>The response task produced by the validated stream handler.</returns>
    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        EnsureAuthorizedAndActive(context);
        return await continuation(requestStream, context);
    }

    /// <summary>
    /// Validates plugin host authentication for duplex-streaming RPC calls before invoking the service handler.
    /// </summary>
    /// <typeparam name="TRequest">The streaming request type.</typeparam>
    /// <typeparam name="TResponse">The streaming response type.</typeparam>
    /// <param name="requestStream">The request stream containing client messages.</param>
    /// <param name="responseStream">The response stream used to write messages.</param>
    /// <param name="context">The active gRPC server call context.</param>
    /// <param name="continuation">The next handler in the gRPC pipeline.</param>
    /// <returns>A task that completes when the validated duplex handler finishes.</returns>
    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        EnsureAuthorizedAndActive(context);
        await continuation(requestStream, responseStream, context);
    }

    private void EnsureAuthorizedAndActive(ServerCallContext context)
    {
        if (context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "Request was cancelled."));
        }

        if (context.Deadline != DateTime.MaxValue
            && context.Deadline.ToUniversalTime() <= DateTime.UtcNow)
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Request deadline exceeded."));
        }

        var expectedToken = Environment.GetEnvironmentVariable(_options.HostAuthTokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(expectedToken))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Plugin host auth token is not configured."));
        }

        var providedToken = context.RequestHeaders?.Get(_options.HostAuthHeaderName)?.Value;
        if (string.IsNullOrWhiteSpace(providedToken)
            || !string.Equals(providedToken, expectedToken, StringComparison.Ordinal))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Plugin host authentication failed."));
        }
    }
}

/// <summary>
/// Resolves request-scoped metadata such as correlation identifiers.
/// </summary>
public static class PluginRequestContext
{
    /// <summary>
    /// Resolves the correlation identifier for the current plugin request.
    /// </summary>
    /// <param name="context">The active gRPC server call context.</param>
    /// <param name="requestCorrelationId">An explicit request correlation identifier, when one was provided by the caller.</param>
    /// <param name="fallbackValue">The value to use when no correlation identifier is available.</param>
    /// <param name="headerName">The request header name to inspect for the correlation identifier.</param>
    /// <returns>The explicit correlation identifier, the request header value, or the fallback value.</returns>
    public static string GetCorrelationId(
        ServerCallContext context,
        string? requestCorrelationId = null,
        string fallbackValue = "unknown",
        string headerName = "x-correlation-id")
    {
        if (!string.IsNullOrWhiteSpace(requestCorrelationId))
        {
            return requestCorrelationId;
        }

        var header = context.RequestHeaders?.Get(headerName);
        return string.IsNullOrWhiteSpace(header?.Value) ? fallbackValue : header.Value;
    }
}

/// <summary>
/// Collects gRPC services that should be mapped by the plugin SDK host.
/// </summary>
public sealed class PluginEndpointBuilder
{
    private readonly List<Type> _serviceTypes = [];

    /// <summary>
    /// Registers a gRPC service type to be mapped when the plugin host starts.
    /// </summary>
    /// <typeparam name="TService">The gRPC service implementation type.</typeparam>
    /// <returns>The current endpoint builder.</returns>
    public PluginEndpointBuilder AddGrpcService<TService>() where TService : class
    {
        var serviceType = typeof(TService);
        if (!_serviceTypes.Contains(serviceType))
        {
            _serviceTypes.Add(serviceType);
        }

        return this;
    }

    /// <summary>
    /// Registers a control service to be mapped when the plugin host starts.
    /// </summary>
    /// <typeparam name="TService">The control service implementation type.</typeparam>
    /// <returns>The current endpoint builder.</returns>
    public PluginEndpointBuilder AddControlService<TService>() where TService : class => AddGrpcService<TService>();

    /// <summary>
    /// Registers a search provider service to be mapped when the plugin host starts.
    /// </summary>
    /// <typeparam name="TService">The search provider implementation type.</typeparam>
    /// <returns>The current endpoint builder.</returns>
    public PluginEndpointBuilder AddSearchProvider<TService>() where TService : class => AddGrpcService<TService>();

    /// <summary>
    /// Registers a page provider service to be mapped when the plugin host starts.
    /// </summary>
    /// <typeparam name="TService">The page provider implementation type.</typeparam>
    /// <returns>The current endpoint builder.</returns>
    public PluginEndpointBuilder AddPageProvider<TService>() where TService : class => AddGrpcService<TService>();

    /// <summary>
    /// Registers a video provider service to be mapped when the plugin host starts.
    /// </summary>
    /// <typeparam name="TService">The video provider implementation type.</typeparam>
    /// <returns>The current endpoint builder.</returns>
    public PluginEndpointBuilder AddVideoProvider<TService>() where TService : class => AddGrpcService<TService>();

    internal PluginEndpointRegistry BuildRegistry() => new(_serviceTypes);
}

internal sealed class PluginEndpointRegistry(IReadOnlyList<Type> serviceTypes)
{
    public IReadOnlyList<Type> ServiceTypes { get; } = serviceTypes;
}

/// <summary>
/// Creates, configures, and runs ASP.NET Core plugin SDK hosts with gRPC endpoints and host authentication.
/// </summary>
public static class PluginSdkHost
{
    private static readonly MethodInfo MapGrpcServiceMethod =
        typeof(GrpcEndpointRouteBuilderExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method =>
                string.Equals(method.Name, nameof(GrpcEndpointRouteBuilderExtensions.MapGrpcService), StringComparison.Ordinal)
                && method.IsGenericMethodDefinition
                && method.GetParameters().Length == 1);

    /// <summary>
    /// Creates a plugin web application with gRPC, security, telemetry, and registered endpoint services.
    /// </summary>
    /// <param name="args">The command-line arguments provided to the plugin process.</param>
    /// <param name="hostOptions">The host port and root endpoint configuration.</param>
    /// <param name="configureServices">Registers plugin services in the dependency injection container.</param>
    /// <param name="configureEndpoints">Registers gRPC endpoints to be mapped when the app starts.</param>
    /// <param name="configureSecurity">Optionally customizes plugin-host authentication settings.</param>
    /// <returns>A built web application ready for endpoint mapping and execution.</returns>
    public static WebApplication Create(
        string[] args,
        PluginAspNetHostOptions hostOptions,
        Action<IServiceCollection> configureServices,
        Action<PluginEndpointBuilder> configureEndpoints,
        Action<PluginSdkSecurityOptions>? configureSecurity = null)
    {
        var endpointBuilder = new PluginEndpointBuilder();
        configureEndpoints(endpointBuilder);

        var app = PluginAspNetHost.Create(args, hostOptions, services =>
        {
            services.AddGrpc(grpc =>
            {
                grpc.Interceptors.Add<PluginRpcSecurityInterceptor>();
                grpc.EnableDetailedErrors = PluginEnvironment.IsDevelopmentMode();
            });
            services.AddOptions<PluginSdkSecurityOptions>();
            services.TryAddSingleton<IPluginSdkMetrics>(_ =>
            {
                var pluginId = Environment.GetEnvironmentVariable("EMMA_PLUGIN_ID")?.Trim();
                return new MeteredPluginSdkMetrics(string.IsNullOrWhiteSpace(pluginId) ? "unknown" : pluginId);
            });

            if (configureSecurity is not null)
            {
                services.Configure(configureSecurity);
            }

            services.AddSingleton(endpointBuilder.BuildRegistry());
            configureServices(services);
        });

        return app;
    }

    /// <summary>
    /// Maps all gRPC services that were registered with the plugin endpoint builder.
    /// </summary>
    /// <param name="app">The application to map services on.</param>
    public static void MapRegisteredGrpcServices(WebApplication app)
    {
        var registry = app.Services.GetRequiredService<PluginEndpointRegistry>();
        foreach (var serviceType in registry.ServiceTypes)
        {
            var genericMethod = MapGrpcServiceMethod.MakeGenericMethod(serviceType);
            _ = genericMethod.Invoke(null, [app]);
        }
    }

    /// <summary>
    /// Creates, maps, and runs a plugin web application in one step.
    /// </summary>
    /// <param name="args">The command-line arguments provided to the plugin process.</param>
    /// <param name="hostOptions">The host port and root endpoint configuration.</param>
    /// <param name="configureServices">Registers plugin services in the dependency injection container.</param>
    /// <param name="configureEndpoints">Registers gRPC endpoints to be mapped when the app starts.</param>
    /// <param name="mapDefaultEndpoints">When set, maps the default informational and development endpoints.</param>
    /// <param name="configureSecurity">Optionally customizes plugin-host authentication settings.</param>
    public static void Run(
        string[] args,
        PluginAspNetHostOptions hostOptions,
        Action<IServiceCollection> configureServices,
        Action<PluginEndpointBuilder> configureEndpoints,
        bool mapDefaultEndpoints = false,
        Action<PluginSdkSecurityOptions>? configureSecurity = null)
    {
        var app = Create(args, hostOptions, configureServices, configureEndpoints, configureSecurity);

        if (mapDefaultEndpoints)
        {
            MapDefaultEndpoints(app, hostOptions);
        }

        MapRegisteredGrpcServices(app);
        app.Run();
    }

    private static void MapDefaultEndpoints(WebApplication app, PluginAspNetHostOptions hostOptions)
    {
        PluginAspNetHost.MapDefaultEndpoints(app, hostOptions);

        if (app.Services.GetService<IPluginSearchMetadataRuntime>() is not null)
        {
            app.MapPost("/dev/search/enrich", async (
                PluginDevEnrichSearchRequest request,
                IPluginSearchMetadataRuntime runtime,
                IOptions<PluginSdkSecurityOptions> securityOptions,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                var authorizationError = EnsureAuthorized(httpContext, securityOptions.Value);
                if (authorizationError is not null)
                {
                    return authorizationError;
                }

                var items = request.Items?.Where(static item => !string.IsNullOrWhiteSpace(item.id)).ToArray() ?? [];
                var enriched = await runtime.EnrichSearchItemsAsync(items, cancellationToken);
                return Results.Json(enriched);
            });
        }

        if (app.Services.GetService<IPluginVideoRuntime>() is not null)
        {
            app.MapPost("/dev/video/streams", async (
                PluginDevVideoStreamsRequest request,
                IPluginVideoRuntime runtime,
                IOptions<PluginSdkSecurityOptions> securityOptions,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                var authorizationError = EnsureAuthorized(httpContext, securityOptions.Value);
                if (authorizationError is not null)
                {
                    return authorizationError;
                }

                var response = await runtime.GetStreamsAsync(request.MediaId, cancellationToken);
                var streams = response.Streams.Select(static stream => new
                {
                    stream.Id,
                    stream.Label,
                    stream.PlaylistUri,
                    stream.RequestHeaders,
                    stream.RequestCookies,
                    stream.StreamType,
                    stream.IsLive,
                    stream.DrmProtected,
                    stream.DrmScheme,
                    AudioTracks = stream.AudioTracks,
                    SubtitleTracks = stream.SubtitleTracks,
                    stream.DefaultAudioTrackId,
                    stream.DefaultSubtitleTrackId
                });

                return Results.Json(streams);
            });

            app.MapPost("/dev/video/segment", async (
                PluginDevVideoSegmentRequest request,
                IPluginVideoRuntime runtime,
                IOptions<PluginSdkSecurityOptions> securityOptions,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                var authorizationError = EnsureAuthorized(httpContext, securityOptions.Value);
                if (authorizationError is not null)
                {
                    return authorizationError;
                }

                var response = await runtime.GetSegmentAsync(request.MediaId, request.StreamId, request.Sequence, cancellationToken);
                var payload = response.Payload.ToByteArray();
                return Results.Json(new
                {
                    ContentType = string.IsNullOrWhiteSpace(response.ContentType) ? "application/octet-stream" : response.ContentType,
                    PayloadBase64 = Convert.ToBase64String(payload),
                    SizeBytes = payload.Length
                });
            });
        }
    }

    private static IResult? EnsureAuthorized(HttpContext httpContext, PluginSdkSecurityOptions options)
    {
        var expectedToken = Environment.GetEnvironmentVariable(options.HostAuthTokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(expectedToken))
        {
            return Results.Problem("Plugin host auth token is not configured.", statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!httpContext.Request.Headers.TryGetValue(options.HostAuthHeaderName, out var providedToken)
            || StringValues.IsNullOrEmpty(providedToken)
            || !string.Equals(providedToken.ToString(), expectedToken, StringComparison.Ordinal))
        {
            return Results.Problem("Plugin host authentication failed.", statusCode: StatusCodes.Status401Unauthorized);
        }

        return null;
    }
}

/// <summary>
/// Builds and runs an ASP.NET Core plugin SDK host with optional defaults and service registrations.
/// </summary>
public sealed class PluginBuilder
{
    private readonly string[] _args;
    private readonly PluginAspNetHostOptions _hostOptions;
    private readonly List<Action<IServiceCollection>> _serviceRegistrations = [];
    private readonly Action<PluginEndpointBuilder> _configureEndpoints = _ => { };
    private Action<PluginEndpointBuilder> _endpointRegistrations = _ => { };
    private Action<PluginSdkSecurityOptions>? _security;
    private Action<PluginSdkControlOptions>? _defaultControl;
    private bool _usesDefaultControlService;

    private PluginBuilder(string[] args, PluginAspNetHostOptions hostOptions)
    {
        _args = args;
        _hostOptions = hostOptions;
    }

    /// <summary>
    /// Creates a plugin builder with explicit host options.
    /// </summary>
    /// <param name="args">The command-line arguments provided to the plugin process.</param>
    /// <param name="hostOptions">The host options to use when the plugin runs.</param>
    /// <returns>A new plugin builder.</returns>
    public static PluginBuilder Create(string[] args, PluginAspNetHostOptions hostOptions)
        => new(args, hostOptions);

    /// <summary>
    /// Creates a plugin builder using manifest defaults and environment-aware host settings.
    /// </summary>
    /// <param name="args">The command-line arguments provided to the plugin process.</param>
    /// <param name="options">The manifest and host default options to resolve.</param>
    /// <returns>A configured plugin builder with the default control service enabled.</returns>
    public static PluginBuilder CreateWithDefaults(
        string[] args,
        PluginSdkManifestDefaultsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var manifestDefaults = PluginManifestDefaultsProvider.Load(
            options.PluginManifestFileName,
            options.Fallback,
            options.PluginProjectFolderName);

        var devMode = PluginEnvironment.IsDevelopmentMode();
        var hostOptions = new PluginAspNetHostOptions(
            DefaultPort: PluginEnvironment.GetPort(args, options.DefaultPort),
            PortEnvironmentVariables: devMode
                ? options.DevelopmentPortEnvironmentVariables?.ToArray() ?? ["EMMA_PLUGIN_PORT"]
                : options.ProductionPortEnvironmentVariables?.ToArray() ?? ["EMMA_PLUGIN_PORT"],
            PortArgumentName: devMode ? options.DevelopmentPortArgumentName : options.ProductionPortArgumentName,
            RootMessage: options.RootMessage);

        return new PluginBuilder(args, hostOptions)
            .UseDefaultControlService(control => control.ApplyManifestDefaults(manifestDefaults));
    }

    /// <summary>
    /// Adds custom service registrations to the plugin dependency injection container.
    /// </summary>
    /// <param name="configure">Configures service registrations for the plugin.</param>
    /// <returns>The current plugin builder.</returns>
    public PluginBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        _serviceRegistrations.Add(configure);
        return this;
    }

    /// <summary>
    /// Adds or composes plugin-host authentication configuration.
    /// </summary>
    /// <param name="configure">Applies authentication-related settings.</param>
    /// <returns>The current plugin builder.</returns>
    public PluginBuilder ConfigureSecurity(Action<PluginSdkSecurityOptions> configure)
    {
        _security = _security is null
            ? configure
            : _security + configure;
        return this;
    }

    /// <summary>
    /// Adds or composes default control-service configuration.
    /// </summary>
    /// <param name="configure">Applies control response settings.</param>
    /// <returns>The current plugin builder.</returns>
    public PluginBuilder ConfigureDefaultControl(Action<PluginSdkControlOptions> configure)
    {
        _defaultControl = _defaultControl is null
            ? configure
            : _defaultControl + configure;
        return this;
    }

    /// <summary>
    /// Enables the default control service and optionally customizes its response settings.
    /// </summary>
    /// <param name="configure">Optional control configuration applied before the host runs.</param>
    /// <returns>The current plugin builder.</returns>
    public PluginBuilder UseDefaultControlService(Action<PluginSdkControlOptions>? configure = null)
    {
        if (configure is not null)
        {
            ConfigureDefaultControl(configure);
        }

        if (_usesDefaultControlService)
        {
            return this;
        }

        _usesDefaultControlService = true;
        return AddControlService<PluginDefaultControlService>();
    }

    /// <summary>
    /// Enables the default control service using a predefined default set.
    /// </summary>
    /// <param name="defaults">The default control values to apply.</param>
    /// <returns>The current plugin builder.</returns>
    public PluginBuilder UseDefaultControlService(PluginSdkControlDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        return UseDefaultControlService(options => options.ApplyDefaults(defaults));
    }

    /// <summary>
    /// Registers a gRPC service for endpoint mapping.
    /// </summary>
    /// <typeparam name="TService">The gRPC service implementation type.</typeparam>
    /// <returns>The current plugin builder.</returns>
    public PluginBuilder AddGrpcService<TService>() where TService : class
    {
        _endpointRegistrations += endpoints => endpoints.AddGrpcService<TService>();
        return this;
    }

    /// <summary>
    /// Registers a control service for endpoint mapping.
    /// </summary>
    /// <typeparam name="TService">The control service implementation type.</typeparam>
    /// <returns>The current plugin builder.</returns>
    public PluginBuilder AddControlService<TService>() where TService : class
    {
        _endpointRegistrations += endpoints => endpoints.AddControlService<TService>();
        return this;
    }

    /// <summary>
    /// Registers a search provider service for endpoint mapping.
    /// </summary>
    /// <typeparam name="TService">The search provider implementation type.</typeparam>
    /// <returns>The current plugin builder.</returns>
    public PluginBuilder AddSearchProvider<TService>() where TService : class
    {
        _endpointRegistrations += endpoints => endpoints.AddSearchProvider<TService>();
        return this;
    }

    /// <summary>
    /// Registers the default search provider service for the given paged runtime.
    /// </summary>
    /// <typeparam name="TRuntime">The paged runtime implementation type.</typeparam>
    /// <returns>The current plugin builder.</returns>
    public PluginBuilder AddDefaultSearchProvider<TRuntime>()
        where TRuntime : class, IPluginPagedMediaRuntime
    {
        return AddSearchProvider<PluginDefaultSearchProviderService<TRuntime>>();
    }

    /// <summary>
    /// Registers a page provider service for endpoint mapping.
    /// </summary>
    /// <typeparam name="TService">The page provider implementation type.</typeparam>
    /// <returns>The current plugin builder.</returns>
    public PluginBuilder AddPageProvider<TService>() where TService : class
    {
        _endpointRegistrations += endpoints => endpoints.AddPageProvider<TService>();
        return this;
    }

    /// <summary>
    /// Registers the default page provider service for the given paged runtime.
    /// </summary>
    /// <typeparam name="TRuntime">The paged runtime implementation type.</typeparam>
    /// <returns>The current plugin builder.</returns>
    public PluginBuilder AddDefaultPageProvider<TRuntime>()
        where TRuntime : class, IPluginPagedMediaRuntime
    {
        return AddPageProvider<PluginDefaultPageProviderService<TRuntime>>();
    }

    /// <summary>
    /// Registers the default search and page providers for the given paged runtime.
    /// </summary>
    /// <typeparam name="TRuntime">The paged runtime implementation type.</typeparam>
    /// <returns>The current plugin builder.</returns>
    public PluginBuilder AddDefaultPagedProviders<TRuntime>()
        where TRuntime : class, IPluginPagedMediaRuntime
    {
        return AddDefaultSearchProvider<TRuntime>()
            .AddDefaultPageProvider<TRuntime>();
    }

            /// <summary>
            /// Registers a video provider service for endpoint mapping.
            /// </summary>
            /// <typeparam name="TService">The video provider implementation type.</typeparam>
            /// <returns>The current plugin builder.</returns>
    public PluginBuilder AddVideoProvider<TService>() where TService : class
    {
        _endpointRegistrations += endpoints => endpoints.AddVideoProvider<TService>();
        return this;
    }

    /// <summary>
    /// Registers the default video provider service for the given video runtime.
    /// </summary>
    /// <typeparam name="TRuntime">The video runtime implementation type.</typeparam>
    /// <returns>The current plugin builder.</returns>
    public PluginBuilder AddDefaultVideoProvider<TRuntime>()
        where TRuntime : class, IPluginVideoRuntime
    {
        return AddVideoProvider<PluginDefaultVideoProviderService<TRuntime>>();
    }

    /// <summary>
    /// Builds and runs the configured plugin host.
    /// </summary>
    /// <param name="mapDefaultEndpoints">When set, maps the default informational and development endpoints before the app starts.</param>
    public void Run(bool mapDefaultEndpoints = false)
    {
        PluginSdkHost.Run(
            _args,
            _hostOptions,
            configureServices: services =>
            {
                services.AddOptions<PluginSdkControlOptions>();
                if (_defaultControl is not null)
                {
                    services.Configure(_defaultControl);
                }

                foreach (var registration in _serviceRegistrations)
                {
                    registration(services);
                }
            },
            configureEndpoints: endpoints =>
            {
                _configureEndpoints(endpoints);
                _endpointRegistrations(endpoints);
            },
            mapDefaultEndpoints: mapDefaultEndpoints,
            configureSecurity: _security);
    }
}
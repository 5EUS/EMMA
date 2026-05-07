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

public sealed class PluginSdkSecurityOptions
{
    public string HostAuthHeaderName { get; set; } = "x-emma-plugin-host-auth";
    public string HostAuthTokenEnvironmentVariable { get; set; } = "EMMA_PLUGIN_HOST_AUTH_TOKEN";
    public string CorrelationIdHeaderName { get; set; } = "x-correlation-id";
}

public sealed class PluginSdkControlOptions
{
    public string Status { get; set; } = "ok";
    public string Message { get; set; } = "EMMA plugin ready";
    public string? VersionOverride { get; set; }
    public int CpuBudgetMs { get; set; }
    public int MemoryMb { get; set; }
    public IList<string> Capabilities { get; } = ["health", "capabilities"];
    public IList<string> Domains { get; } = [];
    public IList<string> Paths { get; } = [];
}

public sealed record PluginSdkControlDefaults(
    string Message,
    int CpuBudgetMs,
    int MemoryMb,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string>? Domains = null,
    IReadOnlyList<string>? Paths = null);

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

public static class PluginSdkControlOptionsExtensions
{
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

public sealed class PluginDefaultControlService(IOptions<PluginSdkControlOptions> options)
    : PluginControl.PluginControlBase
{
    private readonly PluginSdkControlOptions _options = options.Value;

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

public sealed class PluginRpcSecurityInterceptor(IOptions<PluginSdkSecurityOptions> options) : Interceptor
{
    private readonly PluginSdkSecurityOptions _options = options.Value;

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        EnsureAuthorizedAndActive(context);
        return continuation(request, context);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        EnsureAuthorizedAndActive(context);
        await continuation(request, responseStream, context);
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        EnsureAuthorizedAndActive(context);
        return await continuation(requestStream, context);
    }

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

public static class PluginRequestContext
{
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

public sealed class PluginEndpointBuilder
{
    private readonly List<Type> _serviceTypes = [];

    public PluginEndpointBuilder AddGrpcService<TService>() where TService : class
    {
        var serviceType = typeof(TService);
        if (!_serviceTypes.Contains(serviceType))
        {
            _serviceTypes.Add(serviceType);
        }

        return this;
    }

    public PluginEndpointBuilder AddControlService<TService>() where TService : class => AddGrpcService<TService>();

    public PluginEndpointBuilder AddSearchProvider<TService>() where TService : class => AddGrpcService<TService>();

    public PluginEndpointBuilder AddPageProvider<TService>() where TService : class => AddGrpcService<TService>();

    public PluginEndpointBuilder AddVideoProvider<TService>() where TService : class => AddGrpcService<TService>();

    internal PluginEndpointRegistry BuildRegistry() => new(_serviceTypes);
}

internal sealed class PluginEndpointRegistry(IReadOnlyList<Type> serviceTypes)
{
    public IReadOnlyList<Type> ServiceTypes { get; } = serviceTypes;
}

public static class PluginSdkHost
{
    private static readonly MethodInfo MapGrpcServiceMethod =
        typeof(GrpcEndpointRouteBuilderExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method =>
                string.Equals(method.Name, nameof(GrpcEndpointRouteBuilderExtensions.MapGrpcService), StringComparison.Ordinal)
                && method.IsGenericMethodDefinition
                && method.GetParameters().Length == 1);

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
            services.AddGrpc(grpc => grpc.Interceptors.Add<PluginRpcSecurityInterceptor>());
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

    public static void MapRegisteredGrpcServices(WebApplication app)
    {
        var registry = app.Services.GetRequiredService<PluginEndpointRegistry>();
        foreach (var serviceType in registry.ServiceTypes)
        {
            var genericMethod = MapGrpcServiceMethod.MakeGenericMethod(serviceType);
            _ = genericMethod.Invoke(null, [app]);
        }
    }

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

        if (app.Services.GetService<IPluginSearchMetadataRuntime>() is null)
        {
            return;
        }

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

    public static PluginBuilder Create(string[] args, PluginAspNetHostOptions hostOptions)
        => new(args, hostOptions);

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

    public PluginBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        _serviceRegistrations.Add(configure);
        return this;
    }

    public PluginBuilder ConfigureSecurity(Action<PluginSdkSecurityOptions> configure)
    {
        _security = _security is null
            ? configure
            : _security + configure;
        return this;
    }

    public PluginBuilder ConfigureDefaultControl(Action<PluginSdkControlOptions> configure)
    {
        _defaultControl = _defaultControl is null
            ? configure
            : _defaultControl + configure;
        return this;
    }

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

    public PluginBuilder UseDefaultControlService(PluginSdkControlDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        return UseDefaultControlService(options => options.ApplyDefaults(defaults));
    }

    public PluginBuilder AddGrpcService<TService>() where TService : class
    {
        _endpointRegistrations += endpoints => endpoints.AddGrpcService<TService>();
        return this;
    }

    public PluginBuilder AddControlService<TService>() where TService : class
    {
        _endpointRegistrations += endpoints => endpoints.AddControlService<TService>();
        return this;
    }

    public PluginBuilder AddSearchProvider<TService>() where TService : class
    {
        _endpointRegistrations += endpoints => endpoints.AddSearchProvider<TService>();
        return this;
    }

    public PluginBuilder AddDefaultSearchProvider<TRuntime>()
        where TRuntime : class, IPluginPagedMediaRuntime
    {
        return AddSearchProvider<PluginDefaultSearchProviderService<TRuntime>>();
    }

    public PluginBuilder AddPageProvider<TService>() where TService : class
    {
        _endpointRegistrations += endpoints => endpoints.AddPageProvider<TService>();
        return this;
    }

    public PluginBuilder AddDefaultPageProvider<TRuntime>()
        where TRuntime : class, IPluginPagedMediaRuntime
    {
        return AddPageProvider<PluginDefaultPageProviderService<TRuntime>>();
    }

    public PluginBuilder AddDefaultPagedProviders<TRuntime>()
        where TRuntime : class, IPluginPagedMediaRuntime
    {
        return AddDefaultSearchProvider<TRuntime>()
            .AddDefaultPageProvider<TRuntime>();
    }

    public PluginBuilder AddVideoProvider<TService>() where TService : class
    {
        _endpointRegistrations += endpoints => endpoints.AddVideoProvider<TService>();
        return this;
    }

    public PluginBuilder AddDefaultVideoProvider<TRuntime>()
        where TRuntime : class, IPluginVideoRuntime
    {
        return AddVideoProvider<PluginDefaultVideoProviderService<TRuntime>>();
    }

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
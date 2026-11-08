using EMMA.Api.Composition;
using EMMA.Api.Configuration;
using EMMA.Api.Endpoints;
using EMMA.Api.Http;
using EMMA.Api.Security;

namespace EMMA.Api;

/// <summary>Builds the API host: configuration, security, then the endpoints.</summary>
internal static class ApiHost
{
    /// <summary>The largest request body accepted. Every request body is a small JSON document.</summary>
    private const long MaxRequestBodyBytes = 64 * 1024;

    /// <summary>Builds the host and prepares it to serve. The caller starts it.</summary>
    /// <param name="args">The command-line arguments, which configuration may read.</param>
    /// <param name="configureServices">Replaces registrations after the defaults; used by tests to swap a port.</param>
    /// <param name="cancellationToken">Cancels the preparation.</param>
    /// <returns>The built application.</returns>
    public static async Task<WebApplication> BuildAsync(
        string[] args,
        Action<IServiceCollection>? configureServices = null,
        CancellationToken cancellationToken = default)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
        ApiSettings settings = ApiSettingsReader.Read(builder.Configuration, allowUnsignedPlugins: builder.Environment.IsDevelopment());

        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
        });
        builder.Services.AddEmma(settings);
        builder.Services.AddRateLimiter(limiter => RateLimiting.Configure(limiter, settings.Security.RateLimit));
        configureServices?.Invoke(builder.Services);

        WebApplication app = builder.Build();
        await app.Services.GetRequiredService<HostInitializer>().InitializeAsync(cancellationToken).ConfigureAwait(false);

        // Order matters: failures become JSON, then the caller is identified, and only then are they rate-limited.
        app.UseExceptionHandler(failure => failure.Run(context =>
            ApiResults.Error(StatusCodes.Status500InternalServerError, "internal_error", "The request failed.").ExecuteAsync(context)));
        ApiAuthenticator authenticator = new(new ApiKeyValidator(settings.Security.Keys), settings.Security.KeyHeader);
        app.Use(authenticator.InvokeAsync);
        app.UseRateLimiter();

        HealthEndpoints.Map(app);
        RouteGroupBuilder v1 = app.MapGroup("/v1");
        PluginEndpoints.Map(v1);
        NodeEndpoints.Map(v1);
        AssetEndpoints.Map(v1);
        ProgressEndpoints.Map(v1);
        LibraryEndpoints.Map(v1);
        return app;
    }
}

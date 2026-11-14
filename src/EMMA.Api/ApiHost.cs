using EMMA.Api.Composition;
using EMMA.Api.Configuration;
using EMMA.Api.Endpoints;
using EMMA.Api.Http;
using EMMA.Api.Security;
using EMMA.Domain;
using EMMA.Runtime;

namespace EMMA.Api;

/// <summary>Builds the API host: configuration, security, then the endpoints.</summary>
internal static class ApiHost
{
    /// <summary>The largest request body accepted. Every request body is a small JSON document.</summary>
    private const long MaxRequestBodyBytes = 64 * 1024;

    /// <summary>Builds the host and prepares it to serve. The caller starts it.</summary>
    /// <param name="args">The command-line arguments, which configuration may read.</param>
    /// <param name="configureRuntime">Adjusts the runtime's options; used by tests to swap a port.</param>
    /// <param name="cancellationToken">Cancels the preparation.</param>
    /// <returns>The built application.</returns>
    public static async Task<WebApplication> BuildAsync(
        string[] args,
        Func<EmmaRuntimeOptions, EmmaRuntimeOptions>? configureRuntime = null,
        CancellationToken cancellationToken = default)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
        ApiSettings settings = ApiSettingsReader.Read(builder.Configuration, allowUnsignedPlugins: builder.Environment.IsDevelopment());

        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
        });
        builder.Services.AddEmma(settings, configureRuntime);
        builder.Services.AddRateLimiter(limiter => RateLimiting.Configure(limiter, settings.Security.RateLimit));

        WebApplication app = builder.Build();
        EmmaRuntime runtime = app.Services.GetRequiredService<EmmaRuntime>();
        IReadOnlyList<PluginId> quarantined = await runtime.InitializeAsync(cancellationToken).ConfigureAwait(false);
        foreach (PluginId plugin in quarantined)
        {
            app.Logger.LogError("Quarantined plugin {Plugin}: its stored manifest cannot be read.", plugin.Value);
        }

        runtime.Downloads.Start();

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
        PlaybackEndpoints.Map(v1);
        ProgressEndpoints.Map(v1);
        LibraryEndpoints.Map(v1);
        AppSettingsEndpoints.Map(v1);
        DownloadEndpoints.Map(v1);
        return app;
    }
}

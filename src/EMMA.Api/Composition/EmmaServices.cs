using EMMA.Api.Configuration;
using EMMA.PluginHost.Signing;
using EMMA.Runtime;

namespace EMMA.Api.Composition;

/// <summary>Exposes the parts of the shared <see cref="EmmaRuntime"/> to the endpoints through the service container.</summary>
internal static class EmmaServices
{
    /// <summary>Builds the runtime lazily and registers what the endpoints and tests use from it.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="settings">The validated settings.</param>
    /// <param name="configureRuntime">Adjusts the runtime's options; used by tests to swap a port.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddEmma(
        this IServiceCollection services,
        ApiSettings settings,
        Func<EmmaRuntimeOptions, EmmaRuntimeOptions>? configureRuntime = null)
    {
        services.AddSingleton(provider =>
        {
            EmmaRuntimeOptions options = new(settings.DataDirectory)
            {
                Trust = TrustPolicy.Create(settings.Trust.Repositories, settings.Trust.RequireSigned),
                PluginLog = new LoggingPluginLog(provider.GetRequiredService<ILogger<LoggingPluginLog>>()),
            };
            return new EmmaRuntime(configureRuntime?.Invoke(options) ?? options);
        });

        services.AddSingleton(provider => provider.GetRequiredService<EmmaRuntime>().Time);
        services.AddSingleton(provider => provider.GetRequiredService<EmmaRuntime>().Registry);
        services.AddSingleton(provider => provider.GetRequiredService<EmmaRuntime>().Nodes);
        services.AddSingleton(provider => provider.GetRequiredService<EmmaRuntime>().ProgressStore);
        services.AddSingleton(provider => provider.GetRequiredService<EmmaRuntime>().Library);
        services.AddSingleton(provider => provider.GetRequiredService<EmmaRuntime>().Gateway);
        services.AddSingleton(provider => provider.GetRequiredService<EmmaRuntime>().Catalog);
        services.AddSingleton(provider => provider.GetRequiredService<EmmaRuntime>().Assets);
        services.AddSingleton(provider => provider.GetRequiredService<EmmaRuntime>().Progress);
        services.AddSingleton(provider => provider.GetRequiredService<EmmaRuntime>().AppSettings);
        services.AddSingleton(provider => provider.GetRequiredService<EmmaRuntime>().Policies);
        services.AddSingleton(provider => provider.GetRequiredService<EmmaRuntime>().Verifier);
        services.AddSingleton(provider => provider.GetRequiredService<EmmaRuntime>().HostOptions);
        services.AddSingleton(provider => provider.GetRequiredService<EmmaRuntime>().Trust);
        return services;
    }
}

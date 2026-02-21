using EMMA.PluginHost.Plugins;

namespace EMMA.PluginHost.Services;

/// <summary>
/// Host inspection endpoints for development.
/// TODO: Deprecate once the public API layer is implemented.
/// </summary>
public static class PluginHostEndpoints
{
    public static WebApplication MapPluginHostEndpoints(this WebApplication app)
    {
        app.MapGet("/plugins", (PluginRegistry registry) => registry.GetSnapshot());

        app.MapPost("/plugins/refresh", async (
            PluginHandshakeService handshake,
            PluginRegistry registry,
            CancellationToken cancellationToken) =>
        {
            await handshake.RescanAsync(cancellationToken);
            return Results.Ok(registry.GetSnapshot());
        });

        app.MapGet("/plugins/status", (PluginRegistry registry) =>
        {
            var snapshot = registry.GetSnapshot();
            var results = snapshot.Select(record => new
            {
                record.Manifest.Id,
                record.Manifest.Name,
                record.Manifest.Version,
                State = record.Status.Success ? "healthy" : "unhealthy",
                RuntimeState = record.Runtime.State.ToString().ToLowerInvariant(),
                record.Runtime.LastErrorCode,
                record.Runtime.LastErrorMessage,
                record.Status.Message,
                LastHandshake = record.Status.Timestamp,
                record.Status.CpuBudgetMs,
                record.Status.MemoryMb
            });

            return Results.Ok(results);
        });

        return app;
    }
}

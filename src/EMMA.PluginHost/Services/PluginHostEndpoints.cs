using EMMA.PluginHost.Plugins;

namespace EMMA.PluginHost.Services;

/// <summary>
/// Plugin host administration endpoints.
/// </summary>
public static class PluginHostEndpoints
{
    public static WebApplication MapPluginHostEndpoints(this WebApplication app)
    {
        app.MapPost("/plugins/rescan", async (
            PluginHandshakeService handshakeService,
            CancellationToken cancellationToken) =>
        {
            await handshakeService.RescanAsync(cancellationToken);
            return Results.Ok(new { status = "rescanned" });
        });

        app.MapGet("/plugins/available", async (
            PluginManifestLoader manifestLoader,
            PluginRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var manifests = await manifestLoader.LoadManifestsAsync(cancellationToken);
            var loaded = registry.GetSnapshot()
                .Select(record => record.Manifest.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var results = manifests.Select(manifest => new
            {
                manifest.Id,
                manifest.Name,
                manifest.Version,
                Runtime = new
                {
                    manifest.Runtime?.MinHostVersion
                },
                manifest.Description,
                manifest.Author,
                manifest.MediaTypes,
                Capabilities = manifest.Capabilities is null
                    ? null
                    : new
                    {
                        manifest.Capabilities.Network,
                        manifest.Capabilities.FileSystem,
                        manifest.Capabilities.Cache,
                        manifest.Capabilities.CpuBudgetMs,
                        manifest.Capabilities.MemoryMb
                    },
                Loaded = loaded.Contains(manifest.Id)
            });

            return Results.Ok(results);
        });

        app.MapGet("/plugins/logs", (
            string? pluginId,
            int? take,
            PluginProcessManager processManager) =>
        {
            if (string.IsNullOrWhiteSpace(pluginId))
            {
                return Results.BadRequest(new { message = "pluginId is required." });
            }

            var lines = processManager.GetLogs(pluginId, take);
            return Results.Ok(new { pluginId, lines });
        });

        return app;
    }
}

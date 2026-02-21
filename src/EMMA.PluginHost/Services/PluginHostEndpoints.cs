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

        app.MapPost("/plugins/start", async (
            string? pluginId,
            bool? reset,
            PluginManifestLoader loader,
            PluginProcessManager processManager,
            PluginHandshakeService handshake,
            PluginRegistry registry,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(pluginId))
            {
                return Results.BadRequest(new { message = "pluginId is required." });
            }

            var manifests = await loader.LoadManifestsAsync(cancellationToken);
            var manifest = manifests.FirstOrDefault(item =>
                string.Equals(item.Id, pluginId, StringComparison.OrdinalIgnoreCase));

            if (manifest is null)
            {
                return Results.NotFound(new { message = "Plugin manifest not found." });
            }

            var currentRuntime = registry.GetRuntime(manifest);
            if (currentRuntime.State == PluginRuntimeState.Quarantined && reset != true)
            {
                return Results.Conflict(new { message = "Plugin is quarantined. Pass reset=true to override." });
            }

            if (reset == true)
            {
                currentRuntime = PluginRuntimeStatus.Unknown();
                registry.UpdateRuntime(manifest, currentRuntime);
            }

            var runtime = await processManager.EnsureStartedAsync(manifest, currentRuntime, cancellationToken);
            registry.UpdateRuntime(manifest, runtime);
            var status = await handshake.HandshakeSingleAsync(manifest, cancellationToken);
            registry.Upsert(manifest, status, runtime);

            return Results.Ok(new
            {
                manifest.Id,
                RuntimeState = runtime.State.ToString().ToLowerInvariant(),
                runtime.LastErrorCode,
                runtime.LastErrorMessage,
                status.Message
            });
        });

        app.MapPost("/plugins/reset", async (
            string? pluginId,
            PluginManifestLoader loader,
            PluginRegistry registry,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(pluginId))
            {
                return Results.BadRequest(new { message = "pluginId is required." });
            }

            var manifests = await loader.LoadManifestsAsync(cancellationToken);
            var manifest = manifests.FirstOrDefault(item =>
                string.Equals(item.Id, pluginId, StringComparison.OrdinalIgnoreCase));

            if (manifest is null)
            {
                return Results.NotFound(new { message = "Plugin manifest not found." });
            }

            registry.UpdateRuntime(manifest, PluginRuntimeStatus.Unknown());
            return Results.Ok(new { manifest.Id, State = "reset" });
        });

        app.MapPost("/plugins/stop", async (
            string? pluginId,
            PluginManifestLoader loader,
            PluginProcessManager processManager,
            PluginRegistry registry,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(pluginId))
            {
                return Results.BadRequest(new { message = "pluginId is required." });
            }

            var manifests = await loader.LoadManifestsAsync(cancellationToken);
            var manifest = manifests.FirstOrDefault(item =>
                string.Equals(item.Id, pluginId, StringComparison.OrdinalIgnoreCase));

            if (manifest is null)
            {
                return Results.NotFound(new { message = "Plugin manifest not found." });
            }

            await processManager.StopAsync(pluginId, cancellationToken);
            registry.UpdateRuntime(manifest, PluginRuntimeStatus.Stopped());

            return Results.Ok(new { manifest.Id, State = "stopped" });
        });

        app.MapPost("/plugins/refresh", async (
            PluginHandshakeService handshake,
            PluginRegistry registry,
            CancellationToken cancellationToken) =>
        {
            await handshake.RescanAsync(cancellationToken);
            return Results.Ok(registry.GetSnapshot());
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
            if (lines.Count == 0)
            {
                return Results.NotFound(new { message = "No logs recorded for plugin." });
            }

            return Results.Ok(new { PluginId = pluginId, Lines = lines });
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
                record.Runtime.RetryCount,
                record.Runtime.TimeoutCount,
                record.Runtime.NextRetryAt,
                record.Status.Message,
                LastHandshake = record.Status.Timestamp,
                record.Status.CpuBudgetMs,
                record.Status.MemoryMb
            });

            return Results.Ok(results);
        });

        app.MapGet("/plugins/summary", (PluginRegistry registry) =>
        {
            var snapshot = registry.GetSnapshot();
            var results = snapshot.Select(record => new
            {
                record.Manifest.Id,
                record.Manifest.Name,
                record.Manifest.Version,
                Health = record.Status.Success ? "healthy" : "unhealthy",
                Runtime = record.Runtime.State.ToString().ToLowerInvariant(),
                record.Status.Message,
                record.Runtime.LastErrorCode,
                record.Runtime.LastErrorMessage,
                record.Runtime.TimeoutCount,
                record.Runtime.RetryCount,
                record.Runtime.NextRetryAt,
                LastHandshake = record.Status.Timestamp
            });

            return Results.Ok(results);
        });

        return app;
    }
}

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

        app.MapGet("/plugins/repositories", async (
            PluginRepositoryService repositoryService,
            CancellationToken cancellationToken) =>
        {
            var repositories = await repositoryService.ListRepositoriesAsync(cancellationToken);
            return Results.Ok(repositories);
        });

        app.MapPost("/plugins/repositories", async (
            AddPluginRepositoryRequest request,
            PluginRepositoryService repositoryService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var repository = await repositoryService.AddRepositoryAsync(request, cancellationToken);
                return Results.Ok(repository);
            }
            catch (Exception ex)
            {
                return MapRepositoryException(ex);
            }
        });

        app.MapDelete("/plugins/repositories/{repositoryId}", async (
            string repositoryId,
            PluginRepositoryService repositoryService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var removed = await repositoryService.RemoveRepositoryAsync(repositoryId, cancellationToken);
                if (!removed)
                {
                    return Results.NotFound(new { message = $"Repository '{repositoryId}' was not found." });
                }

                return Results.Ok(new { status = "removed", repositoryId });
            }
            catch (Exception ex)
            {
                return MapRepositoryException(ex);
            }
        });

        app.MapPost("/plugins/repositories/{repositoryId}/refresh", async (
            string repositoryId,
            PluginRepositoryService repositoryService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var snapshot = await repositoryService.GetRepositoryCatalogSnapshotAsync(
                    repositoryId,
                    refresh: true,
                    cancellationToken);

                return Results.Ok(new
                {
                    snapshot.Repository,
                    snapshot.Refreshed,
                    snapshot.RetrievedAtUtc,
                    pluginCount = snapshot.Catalog.Plugins.Count
                });
            }
            catch (Exception ex)
            {
                return MapRepositoryException(ex);
            }
        });

        app.MapGet("/plugins/repositories/{repositoryId}/plugins", async (
            string repositoryId,
            bool? refresh,
            PluginRepositoryService repositoryService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await repositoryService.GetRepositoryPluginsAsync(
                    repositoryId,
                    refresh ?? false,
                    cancellationToken);

                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return MapRepositoryException(ex);
            }
        });

        app.MapGet("/plugins/repository-plugins", async (
            bool? refresh,
            PluginRepositoryService repositoryService,
            CancellationToken cancellationToken) =>
        {
            var plugins = await repositoryService.GetAllRepositoryPluginsAsync(refresh ?? false, cancellationToken);
            return Results.Ok(plugins);
        });

        app.MapPost("/plugins/install-from-repository", async (
            InstallPluginFromRepositoryRequest request,
            PluginRepositoryInstallOrchestrator installOrchestrator,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await installOrchestrator.InstallFromRepositoryAsync(request, cancellationToken);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return MapRepositoryException(ex);
            }
        });

        return app;
    }

    private static IResult MapRepositoryException(Exception ex)
    {
        return ex switch
        {
            ArgumentException => Results.BadRequest(new { message = ex.Message }),
            InvalidDataException => Results.BadRequest(new { message = ex.Message }),
            KeyNotFoundException => Results.NotFound(new { message = ex.Message }),
            _ => Results.Problem(detail: ex.Message)
        };
    }
}

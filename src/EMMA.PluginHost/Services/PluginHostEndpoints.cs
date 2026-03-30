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
            return await ExecuteRepositoryActionAsync(async ct =>
            {
                var repository = await repositoryService.AddRepositoryAsync(request, ct);
                return Results.Ok(repository);
            }, cancellationToken);
        });

        app.MapDelete("/plugins/repositories/{repositoryId}", async (
            string repositoryId,
            PluginRepositoryService repositoryService,
            CancellationToken cancellationToken) =>
        {
            return await ExecuteRepositoryActionAsync(async ct =>
            {
                var removed = await repositoryService.RemoveRepositoryAsync(repositoryId, ct);
                if (!removed)
                {
                    return Results.NotFound(new { message = $"Repository '{repositoryId}' was not found." });
                }

                return Results.Ok(new { status = "removed", repositoryId });
            }, cancellationToken);
        });

        app.MapPost("/plugins/repositories/{repositoryId}/refresh", async (
            string repositoryId,
            PluginRepositoryService repositoryService,
            CancellationToken cancellationToken) =>
        {
            return await ExecuteRepositoryActionAsync(async ct =>
            {
                var snapshot = await repositoryService.GetRepositoryCatalogSnapshotAsync(
                    repositoryId,
                    refresh: true,
                    ct);

                return Results.Ok(new
                {
                    snapshot.Repository,
                    snapshot.Refreshed,
                    snapshot.RetrievedAtUtc,
                    pluginCount = snapshot.Catalog.Plugins.Count
                });
            }, cancellationToken);
        });

        app.MapGet("/plugins/repositories/{repositoryId}/plugins", async (
            string repositoryId,
            bool? refresh,
            PluginRepositoryService repositoryService,
            CancellationToken cancellationToken) =>
        {
            return await ExecuteRepositoryActionAsync(async ct =>
            {
                var result = await repositoryService.GetRepositoryPluginsAsync(
                    repositoryId,
                    refresh ?? false,
                    ct);

                return Results.Ok(result);
            }, cancellationToken);
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
            return await ExecuteRepositoryActionAsync(async ct =>
            {
                var result = await installOrchestrator.InstallFromRepositoryAsync(request, ct);
                return Results.Ok(result);
            }, cancellationToken);
        });

        return app;
    }

    private static async Task<IResult> ExecuteRepositoryActionAsync(
        Func<CancellationToken, Task<IResult>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action(cancellationToken);
        }
        catch (Exception ex)
        {
            return MapRepositoryException(ex);
        }
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

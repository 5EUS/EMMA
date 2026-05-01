using System.Text.RegularExpressions;

namespace EMMA.PluginHost.Services;

public sealed class PluginRepositoryService(
    PluginRepositoryStore store,
    PluginRepositoryCatalogClient catalogClient,
    ILogger<PluginRepositoryService> logger)
{
    private static readonly Regex RepositoryIdSanitizer = new("[^a-z0-9._-]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly PluginRepositoryStore _store = store;
    private readonly PluginRepositoryCatalogClient _catalogClient = catalogClient;
    private readonly ILogger<PluginRepositoryService> _logger = logger;

    public Task<IReadOnlyList<PluginRepositoryRecord>> ListRepositoriesAsync(CancellationToken cancellationToken)
    {
        return _store.ListAsync(cancellationToken);
    }

    public async Task<PluginRepositoryRecord> AddRepositoryAsync(
        AddPluginRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CatalogUrl))
        {
            throw new ArgumentException("Catalog URL is required.", nameof(request));
        }

        var normalizedId = NormalizeRepositoryId(request.RepositoryId, request.Name, request.CatalogUrl);
        var existing = await _store.GetAsync(normalizedId, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        var repository = new PluginRepositoryRecord(
            Id: normalizedId,
            Name: ResolveRepositoryName(request.Name, normalizedId),
            CatalogUrl: request.CatalogUrl.Trim(),
            SourceRepositoryUrl: NormalizeOptionalUrl(request.SourceRepositoryUrl),
            CreatedAtUtc: existing?.CreatedAtUtc ?? now,
            UpdatedAtUtc: now,
            LastRefreshedAtUtc: existing?.LastRefreshedAtUtc,
            LastRefreshError: null,
            ETag: existing?.ETag,
            Enabled: true);

        var fetch = await _catalogClient.FetchCatalogAsync(repository, cancellationToken);
        var catalog = await ResolveCatalogFromFetchAsync(repository, fetch, cancellationToken);

        await _store.SaveCatalogAsync(repository.Id, catalog, fetch.RawJson!, cancellationToken);

        var updated = repository with
        {
            Name = ResolveRepositoryName(request.Name, catalog.Name ?? repository.Name),
            LastRefreshedAtUtc = fetch.RetrievedAtUtc,
            LastRefreshError = null,
            ETag = fetch.ETag,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await _store.UpsertAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<bool> RemoveRepositoryAsync(string repositoryId, CancellationToken cancellationToken)
    {
        return await _store.RemoveAsync(NormalizeRepositoryId(repositoryId, null, null), cancellationToken);
    }

    public async Task<PluginRepositoryCatalogSnapshot> GetRepositoryCatalogSnapshotAsync(
        string repositoryId,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var normalizedId = NormalizeRepositoryId(repositoryId, null, null);
        var repository = await _store.GetAsync(normalizedId, cancellationToken)
            ?? throw new KeyNotFoundException($"Repository '{normalizedId}' was not found.");

        if (refresh)
        {
            return await RefreshCatalogAsync(repository, cancellationToken);
        }

        var cached = await _store.LoadCatalogAsync(repository.Id, cancellationToken);
        if (cached is not null)
        {
            ValidateCatalog(repository, cached);
            return new PluginRepositoryCatalogSnapshot(repository, cached, Refreshed: false, RetrievedAtUtc: DateTimeOffset.UtcNow);
        }

        return await RefreshCatalogAsync(repository, cancellationToken);
    }

    public async Task<RepositoryPluginsResponse> GetRepositoryPluginsAsync(
        string repositoryId,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetRepositoryCatalogSnapshotAsync(repositoryId, refresh, cancellationToken);
        var plugins = snapshot.Catalog.Plugins
            .Select(plugin => ToView(snapshot.Repository.Id, plugin))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.PluginId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RepositoryPluginsResponse(snapshot.Repository, plugins, snapshot.Refreshed, snapshot.RetrievedAtUtc);
    }

    public async Task<IReadOnlyList<PluginRepositoryPluginView>> GetAllRepositoryPluginsAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        var repositories = await _store.ListAsync(cancellationToken);
        var all = new List<PluginRepositoryPluginView>();

        foreach (var repository in repositories)
        {
            try
            {
                var snapshot = await GetRepositoryCatalogSnapshotAsync(repository.Id, refresh, cancellationToken);
                all.AddRange(snapshot.Catalog.Plugins.Select(plugin => ToView(snapshot.Repository.Id, plugin)));
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(ex, "Skipping repository {RepositoryId} while listing repository plugins.", repository.Id);
                }
            }
        }

        return all
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.PluginId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<PluginRepositorySelectionResult> ResolvePluginReleaseAsync(
        string repositoryId,
        string pluginId,
        string? version,
        bool refreshCatalog,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetRepositoryCatalogSnapshotAsync(repositoryId, refreshCatalog, cancellationToken);
        var plugin = snapshot.Catalog.Plugins
            .FirstOrDefault(item => string.Equals(item.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));

        if (plugin is null)
        {
            throw new KeyNotFoundException($"Plugin '{pluginId}' was not found in repository '{snapshot.Repository.Id}'.");
        }

        var release = SelectRelease(plugin, version);
        if (release is null)
        {
            var requested = string.IsNullOrWhiteSpace(version) ? "latest" : version;
            throw new KeyNotFoundException(
                $"Release '{requested}' was not found for plugin '{plugin.PluginId}' in repository '{snapshot.Repository.Id}'.");
        }

        return new PluginRepositorySelectionResult(snapshot.Repository, plugin, release);
    }

    private async Task<PluginRepositoryCatalogSnapshot> RefreshCatalogAsync(
        PluginRepositoryRecord repository,
        CancellationToken cancellationToken)
    {
        var fetch = await _catalogClient.FetchCatalogAsync(repository, cancellationToken);
        if (fetch.NotModified)
        {
            var cached = await _store.LoadCatalogAsync(repository.Id, cancellationToken)
                ?? throw new InvalidDataException($"Repository '{repository.Id}' reported not-modified but no local catalog cache exists.");

            ValidateCatalog(repository, cached);

            var unchanged = repository with
            {
                LastRefreshedAtUtc = fetch.RetrievedAtUtc,
                LastRefreshError = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _store.UpsertAsync(unchanged, cancellationToken);
            return new PluginRepositoryCatalogSnapshot(unchanged, cached, Refreshed: true, fetch.RetrievedAtUtc);
        }

        var catalog = await ResolveCatalogFromFetchAsync(repository, fetch, cancellationToken);
        await _store.SaveCatalogAsync(repository.Id, catalog, fetch.RawJson!, cancellationToken);

        var updated = repository with
        {
            Name = ResolveRepositoryName(repository.Name, catalog.Name ?? repository.Name),
            LastRefreshedAtUtc = fetch.RetrievedAtUtc,
            LastRefreshError = null,
            ETag = fetch.ETag,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await _store.UpsertAsync(updated, cancellationToken);
        return new PluginRepositoryCatalogSnapshot(updated, catalog, Refreshed: true, fetch.RetrievedAtUtc);
    }

    private async Task<PluginRepositoryCatalog> ResolveCatalogFromFetchAsync(
        PluginRepositoryRecord repository,
        PluginRepositoryCatalogFetchResult fetch,
        CancellationToken cancellationToken)
    {
        if (fetch.Catalog is null || string.IsNullOrWhiteSpace(fetch.RawJson))
        {
            throw new InvalidDataException($"Repository '{repository.Id}' returned an empty catalog payload.");
        }

        var catalog = fetch.Catalog;
        ValidateCatalog(repository, catalog);

        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        return catalog;
    }

    private static PluginRepositoryCatalogRelease? SelectRelease(PluginRepositoryCatalogPlugin plugin, string? version)
    {
        if (plugin.Releases.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(version))
        {
            return plugin.Releases.FirstOrDefault(item =>
                string.Equals(item.Version, version.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return plugin.Releases
            .OrderByDescending(ReleaseOrderingKey)
            .FirstOrDefault();
    }

    private static (int Stability, int Major, int Minor, int Patch, string Version) ReleaseOrderingKey(
        PluginRepositoryCatalogRelease release)
    {
        var normalized = release.Version.Trim();
        var prereleaseIndex = normalized.IndexOf('-');
        if (prereleaseIndex >= 0)
        {
            normalized = normalized[..prereleaseIndex];
        }

        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var major = ParsePart(parts, 0);
        var minor = ParsePart(parts, 1);
        var patch = ParsePart(parts, 2);
        var stability = release.IsPrerelease ? 0 : 1;

        return (stability, major, minor, patch, release.Version);
    }

    private static int ParsePart(string[] parts, int index)
    {
        if (index >= parts.Length)
        {
            return 0;
        }

        return int.TryParse(parts[index], out var parsed) ? parsed : 0;
    }

    private static PluginRepositoryPluginView ToView(string repositoryId, PluginRepositoryCatalogPlugin plugin)
    {
        var releases = plugin.Releases
            .Select(release => new PluginRepositoryReleaseView(
                Version: release.Version,
                AssetUrl: release.AssetUrl,
                Sha256: release.Sha256,
                Platforms: NormalizePlatforms(release.Platforms),
                IsPrerelease: release.IsPrerelease,
                PublishedAtUtc: release.PublishedAtUtc,
                Notes: release.Notes))
            .OrderByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PluginRepositoryPluginView(
            RepositoryId: repositoryId,
            PluginId: plugin.PluginId,
            Name: plugin.Name,
            Description: plugin.Description,
            Author: plugin.Author,
            SourceRepositoryUrl: plugin.SourceRepositoryUrl,
            Releases: releases);
    }

    private static IReadOnlyList<string> NormalizePlatforms(IReadOnlyList<string>? value)
    {
        if (value is null || value.Count == 0)
        {
            return [];
        }

        return value
            .Select(item => item?.Trim().ToLowerInvariant() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ValidateCatalog(PluginRepositoryRecord repository, PluginRepositoryCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(catalog.RepositoryId))
        {
            throw new InvalidDataException($"Repository '{repository.Id}' catalog is missing repositoryId.");
        }

        if (!string.Equals(catalog.RepositoryId, repository.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Repository id mismatch. Expected '{repository.Id}', catalog reports '{catalog.RepositoryId}'.");
        }

        var seenPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in catalog.Plugins)
        {
            if (string.IsNullOrWhiteSpace(plugin.PluginId))
            {
                throw new InvalidDataException($"Repository '{repository.Id}' contains a plugin with empty pluginId.");
            }

            if (!seenPluginIds.Add(plugin.PluginId))
            {
                throw new InvalidDataException($"Repository '{repository.Id}' contains duplicate pluginId '{plugin.PluginId}'.");
            }

            if (string.IsNullOrWhiteSpace(plugin.Name))
            {
                throw new InvalidDataException($"Plugin '{plugin.PluginId}' in repository '{repository.Id}' is missing a name.");
            }

            if (plugin.Releases is null || plugin.Releases.Count == 0)
            {
                throw new InvalidDataException($"Plugin '{plugin.PluginId}' in repository '{repository.Id}' has no releases.");
            }

            var seenVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var release in plugin.Releases)
            {
                if (string.IsNullOrWhiteSpace(release.Version))
                {
                    throw new InvalidDataException($"Plugin '{plugin.PluginId}' contains a release with empty version.");
                }

                if (!seenVersions.Add(release.Version))
                {
                    throw new InvalidDataException($"Plugin '{plugin.PluginId}' contains duplicate release version '{release.Version}'.");
                }

                if (string.IsNullOrWhiteSpace(release.AssetUrl))
                {
                    throw new InvalidDataException(
                        $"Plugin '{plugin.PluginId}' release '{release.Version}' is missing assetUrl.");
                }

                if (string.IsNullOrWhiteSpace(release.Sha256))
                {
                    throw new InvalidDataException(
                        $"Plugin '{plugin.PluginId}' release '{release.Version}' is missing sha256.");
                }
            }
        }
    }

    private static string ResolveRepositoryName(string? proposed, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(proposed))
        {
            return proposed.Trim();
        }

        return fallback;
    }

    private static string? NormalizeOptionalUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static string NormalizeRepositoryId(string? repositoryId, string? name, string? catalogUrl)
    {
        var candidate = repositoryId;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = name;
        }

        if (string.IsNullOrWhiteSpace(candidate) && !string.IsNullOrWhiteSpace(catalogUrl))
        {
            if (Uri.TryCreate(catalogUrl, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                candidate = uri.Host;
            }
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new ArgumentException("Repository id could not be resolved.", nameof(repositoryId));
        }

        var trimmed = candidate.Trim().ToLowerInvariant();
        trimmed = RepositoryIdSanitizer.Replace(trimmed, "-");
        trimmed = trimmed.Trim('-');

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Repository id is invalid after normalization.", nameof(repositoryId));
        }

        return trimmed;
    }
}

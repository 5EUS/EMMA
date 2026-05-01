namespace EMMA.PluginHost.Services;

public sealed record PluginRepositoryRecord(
    string Id,
    string Name,
    string CatalogUrl,
    string? SourceRepositoryUrl,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastRefreshedAtUtc,
    string? LastRefreshError,
    string? ETag,
    bool Enabled = true);

public sealed record PluginRepositoryStateFile(
    IReadOnlyList<PluginRepositoryRecord> Repositories);

public sealed record PluginRepositoryCatalog(
    string RepositoryId,
    string? Name,
    IReadOnlyList<PluginRepositoryCatalogPlugin> Plugins,
    string? GeneratedAtUtc = null,
    string? ApiVersion = null);

public sealed record PluginRepositoryCatalogPlugin(
    string PluginId,
    string Name,
    string? Description,
    string? Author,
    string? SourceRepositoryUrl,
    IReadOnlyList<PluginRepositoryCatalogRelease> Releases);

public sealed record PluginRepositoryCatalogRelease(
    string Version,
    string AssetUrl,
    string Sha256,
    IReadOnlyList<string>? Platforms,
    string? PublishedAtUtc = null,
    bool IsPrerelease = false,
    string? Notes = null);

public sealed record AddPluginRepositoryRequest(
    string CatalogUrl,
    string? RepositoryId = null,
    string? Name = null,
    string? SourceRepositoryUrl = null);

public sealed record InstallPluginFromRepositoryRequest(
    string RepositoryId,
    string PluginId,
    string? Version = null,
    bool RefreshCatalog = true,
    bool RescanAfterInstall = true);

public sealed record RepositoryPluginsResponse(
    PluginRepositoryRecord Repository,
    IReadOnlyList<PluginRepositoryPluginView> Plugins,
    bool Refreshed,
    DateTimeOffset RetrievedAtUtc);

public sealed record PluginRepositoryPluginView(
    string RepositoryId,
    string PluginId,
    string Name,
    string? Description,
    string? Author,
    string? SourceRepositoryUrl,
    IReadOnlyList<PluginRepositoryReleaseView> Releases);

public sealed record PluginRepositoryReleaseView(
    string Version,
    string AssetUrl,
    string Sha256,
    IReadOnlyList<string> Platforms,
    bool IsPrerelease,
    string? PublishedAtUtc,
    string? Notes);

public sealed record PluginRepositoryInstallResult(
    bool Success,
    string RepositoryId,
    string PluginId,
    string Version,
    string InstalledManifestPath,
    string InstalledPluginPath,
    string PayloadType,
    DateTimeOffset InstalledAtUtc,
    string? Message = null);

public sealed record PluginRepositoryCatalogFetchResult(
    bool NotModified,
    PluginRepositoryCatalog? Catalog,
    string? ETag,
    string? RawJson,
    DateTimeOffset RetrievedAtUtc);

public sealed record PluginRepositoryCatalogSnapshot(
    PluginRepositoryRecord Repository,
    PluginRepositoryCatalog Catalog,
    bool Refreshed,
    DateTimeOffset RetrievedAtUtc);

public sealed record PluginRepositorySelectionResult(
    PluginRepositoryRecord Repository,
    PluginRepositoryCatalogPlugin Plugin,
    PluginRepositoryCatalogRelease Release);

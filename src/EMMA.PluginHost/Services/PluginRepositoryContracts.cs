namespace EMMA.PluginHost.Services;

/// <summary>
/// Represents a configured plugin repository tracked by the host.
/// </summary>
/// <param name="Id">The repository identifier.</param>
/// <param name="Name">The display name for the repository.</param>
/// <param name="CatalogUrl">The repository catalog URL.</param>
/// <param name="SourceRepositoryUrl">The optional source repository URL.</param>
/// <param name="CreatedAtUtc">The UTC timestamp when the repository record was created.</param>
/// <param name="UpdatedAtUtc">The UTC timestamp when the repository record was last updated.</param>
/// <param name="LastRefreshedAtUtc">The UTC timestamp of the last successful refresh.</param>
/// <param name="LastRefreshError">The last refresh error message, if any.</param>
/// <param name="ETag">The cached ETag for conditional catalog requests.</param>
/// <param name="Enabled">Whether the repository is enabled.</param>
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

/// <summary>
/// Represents the persisted state file containing configured repositories.
/// </summary>
/// <param name="Repositories">The configured repositories.</param>
public sealed record PluginRepositoryStateFile(
    IReadOnlyList<PluginRepositoryRecord> Repositories);

/// <summary>
/// Represents a repository catalog document.
/// </summary>
/// <param name="RepositoryId">The repository identifier.</param>
/// <param name="Name">The repository display name.</param>
/// <param name="Plugins">The cataloged plugins.</param>
/// <param name="GeneratedAtUtc">The UTC timestamp when the catalog was generated.</param>
/// <param name="ApiVersion">The catalog API version.</param>
public sealed record PluginRepositoryCatalog(
    string RepositoryId,
    string? Name,
    IReadOnlyList<PluginRepositoryCatalogPlugin> Plugins,
    string? GeneratedAtUtc = null,
    string? ApiVersion = null);

/// <summary>
/// Represents a plugin entry inside a repository catalog.
/// </summary>
/// <param name="PluginId">The plugin identifier.</param>
/// <param name="Name">The plugin display name.</param>
/// <param name="Description">The plugin description.</param>
/// <param name="Author">The plugin author.</param>
/// <param name="SourceRepositoryUrl">The optional source repository URL.</param>
/// <param name="Releases">The published releases for the plugin.</param>
public sealed record PluginRepositoryCatalogPlugin(
    string PluginId,
    string Name,
    string? Description,
    string? Author,
    string? SourceRepositoryUrl,
    IReadOnlyList<PluginRepositoryCatalogRelease> Releases);

/// <summary>
/// Represents a published plugin release from a repository catalog.
/// </summary>
/// <param name="Version">The release version.</param>
/// <param name="AssetUrl">The downloadable asset URL.</param>
/// <param name="Sha256">The SHA-256 digest for the release asset.</param>
/// <param name="Platforms">The supported platform tags.</param>
/// <param name="PublishedAtUtc">The UTC publication timestamp.</param>
/// <param name="IsPrerelease">Whether the release is marked as a prerelease.</param>
/// <param name="Notes">Optional release notes.</param>
public sealed record PluginRepositoryCatalogRelease(
    string Version,
    string AssetUrl,
    string Sha256,
    IReadOnlyList<string>? Platforms,
    string? PublishedAtUtc = null,
    bool IsPrerelease = false,
    string? Notes = null);

/// <summary>
/// Represents the input required to add a plugin repository.
/// </summary>
/// <param name="CatalogUrl">The repository catalog URL.</param>
/// <param name="RepositoryId">The optional repository identifier override.</param>
/// <param name="Name">The optional repository display name.</param>
/// <param name="SourceRepositoryUrl">The optional source repository URL.</param>
public sealed record AddPluginRepositoryRequest(
    string CatalogUrl,
    string? RepositoryId = null,
    string? Name = null,
    string? SourceRepositoryUrl = null);

/// <summary>
/// Represents a request to install a plugin from a configured repository.
/// </summary>
/// <param name="RepositoryId">The repository identifier.</param>
/// <param name="PluginId">The plugin identifier.</param>
/// <param name="Version">The optional version to install, or <see langword="null"/> for the latest release.</param>
/// <param name="RefreshCatalog">Whether the repository catalog should be refreshed first.</param>
/// <param name="RescanAfterInstall">Whether installed plugins should be rescanned after installation.</param>
public sealed record InstallPluginFromRepositoryRequest(
    string RepositoryId,
    string PluginId,
    string? Version = null,
    bool RefreshCatalog = true,
    bool RescanAfterInstall = true);

/// <summary>
/// Represents the plugin list response for a single repository.
/// </summary>
/// <param name="Repository">The repository metadata.</param>
/// <param name="Plugins">The plugin view models.</param>
/// <param name="Refreshed">Whether the catalog was refreshed during the request.</param>
/// <param name="RetrievedAtUtc">The UTC timestamp when the data was retrieved.</param>
public sealed record RepositoryPluginsResponse(
    PluginRepositoryRecord Repository,
    IReadOnlyList<PluginRepositoryPluginView> Plugins,
    bool Refreshed,
    DateTimeOffset RetrievedAtUtc);

/// <summary>
/// Represents a flattened plugin view returned by repository APIs.
/// </summary>
/// <param name="RepositoryId">The repository identifier.</param>
/// <param name="PluginId">The plugin identifier.</param>
/// <param name="Name">The plugin display name.</param>
/// <param name="Description">The plugin description.</param>
/// <param name="Author">The plugin author.</param>
/// <param name="SourceRepositoryUrl">The optional source repository URL.</param>
/// <param name="Releases">The release view models.</param>
public sealed record PluginRepositoryPluginView(
    string RepositoryId,
    string PluginId,
    string Name,
    string? Description,
    string? Author,
    string? SourceRepositoryUrl,
    IReadOnlyList<PluginRepositoryReleaseView> Releases);

/// <summary>
/// Represents a flattened release view returned by repository APIs.
/// </summary>
/// <param name="Version">The release version.</param>
/// <param name="AssetUrl">The downloadable asset URL.</param>
/// <param name="Sha256">The SHA-256 digest for the release asset.</param>
/// <param name="Platforms">The supported platforms.</param>
/// <param name="IsPrerelease">Whether the release is a prerelease.</param>
/// <param name="PublishedAtUtc">The UTC publication timestamp.</param>
/// <param name="Notes">Optional release notes.</param>
public sealed record PluginRepositoryReleaseView(
    string Version,
    string AssetUrl,
    string Sha256,
    IReadOnlyList<string> Platforms,
    bool IsPrerelease,
    string? PublishedAtUtc,
    string? Notes);

/// <summary>
/// Represents the result of installing a plugin from a repository.
/// </summary>
/// <param name="Success">Whether the installation succeeded.</param>
/// <param name="RepositoryId">The repository identifier.</param>
/// <param name="PluginId">The installed plugin identifier.</param>
/// <param name="Version">The installed plugin version.</param>
/// <param name="InstalledManifestPath">The destination manifest path.</param>
/// <param name="InstalledPluginPath">The destination plugin payload path.</param>
/// <param name="PayloadType">The resolved payload type.</param>
/// <param name="InstalledAtUtc">The UTC timestamp when the install completed.</param>
/// <param name="Message">An optional status message.</param>
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

/// <summary>
/// Represents the outcome of fetching a repository catalog.
/// </summary>
/// <param name="NotModified">Whether the remote catalog returned HTTP 304.</param>
/// <param name="Catalog">The parsed catalog, when one was returned.</param>
/// <param name="ETag">The response ETag.</param>
/// <param name="RawJson">The raw catalog JSON payload.</param>
/// <param name="RetrievedAtUtc">The UTC timestamp when the fetch completed.</param>
public sealed record PluginRepositoryCatalogFetchResult(
    bool NotModified,
    PluginRepositoryCatalog? Catalog,
    string? ETag,
    string? RawJson,
    DateTimeOffset RetrievedAtUtc);

/// <summary>
/// Represents a repository and its resolved catalog snapshot.
/// </summary>
/// <param name="Repository">The repository metadata.</param>
/// <param name="Catalog">The resolved catalog.</param>
/// <param name="Refreshed">Whether the catalog was refreshed during the request.</param>
/// <param name="RetrievedAtUtc">The UTC timestamp when the snapshot was retrieved.</param>
public sealed record PluginRepositoryCatalogSnapshot(
    PluginRepositoryRecord Repository,
    PluginRepositoryCatalog Catalog,
    bool Refreshed,
    DateTimeOffset RetrievedAtUtc);

/// <summary>
/// Represents the selected repository plugin release for installation.
/// </summary>
/// <param name="Repository">The repository metadata.</param>
/// <param name="Plugin">The selected plugin metadata.</param>
/// <param name="Release">The selected release metadata.</param>
public sealed record PluginRepositorySelectionResult(
    PluginRepositoryRecord Repository,
    PluginRepositoryCatalogPlugin Plugin,
    PluginRepositoryCatalogRelease Release);

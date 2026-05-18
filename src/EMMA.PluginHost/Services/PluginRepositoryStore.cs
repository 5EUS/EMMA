using System.Text;
using System.Text.Json;
using EMMA.PluginHost.Configuration;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Services;

/// <summary>
/// Persists repository configuration and cached catalogs on disk.
/// </summary>
/// <param name="options">The plugin host options.</param>
public sealed class PluginRepositoryStore(IOptions<PluginHostOptions> options)
{
    private const string RepositoriesFileName = "repositories.json";
    private readonly PluginHostOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Gets the resolved repository storage directory path.
    /// </summary>
    public string RepositoryDirectoryPath => ResolveRepositoryDirectoryPath(_options);

    /// <summary>
    /// Lists the configured repositories from persistent storage.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The configured repositories.</returns>
    public async Task<IReadOnlyList<PluginRepositoryRecord>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateCoreAsync(cancellationToken);
            return state.Repositories
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gets a configured repository by identifier.
    /// </summary>
    /// <param name="repositoryId">The repository identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The repository record, or <see langword="null"/> when it does not exist.</returns>
    public async Task<PluginRepositoryRecord?> GetAsync(string repositoryId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateCoreAsync(cancellationToken);
            return state.Repositories
                .FirstOrDefault(item => string.Equals(item.Id, repositoryId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Inserts or updates a repository record in persistent storage.
    /// </summary>
    /// <param name="repository">The repository record.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task UpsertAsync(PluginRepositoryRecord repository, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository.Id);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateCoreAsync(cancellationToken);
            var updated = state.Repositories
                .Where(item => !string.Equals(item.Id, repository.Id, StringComparison.OrdinalIgnoreCase))
                .Concat([repository])
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            await SaveStateCoreAsync(new PluginRepositoryStateFile(updated), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes a repository record and its cached catalog.
    /// </summary>
    /// <param name="repositoryId">The repository identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when the repository was removed; otherwise, <see langword="false"/>.</returns>
    public async Task<bool> RemoveAsync(string repositoryId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryId))
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateCoreAsync(cancellationToken);
            var updated = state.Repositories
                .Where(item => !string.Equals(item.Id, repositoryId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (updated.Count == state.Repositories.Count)
            {
                return false;
            }

            await SaveStateCoreAsync(new PluginRepositoryStateFile(updated), cancellationToken);
            DeleteCatalogCore(repositoryId);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Saves a repository catalog JSON payload to persistent storage.
    /// </summary>
    /// <param name="repositoryId">The repository identifier.</param>
    /// <param name="catalog">The parsed catalog.</param>
    /// <param name="rawJson">The raw catalog JSON.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task SaveCatalogAsync(
        string repositoryId,
        PluginRepositoryCatalog catalog,
        string rawJson,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawJson);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureStorageCoreAsync(cancellationToken);
            var path = GetCatalogPath(repositoryId);
            await File.WriteAllTextAsync(path, rawJson, Encoding.UTF8, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads a cached repository catalog from persistent storage.
    /// </summary>
    /// <param name="repositoryId">The repository identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cached catalog, or <see langword="null"/> when none exists.</returns>
    public async Task<PluginRepositoryCatalog?> LoadCatalogAsync(string repositoryId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = GetCatalogPath(repositoryId);
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(
                stream,
                PluginRepositoryJsonContext.Default.PluginRepositoryCatalog,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PluginRepositoryStateFile> LoadStateCoreAsync(CancellationToken cancellationToken)
    {
        await EnsureStorageCoreAsync(cancellationToken);

        var path = GetRepositoriesFilePath();
        if (!File.Exists(path))
        {
            return new PluginRepositoryStateFile([]);
        }

        await using var stream = File.OpenRead(path);
        var state = await JsonSerializer.DeserializeAsync(
            stream,
            PluginRepositoryJsonContext.Default.PluginRepositoryStateFile,
            cancellationToken);

        return state ?? new PluginRepositoryStateFile([]);
    }

    private async Task SaveStateCoreAsync(PluginRepositoryStateFile state, CancellationToken cancellationToken)
    {
        await EnsureStorageCoreAsync(cancellationToken);
        var path = GetRepositoriesFilePath();

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(
            stream,
            state,
            PluginRepositoryJsonContext.Default.PluginRepositoryStateFile,
            cancellationToken);
    }

    private async Task EnsureStorageCoreAsync(CancellationToken cancellationToken)
    {
        var directory = RepositoryDirectoryPath;
        if (Directory.Exists(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void DeleteCatalogCore(string repositoryId)
    {
        var path = GetCatalogPath(repositoryId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetRepositoriesFilePath()
    {
        return Path.Combine(RepositoryDirectoryPath, RepositoriesFileName);
    }

    private string GetCatalogPath(string repositoryId)
    {
        var safeName = ToSafeFileName(repositoryId);
        return Path.Combine(RepositoryDirectoryPath, $"{safeName}.catalog.json");
    }

    private static string ToSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (invalid.Contains(ch) || ch == '/' || ch == '\\')
            {
                sb.Append('_');
                continue;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string ResolveRepositoryDirectoryPath(PluginHostOptions options)
    {
        var configuredDirectory = string.IsNullOrWhiteSpace(options.RepositoryDirectory)
            ? "repositories"
            : options.RepositoryDirectory.Trim();

        if (Path.IsPathRooted(configuredDirectory))
        {
            return Path.GetFullPath(configuredDirectory);
        }

        var manifestDirectory = string.IsNullOrWhiteSpace(options.ManifestDirectory)
            ? Path.Combine(Directory.GetCurrentDirectory(), "manifests")
            : Path.GetFullPath(options.ManifestDirectory);

        var root = Directory.GetParent(manifestDirectory)?.FullName ?? Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(root, configuredDirectory));
    }
}

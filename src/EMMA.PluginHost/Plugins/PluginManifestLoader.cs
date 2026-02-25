using System.Text.Json;
using EMMA.PluginHost.Configuration;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Loads plugin manifests from disk based on configured conventions.
/// </summary>
public sealed class PluginManifestLoader(
    IOptions<PluginHostOptions> options,
    PluginPermissionSanitizer permissionSanitizer,
    ILogger<PluginManifestLoader> logger)
{
    private readonly PluginHostOptions _options = options.Value;
    private readonly PluginPermissionSanitizer _permissionSanitizer = permissionSanitizer;
    private readonly ILogger<PluginManifestLoader> _logger = logger;

    /// <summary>
    /// Loads all manifests from the configured directory.
    /// </summary>
    public async Task<IReadOnlyList<PluginManifest>> LoadManifestsAsync(CancellationToken cancellationToken)
    {
        var directory = _options.ManifestDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning("Plugin manifest directory not found: {Directory}", directory);
            }
            return [];
        }

        var manifests = new List<PluginManifest>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.plugin.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var manifest = await JsonSerializer.DeserializeAsync<PluginManifest>(
                    stream,
                    PluginManifestDefaults.JsonOptions,
                    cancellationToken);

                if (manifest is null)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.LogWarning("Plugin manifest is empty: {Path}", path);
                    }
                    continue;
                }

                if (string.IsNullOrWhiteSpace(manifest.Id))
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.LogWarning("Plugin manifest missing id: {Path}", path);
                    }
                    continue;
                }

                var sanitizedPermissions = SanitizePermissions(manifest.Id, manifest.Permissions);
                if (!ReferenceEquals(sanitizedPermissions, manifest.Permissions))
                {
                    manifest = manifest with { Permissions = sanitizedPermissions };
                }

                manifests.Add(manifest);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(ex, "Failed to load plugin manifest: {Path}", path);
                }
            }
        }

        return manifests;
    }

    private PluginManifestPermissions? SanitizePermissions(string pluginId, PluginManifestPermissions? permissions)
    {
        if (permissions is null)
        {
            return null;
        }

        var sanitizedPaths = _permissionSanitizer.SanitizePaths(pluginId, permissions.Paths, "manifest");
        if (ReferenceEquals(sanitizedPaths, permissions.Paths))
        {
            return permissions;
        }

        return permissions with { Paths = sanitizedPaths };
    }
}

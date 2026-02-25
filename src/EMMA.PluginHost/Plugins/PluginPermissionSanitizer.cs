using EMMA.PluginHost.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Plugins;

public sealed class PluginPermissionSanitizer(IOptions<PluginHostOptions> options, ILogger<PluginPermissionSanitizer> logger)
{
    private readonly PluginHostOptions _options = options.Value;
    private readonly ILogger<PluginPermissionSanitizer> _logger = logger;

    public IReadOnlyList<string>? SanitizePaths(string pluginId, IReadOnlyList<string>? paths, string source)
    {
        if (paths is null || paths.Count == 0)
        {
            return paths;
        }

        var pluginRoot = Path.GetFullPath(Path.Combine(_options.SandboxRootDirectory, pluginId));
        var pluginRootPrefix = pluginRoot + Path.DirectorySeparatorChar;
        var sanitized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                changed = true;
                continue;
            }

            if (Path.IsPathRooted(path))
            {
                LogRejectedPath(pluginId, path, source, "rooted paths are not allowed");
                changed = true;
                continue;
            }

            var combined = Path.GetFullPath(Path.Combine(pluginRoot, path));
            if (!combined.StartsWith(pluginRootPrefix, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(combined, pluginRoot, StringComparison.OrdinalIgnoreCase))
            {
                LogRejectedPath(pluginId, path, source, "path escapes plugin sandbox");
                changed = true;
                continue;
            }

            if (!seen.Add(combined))
            {
                changed = true;
                continue;
            }

            if (!string.Equals(path, combined, StringComparison.Ordinal))
            {
                changed = true;
            }

            sanitized.Add(combined);
        }

        return changed ? sanitized : paths;
    }

    private void LogRejectedPath(string pluginId, string path, string source, string reason)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "Rejected plugin path {Path} from {Source} for {PluginId}: {Reason}",
                path,
                source,
                pluginId,
                reason);
        }
    }
}

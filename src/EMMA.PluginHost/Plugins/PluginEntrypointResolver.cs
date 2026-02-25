using EMMA.PluginHost.Configuration;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Plugins;

public interface IPluginEntrypointResolver
{
    string GetPluginRoot(string pluginId);
    string ResolveEntrypoint(PluginManifest manifest);
}

public sealed class PluginEntrypointResolver(IOptions<PluginHostOptions> options) : IPluginEntrypointResolver
{
    private readonly PluginHostOptions _options = options.Value;

    public string GetPluginRoot(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            throw new InvalidOperationException("Plugin id is required to resolve the sandbox root.");
        }

        return Path.GetFullPath(Path.Combine(_options.SandboxRootDirectory, pluginId));
    }

    public string ResolveEntrypoint(PluginManifest manifest)
    {
        if (manifest.Entry is null)
        {
            throw new InvalidOperationException("Plugin entry is missing.");
        }

        var entrypoint = manifest.Entry.Entrypoint?.Trim();
        if (string.IsNullOrWhiteSpace(entrypoint))
        {
            throw new InvalidOperationException("Plugin entrypoint is missing.");
        }

        var pluginRoot = GetPluginRoot(manifest.Id);
        return ResolveEntrypointPath(pluginRoot, entrypoint);
    }

    private static string ResolveEntrypointPath(string pluginRoot, string entrypoint)
    {
        if (Path.IsPathRooted(entrypoint))
        {
            throw new InvalidOperationException("Plugin entrypoint must be a file name.");
        }

        if (entrypoint.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidOperationException("Plugin entrypoint must not include directories.");
        }

        if (entrypoint.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("Plugin entrypoint contains invalid characters.");
        }

        var candidate = Path.Combine(pluginRoot, entrypoint);
        if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Path.GetExtension(candidate)))
        {
            var exeCandidate = candidate + ".exe";
            if (File.Exists(exeCandidate))
            {
                return exeCandidate;
            }
        }

        if (!File.Exists(candidate))
        {
            throw new InvalidOperationException("Plugin entrypoint executable not found.");
        }

        return candidate;
    }
}

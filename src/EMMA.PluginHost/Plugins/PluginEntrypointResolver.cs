using EMMA.PluginHost.Configuration;
using Microsoft.Extensions.Options;
using System.Xml.Linq;

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
        if (string.IsNullOrWhiteSpace(manifest.Protocol))
        {
            throw new InvalidOperationException("Plugin protocol is missing.");
        }

        var pluginRoot = GetPluginRoot(manifest.Id);
        return ResolveDefaultEntrypoint(pluginRoot, manifest);
    }

    private static string ResolveDefaultEntrypoint(string pluginRoot, PluginManifest manifest)
    {
        if (OperatingSystem.IsMacOS())
        {
            var appCandidates = Directory.EnumerateDirectories(pluginRoot, "*.app", SearchOption.TopDirectoryOnly)
                .ToList();
            if (appCandidates.Count == 1)
            {
                return ResolveAppBundle(appCandidates[0], Path.GetFileName(appCandidates[0]));
            }
        }

        var candidates = BuildNameCandidates(manifest);
        foreach (var name in candidates)
        {
            if (TryResolveCandidate(pluginRoot, name, out var resolved))
            {
                return resolved;
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            throw new InvalidOperationException("Plugin executable not found; no .app bundle was found.");
        }

        throw new InvalidOperationException("Plugin executable not found; no matching binary was found.");
    }

    private static List<string> BuildNameCandidates(PluginManifest manifest)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(manifest.Name))
        {
            names.Add(RemoveSpaces(manifest.Name));
        }

        if (!string.IsNullOrWhiteSpace(manifest.Id))
        {
            names.Add(manifest.Id);
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string RemoveSpaces(string value)
    {
        return new string(value.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
    }

    private static bool TryResolveCandidate(string pluginRoot, string name, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (OperatingSystem.IsMacOS())
        {
            var bundlePath = Path.Combine(pluginRoot, name + ".app");
            if (Directory.Exists(bundlePath))
            {
                resolved = ResolveAppBundle(bundlePath, Path.GetFileName(bundlePath));
                return true;
            }
        }

        var candidate = Path.Combine(pluginRoot, name);
        if (OperatingSystem.IsWindows())
        {
            var exeCandidate = candidate + ".exe";
            if (File.Exists(exeCandidate))
            {
                resolved = exeCandidate;
                return true;
            }
        }

        if (File.Exists(candidate))
        {
            resolved = candidate;
            return true;
        }

        return false;
    }

    private static string ResolveAppBundle(string bundlePath, string entrypoint)
    {
        if (!Directory.Exists(bundlePath))
        {
            throw new InvalidOperationException("Plugin app bundle not found.");
        }

        var infoPlist = Path.Combine(bundlePath, "Contents", "Info.plist");
        if (!File.Exists(infoPlist))
        {
            throw new InvalidOperationException("Plugin app bundle is missing Info.plist.");
        }

        var executable = ReadBundleExecutable(infoPlist)
            ?? Path.GetFileNameWithoutExtension(entrypoint);

        var candidate = Path.Combine(bundlePath, "Contents", "MacOS", executable);
        if (!File.Exists(candidate))
        {
            throw new InvalidOperationException("Plugin app bundle executable not found.");
        }

        return candidate;
    }

    private static string? ReadBundleExecutable(string infoPlist)
    {
        try
        {
            var document = XDocument.Load(infoPlist);
            var dict = document.Root?.Element("dict");
            if (dict is null)
            {
                return null;
            }

            string? key = null;
            foreach (var node in dict.Elements())
            {
                if (node.Name.LocalName == "key")
                {
                    key = node.Value;
                    continue;
                }

                if (key == "CFBundleExecutable")
                {
                    return node.Value;
                }

                key = null;
            }
        }
        catch
        {
        }

        return null;
    }
}

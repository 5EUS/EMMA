using EMMA.PluginHost.Configuration;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Plugins;

public interface IPluginEntrypointResolver
{
    string GetPluginRoot(string pluginId);
    string ResolveEntrypoint(PluginManifest manifest);
    bool TryResolveWasmComponent(PluginManifest manifest, out string componentPath);
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

    public bool TryResolveWasmComponent(PluginManifest manifest, out string componentPath)
    {
        componentPath = string.Empty;
        var pluginRoot = GetPluginRoot(manifest.Id);
        if (!Directory.Exists(pluginRoot))
        {
            return false;
        }

        var candidates = BuildWasmComponentCandidates(pluginRoot, manifest);

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate) && IsWasmComponentBinary(candidate))
            {
                componentPath = candidate;
                return true;
            }
        }

        var wildcard = Directory.EnumerateFiles(pluginRoot, "*.cwasm", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(pluginRoot, "*.wasm", SearchOption.TopDirectoryOnly))
            .Where(IsWasmComponentBinary)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (wildcard.Count == 1)
        {
            componentPath = wildcard[0];
            return true;
        }

        return false;
    }

    private static bool IsWasmComponentBinary(string path)
    {
        var extension = Path.GetExtension(path);
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) != header.Length)
        {
            return false;
        }

        if (string.Equals(extension, ".cwasm", StringComparison.OrdinalIgnoreCase))
        {
            return IsWasmComponentHeader(header)
                || IsElfHeader(header)
                || IsMachOHeader(header);
        }

        return IsWasmComponentHeader(header);
    }

    private static bool IsWasmComponentHeader(ReadOnlySpan<byte> header)
    {
        return header[0] == 0x00
            && header[1] == 0x61
            && header[2] == 0x73
            && header[3] == 0x6D
            && header[4] == 0x0D
            && header[5] == 0x00
            && header[6] == 0x01
            && header[7] == 0x00;
    }

    private static bool IsElfHeader(ReadOnlySpan<byte> header)
    {
        return header[0] == 0x7F
            && header[1] == 0x45
            && header[2] == 0x4C
            && header[3] == 0x46;
    }

    private static bool IsMachOHeader(ReadOnlySpan<byte> header)
    {
        var magic = (uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]);
        return magic is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE or 0xBEBAFECA;
    }

    private static string ResolveDefaultEntrypoint(string pluginRoot, PluginManifest manifest)
    {
        var candidates = BuildNameCandidates(manifest);
        foreach (var name in candidates)
        {
            if (TryResolveCandidate(pluginRoot, name, out var resolved))
            {
                return resolved;
            }
        }

        throw new InvalidOperationException("Plugin executable not found; no matching binary was found.");
    }

    private static IEnumerable<string> BuildWasmComponentCandidates(string pluginRoot, PluginManifest manifest)
    {
        var names = new List<string> { "plugin" };
        if (!string.IsNullOrWhiteSpace(manifest.Id))
        {
            names.Add(manifest.Id);
        }

        if (!string.IsNullOrWhiteSpace(manifest.Name))
        {
            names.Add(RemoveSpaces(manifest.Name));
        }

        var uniqueNames = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var name in uniqueNames)
        {
            yield return Path.Combine(pluginRoot, name + ".cwasm");
            yield return Path.Combine(pluginRoot, name + ".wasm");
            yield return Path.Combine(pluginRoot, "wasm", name + ".cwasm");
            yield return Path.Combine(pluginRoot, "wasm", name + ".wasm");
        }
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

        var candidate = Path.Combine(pluginRoot, name);
        var exeCandidate = candidate + ".exe";
        if (File.Exists(exeCandidate))
        {
            resolved = exeCandidate;
            return true;
        }

        if (File.Exists(candidate))
        {
            resolved = candidate;
            return true;
        }

        return false;
    }
}

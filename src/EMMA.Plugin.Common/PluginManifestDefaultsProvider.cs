using System.Text.Json;

namespace EMMA.Plugin.Common;

/// <summary>
/// Represents default runtime limits and permissions loaded from a plugin manifest.
/// </summary>
/// <param name="CpuBudgetMs">The default CPU budget in milliseconds.</param>
/// <param name="MemoryMb">The default memory budget in megabytes.</param>
/// <param name="Domains">The default allowed network domains.</param>
/// <param name="Paths">The default allowed filesystem paths.</param>
public readonly record struct PluginManifestDefaults(
    int CpuBudgetMs,
    int MemoryMb,
    string[] Domains,
    string[] Paths);

/// <summary>
/// Loads manifest-derived defaults for plugin runtime configuration.
/// </summary>
public static class PluginManifestDefaultsProvider
{
    /// <summary>
    /// Loads manifest default values from the first matching plugin manifest file, or falls back to the supplied defaults.
    /// </summary>
    /// <param name="pluginManifestFileName">The manifest file name to search for.</param>
    /// <param name="fallback">The fallback values to return when no manifest can be read.</param>
    /// <param name="pluginProjectFolderName">An optional project folder name used to probe a nested <c>src</c> path.</param>
    /// <returns>The manifest defaults loaded from disk, or the fallback values when loading fails.</returns>
    public static PluginManifestDefaults Load(
        string pluginManifestFileName,
        PluginManifestDefaults fallback,
        string? pluginProjectFolderName = null)
    {
        foreach (var manifestPath in EnumerateManifestCandidates(pluginManifestFileName, pluginProjectFolderName))
        {
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = doc.RootElement;

                var capabilities = root.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object
                    ? caps
                    : default;

                var cpu = capabilities.ValueKind == JsonValueKind.Object
                    && capabilities.TryGetProperty("cpuBudgetMs", out var cpuElement)
                    && cpuElement.TryGetInt32(out var parsedCpu)
                        ? parsedCpu
                        : fallback.CpuBudgetMs;

                var memory = capabilities.ValueKind == JsonValueKind.Object
                    && capabilities.TryGetProperty("memoryMb", out var memElement)
                    && memElement.TryGetInt32(out var parsedMem)
                        ? parsedMem
                        : fallback.MemoryMb;

                var permissions = root.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Object
                    ? perms
                    : default;

                var domains = ReadStringArray(permissions, "domains", fallback.Domains);
                var paths = ReadStringArray(permissions, "paths", fallback.Paths);

                return new PluginManifestDefaults(cpu, memory, domains, paths);
            }
            catch
            {
            }
        }

        return fallback;
    }

    private static IEnumerable<string> EnumerateManifestCandidates(string pluginManifestFileName, string? pluginProjectFolderName)
    {
        yield return Path.Combine(AppContext.BaseDirectory, pluginManifestFileName);
        yield return Path.Combine(Directory.GetCurrentDirectory(), pluginManifestFileName);

        if (!string.IsNullOrWhiteSpace(pluginProjectFolderName))
        {
            yield return Path.Combine(Directory.GetCurrentDirectory(), "src", pluginProjectFolderName, pluginManifestFileName);
        }
    }

    private static string[] ReadStringArray(JsonElement permissions, string propertyName, IReadOnlyList<string> fallback)
    {
        if (permissions.ValueKind != JsonValueKind.Object
            || !permissions.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Array)
        {
            return [.. fallback];
        }

        return [.. element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)];
    }
}

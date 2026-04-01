using System.Text.Json;

namespace EMMA.Plugin.Common;

public readonly record struct PluginManifestDefaults(
    int CpuBudgetMs,
    int MemoryMb,
    string[] Domains,
    string[] Paths);

public static class PluginManifestDefaultsProvider
{
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

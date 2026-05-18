namespace EMMA.Cli;

public sealed record PluginDevDesignTimeBuildProfileSyncResult(string FilePath, string PluginTransport);

public sealed class PluginDevDesignTimeBuildProfileSync
{
    private const string RelativePropsPath = "obj/EMMA.PluginDev.props";

    public PluginDevDesignTimeBuildProfileSyncResult Sync(string rootDirectory, PluginDevProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(profile);

        var pluginTransport = profile.RuntimeTarget == PluginRuntimeTarget.Wasm
            ? "Wasm"
            : "AspNet";
        var propsPath = Path.Combine(rootDirectory, RelativePropsPath);
        var propsDirectory = Path.GetDirectoryName(propsPath);
        if (!string.IsNullOrWhiteSpace(propsDirectory))
        {
            Directory.CreateDirectory(propsDirectory);
        }

        var content = string.Join(
            Environment.NewLine,
            "<Project>",
            "  <PropertyGroup>",
            $"    <PluginTransport>{pluginTransport}</PluginTransport>",
            $"    <EMMAPluginDevProfile>{Escape(profile.Name)}</EMMAPluginDevProfile>",
            $"    <EMMAPluginRuntimeTarget>{profile.RuntimeTarget}</EMMAPluginRuntimeTarget>",
            "  </PropertyGroup>",
            "</Project>",
            string.Empty);

        if (!File.Exists(propsPath) || !string.Equals(File.ReadAllText(propsPath), content, StringComparison.Ordinal))
        {
            File.WriteAllText(propsPath, content);
        }

        return new PluginDevDesignTimeBuildProfileSyncResult(propsPath, pluginTransport);
    }

    private static string Escape(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }
}
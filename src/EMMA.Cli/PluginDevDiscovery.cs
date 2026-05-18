using System.Text.Json;
using System.Xml.Linq;

namespace EMMA.Cli;

public sealed record PluginDevArtifactCandidate(
    PluginRuntimeTarget Target,
    string Path,
    bool Exists,
    string Kind);

public sealed record PluginDevDiscoveryResult(
    string RootDirectory,
    string? ProjectFilePath,
    string? ManifestPath,
    string? PluginId,
    string? PluginName,
    IReadOnlyList<string> MediaTypes,
    IReadOnlyList<string> PermittedDomains,
    IReadOnlyList<PluginRuntimeTarget> SupportedTargets,
    IReadOnlyList<PluginDevArtifactCandidate> ArtifactCandidates);

public sealed class PluginDevDiscoveryService
{
    public PluginDevDiscoveryResult Discover(string workingDirectory)
    {
        var rootDirectory = FindNearestPluginRoot(workingDirectory) ?? workingDirectory;
        var manifestPath = Directory.EnumerateFiles(rootDirectory, "*.plugin.json", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        var projectFilePaths = Directory.EnumerateFiles(rootDirectory, "*.csproj", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var projectFilePath = SelectPreferredProject(projectFilePaths, manifestPath);

        var manifest = ReadManifest(manifestPath);
        var supportedTargets = DiscoverSupportedTargets(projectFilePaths);
        var artifactCandidates = BuildArtifactCandidates(rootDirectory, supportedTargets);

        return new PluginDevDiscoveryResult(
            rootDirectory,
            projectFilePath,
            manifestPath,
            manifest.PluginId,
            manifest.PluginName,
            manifest.MediaTypes,
            manifest.PermittedDomains,
            supportedTargets,
            artifactCandidates);
    }

    private static string? FindNearestPluginRoot(string workingDirectory)
    {
        var directory = new DirectoryInfo(workingDirectory);
        while (directory is not null)
        {
            var hasManifest = Directory.EnumerateFiles(directory.FullName, "*.plugin.json", SearchOption.TopDirectoryOnly).Any();
            var hasProject = Directory.EnumerateFiles(directory.FullName, "*.csproj", SearchOption.TopDirectoryOnly).Any();

            if (hasManifest || hasProject)
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static (string? PluginId, string? PluginName, IReadOnlyList<string> MediaTypes, IReadOnlyList<string> PermittedDomains) ReadManifest(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            return (null, null, Array.Empty<string>(), Array.Empty<string>());
        }

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;

        var pluginId = root.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
        var pluginName = root.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
        var mediaTypes = root.TryGetProperty("mediaTypes", out var mediaNode) && mediaNode.ValueKind == JsonValueKind.Array
            ? mediaNode.EnumerateArray().Select(static item => item.GetString()).Where(static item => !string.IsNullOrWhiteSpace(item)).Select(static item => item!).ToArray()
            : Array.Empty<string>();
        var permittedDomains = root.TryGetProperty("permissions", out var permissionsNode)
            && permissionsNode.ValueKind == JsonValueKind.Object
            && permissionsNode.TryGetProperty("domains", out var domainsNode)
            && domainsNode.ValueKind == JsonValueKind.Array
                ? domainsNode.EnumerateArray().Select(static item => item.GetString()).Where(static item => !string.IsNullOrWhiteSpace(item)).Select(static item => item!).ToArray()
                : Array.Empty<string>();

        return (pluginId, pluginName, mediaTypes, permittedDomains);
    }

    private static string? SelectPreferredProject(IReadOnlyList<string> projectFilePaths, string? manifestPath)
    {
        if (projectFilePaths.Count == 0)
        {
            return null;
        }

        var manifestStem = string.IsNullOrWhiteSpace(manifestPath)
            ? null
            : Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(manifestPath));

        if (!string.IsNullOrWhiteSpace(manifestStem))
        {
            var exactMatch = projectFilePaths.FirstOrDefault(path =>
                string.Equals(Path.GetFileNameWithoutExtension(path), manifestStem, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exactMatch))
            {
                return exactMatch;
            }
        }

        return projectFilePaths
            .OrderByDescending(ProjectOutputTypeIsExe)
            .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static IReadOnlyList<PluginRuntimeTarget> DiscoverSupportedTargets(IEnumerable<string> projectFilePaths)
    {
        var supportedTargets = new HashSet<PluginRuntimeTarget>();

        foreach (var projectFilePath in projectFilePaths)
        {
            if (string.IsNullOrWhiteSpace(projectFilePath) || !File.Exists(projectFilePath))
            {
                continue;
            }

            var content = File.ReadAllText(projectFilePath);
            if (content.Contains("wasi-wasm", StringComparison.OrdinalIgnoreCase)
                || content.Contains("BytecodeAlliance.Componentize.DotNet.Wasm.SDK", StringComparison.OrdinalIgnoreCase)
                || content.Contains("PLUGIN_TRANSPORT_WASM", StringComparison.OrdinalIgnoreCase))
            {
                supportedTargets.Add(PluginRuntimeTarget.Wasm);
            }

            if (content.Contains("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase)
                || content.Contains("EMMA.Plugin.AspNetCore", StringComparison.OrdinalIgnoreCase)
                || content.Contains("PLUGIN_TRANSPORT_ASPNET", StringComparison.OrdinalIgnoreCase)
                || ProjectOutputTypeIsExe(projectFilePath))
            {
                supportedTargets.Add(PluginRuntimeTarget.Linux);
                supportedTargets.Add(PluginRuntimeTarget.Windows);
            }
        }

        return supportedTargets.OrderBy(static item => item).ToArray();
    }

    private static bool ProjectOutputTypeIsExe(string projectFilePath)
    {
        var document = XDocument.Load(projectFilePath);
        return document.Descendants().Any(static node => node.Name.LocalName == "OutputType" && string.Equals(node.Value.Trim(), "Exe", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<PluginDevArtifactCandidate> BuildArtifactCandidates(string rootDirectory, IReadOnlyList<PluginRuntimeTarget> supportedTargets)
    {
        var candidates = new List<PluginDevArtifactCandidate>();

        if (supportedTargets.Contains(PluginRuntimeTarget.Wasm))
        {
            AddCandidate(candidates, PluginRuntimeTarget.Wasm, rootDirectory, "artifacts/build-wasm", "build-output");
            AddCandidate(candidates, PluginRuntimeTarget.Wasm, rootDirectory, "bin/Release/net10.0/wasi-wasm", "publish-output");
            AddCandidate(candidates, PluginRuntimeTarget.Wasm, rootDirectory, "artifacts/wasm", "artifact-root");
        }

        if (supportedTargets.Contains(PluginRuntimeTarget.Linux))
        {
            AddCandidate(candidates, PluginRuntimeTarget.Linux, rootDirectory, "artifacts/build-linux-x64", "build-output");
            AddCandidate(candidates, PluginRuntimeTarget.Linux, rootDirectory, "bin/Release/net10.0/linux-x64", "publish-output");
        }

        if (supportedTargets.Contains(PluginRuntimeTarget.Windows))
        {
            AddCandidate(candidates, PluginRuntimeTarget.Windows, rootDirectory, "artifacts/build-win-x64", "build-output");
            AddCandidate(candidates, PluginRuntimeTarget.Windows, rootDirectory, "bin/Release/net10.0/win-x64", "publish-output");
        }

        return candidates;
    }

    private static void AddCandidate(
        ICollection<PluginDevArtifactCandidate> candidates,
        PluginRuntimeTarget target,
        string rootDirectory,
        string relativePath,
        string kind)
    {
        var absolutePath = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));
        candidates.Add(new PluginDevArtifactCandidate(
            target,
            absolutePath,
            Directory.Exists(absolutePath) || File.Exists(absolutePath),
            kind));
    }
}
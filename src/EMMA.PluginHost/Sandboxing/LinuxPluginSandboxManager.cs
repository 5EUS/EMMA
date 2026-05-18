using System.Diagnostics;
using System.Text;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// Linux sandbox scaffolding placeholder (cgroups, seccomp, namespaces).
/// </summary>
/// <param name="options">The plugin host options.</param>
/// <param name="logger">The logger used for sandbox diagnostics.</param>
public sealed class LinuxPluginSandboxManager(IOptions<PluginHostOptions> options, ILogger<LinuxPluginSandboxManager> logger)
    : PluginSandboxManagerBase(options, logger)
{
    /// <summary>
    /// Gets the platform name used in diagnostics.
    /// </summary>
    protected override string PlatformName => "Linux";

    private const string BubblewrapName = "bwrap";

    /// <summary>
    /// Prepares sandbox resources for Linux-hosted plugins.
    /// </summary>
    /// <param name="manifest">The plugin manifest.</param>
    /// <param name="pluginRoot">The plugin sandbox root path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when bubblewrap is available; otherwise, it may return <see langword="false"/> when no-sandbox fallback is explicitly enabled.</returns>
    protected override Task<bool> PrepareSandboxAsync(
        PluginManifest manifest,
        string pluginRoot,
        CancellationToken cancellationToken)
    {
        var bwrapPath = FindExecutable(BubblewrapName);
        if (bwrapPath is not null)
        {
            return Task.FromResult(true);
        }

        if (!Options.AllowNoSandboxFallback)
        {
            throw new InvalidOperationException(
                "Linux sandbox requires bubblewrap (bwrap), but it was not found in PATH. "
                + "Install bubblewrap or set PluginHost:AllowNoSandboxFallback=true for explicit development/test usage.");
        }

        if (Logger.IsEnabled(LogLevel.Warning))
        {
            Logger.LogWarning(
                "Linux sandbox fallback enabled for plugin {PluginId}. bubblewrap was not found; running without sandbox by explicit configuration.",
                manifest.Id);
        }

        return Task.FromResult(false);
    }

    /// <summary>
    /// Wraps the plugin process start info with a bubblewrap sandbox when available.
    /// </summary>
    /// <param name="manifest">The plugin manifest.</param>
    /// <param name="startInfo">The process start configuration.</param>
    /// <returns>The updated process start configuration.</returns>
    public override ProcessStartInfo ApplyToStartInfo(PluginManifest manifest, ProcessStartInfo startInfo)
    {
        if (!Options.SandboxEnabled)
        {
            return startInfo;
        }

        // bubblewrap builds the sandbox; policy is defined by the mounts we pass.
        var bwrapPath = FindExecutable(BubblewrapName);
        if (bwrapPath is null)
        {
            return startInfo;
        }

        var pluginRoot = GetPluginRoot(manifest);
        var originalFile = startInfo.FileName;
        var originalArgs = startInfo.Arguments;
        var arguments = BuildBubblewrapArgs(
            pluginRoot,
            originalFile,
            originalArgs,
            manifest.Permissions?.Paths);

        startInfo.FileName = bwrapPath;
        startInfo.Arguments = arguments;
        startInfo.WorkingDirectory = pluginRoot;

        if (!startInfo.Environment.ContainsKey("EMMA_SANDBOX_ROOT"))
        {
            startInfo.Environment["EMMA_SANDBOX_ROOT"] = pluginRoot;
        }

        return startInfo;
    }

    private static string BuildBubblewrapArgs(
        string pluginRoot,
        string fileName,
        string args,
        IReadOnlyList<string>? allowedPaths)
    {
        var builder = new StringBuilder();
        // Use namespace isolation but share host networking so PluginHost can
        // reach plugin endpoints bound on localhost.
        builder.Append("--unshare-all --share-net --die-with-parent --new-session ");
        builder.Append("--proc /proc --dev /dev --tmpfs /tmp ");

        AppendReadOnlyBindIfExists(builder, "/usr");
        AppendReadOnlyBindIfExists(builder, "/bin");
        AppendReadOnlyBindIfExists(builder, "/lib");
        AppendReadOnlyBindIfExists(builder, "/lib64");
        AppendReadOnlyBindIfExists(builder, "/etc/ssl");
        AppendReadOnlyBindIfExists(builder, "/etc/resolv.conf");
        AppendReadOnlyBindIfExists(builder, "/etc/hosts");
        AppendReadOnlyBindIfExists(builder, "/etc/nsswitch.conf");
        AppendReadOnlyBindIfExists(builder, "/etc/localtime");
        AppendReadOnlyBindIfExists(builder, "/usr/share/zoneinfo");

        if (allowedPaths is not null)
        {
            foreach (var path in allowedPaths)
            {
                if (!string.IsNullOrWhiteSpace(path)
                    && (Directory.Exists(path) || File.Exists(path)))
                {
                    builder.Append($"--ro-bind {Quote(path)} {Quote(path)} ");
                }
            }
        }
        builder.Append($"--bind {Quote(pluginRoot)} /sandbox ");
        builder.Append("--chdir /sandbox ");
        builder.Append("-- ");
        builder.Append(Quote(ToSandboxEntrypoint(pluginRoot, fileName)));
        if (!string.IsNullOrWhiteSpace(args))
        {
            builder.Append(' ');
            builder.Append(args);
        }

        return builder.ToString();
    }

    private static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ToSandboxEntrypoint(string pluginRoot, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return fileName;
        }

        if (!Path.IsPathRooted(fileName))
        {
            return fileName;
        }

        var fullPluginRoot = Path.GetFullPath(pluginRoot);
        var fullFileName = Path.GetFullPath(fileName);
        var pluginPrefix = fullPluginRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullPluginRoot
            : fullPluginRoot + Path.DirectorySeparatorChar;

        if (!fullFileName.StartsWith(pluginPrefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullFileName, fullPluginRoot, StringComparison.OrdinalIgnoreCase))
        {
            return fileName;
        }

        var relative = Path.GetRelativePath(fullPluginRoot, fullFileName)
            .Replace(Path.DirectorySeparatorChar, '/');

        return relative == "." ? "/sandbox" : $"/sandbox/{relative}";
    }

    private static void AppendReadOnlyBindIfExists(StringBuilder builder, string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            builder.Append($"--ro-bind {Quote(path)} {Quote(path)} ");
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}

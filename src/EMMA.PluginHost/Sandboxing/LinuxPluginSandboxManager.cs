using System.Diagnostics;
using System.Text;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// Linux sandbox scaffolding placeholder (cgroups, seccomp, namespaces).
/// </summary>
public sealed class LinuxPluginSandboxManager(IOptions<PluginHostOptions> options, ILogger<LinuxPluginSandboxManager> logger)
    : PluginSandboxManagerBase(options, logger)
{
    protected override string PlatformName => "Linux";

    private const string BubblewrapName = "bwrap";

    protected override Task<bool> PrepareSandboxAsync(
        PluginManifest manifest,
        string pluginRoot,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(FindExecutable(BubblewrapName) is not null);
    }

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
        // --unshare-all disables host namespaces, including network.
        builder.Append("--unshare-all --die-with-parent --new-session ");
        builder.Append("--proc /proc --dev /dev --tmpfs /tmp ");
        builder.Append("--ro-bind /usr /usr --ro-bind /bin /bin --ro-bind /etc /etc ");
        if (Directory.Exists("/sbin"))
        {
            builder.Append("--ro-bind /sbin /sbin ");
        }
        if (Directory.Exists("/lib"))
        {
            builder.Append("--ro-bind /lib /lib ");
        }
        if (Directory.Exists("/lib64"))
        {
            builder.Append("--ro-bind /lib64 /lib64 ");
        }
        if (allowedPaths is not null)
        {
            foreach (var path in allowedPaths)
            {
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    builder.Append($"--ro-bind {Quote(path)} {Quote(path)} ");
                }
            }
        }
        builder.Append($"--bind {Quote(pluginRoot)} /sandbox ");
        builder.Append("--chdir /sandbox ");
        builder.Append("-- ");
        builder.Append(Quote(fileName));
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

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}

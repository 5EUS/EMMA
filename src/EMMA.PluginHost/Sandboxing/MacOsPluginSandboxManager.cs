using System.Diagnostics;
using System.Text;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// macOS sandbox scaffolding placeholder (App Sandbox / seatbelt profiles).
/// </summary>
public sealed class MacOsPluginSandboxManager(IOptions<PluginHostOptions> options, ILogger<MacOsPluginSandboxManager> logger)
    : PluginSandboxManagerBase(options, logger)
{
    protected override string PlatformName => "macOS";

    private const string SandboxExecPath = "/usr/bin/sandbox-exec";

    protected override Task<bool> PrepareSandboxAsync(
        PluginManifest manifest,
        string pluginRoot,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(SandboxExecPath))
        {
            return Task.FromResult(false);
        }

        // TODO sandbox-exec is legacy; we keep profiles minimal and explicit.
        var profilePath = GetProfilePath(manifest);
        var profile = BuildProfile(manifest, pluginRoot);
        TryMakeWritable(profilePath);
        File.WriteAllText(profilePath, profile);
        TryLockDownProfile(profilePath); // best effort to prevent tampering, but not critical since profile is read-only and sandbox-exec is legacy.

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation("macOS sandbox profile written: {Path}", profilePath);
        }

        return Task.FromResult(true);
    }

    public override ProcessStartInfo ApplyToStartInfo(PluginManifest manifest, ProcessStartInfo startInfo)
    {
        if (!Options.SandboxEnabled || !File.Exists(SandboxExecPath))
        {
            return startInfo;
        }

        var pluginRoot = GetPluginRoot(manifest);
        var profilePath = GetProfilePath(manifest);

        if (!File.Exists(profilePath))
        {
            return startInfo;
        }

        var originalFile = startInfo.FileName;
        var originalArgs = startInfo.Arguments;

        // Wrap the plugin process inside sandbox-exec with the generated profile.
        startInfo.FileName = SandboxExecPath;
        startInfo.Arguments = $"-f {Quote(profilePath)} -- {Quote(originalFile)} {originalArgs}".TrimEnd();

        return startInfo;
    }

    private string GetProfilePath(PluginManifest manifest)
    {
        var profilesRoot = Path.Combine(Options.SandboxRootDirectory, "profiles");
        Directory.CreateDirectory(profilesRoot);
        return Path.Combine(profilesRoot, $"{manifest.Id}.sb");
    }

    private static string BuildProfile(PluginManifest manifest, string pluginRoot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("(version 1)");
        builder.AppendLine("(deny default)");
        builder.AppendLine("(allow process*)");
        builder.AppendLine("(allow sysctl-read)");
        builder.AppendLine($"(allow file-read* (subpath \"{pluginRoot}\"))");
        builder.AppendLine($"(allow file-write* (subpath \"{pluginRoot}\"))");
        AppendReadOnlyPath(builder, "/usr"); // TODO what is needed?
        AppendReadOnlyPath(builder, "/bin");
        AppendReadOnlyPath(builder, "/sbin");
        AppendReadOnlyPath(builder, "/System");
        AppendReadOnlyPath(builder, "/Library");

        if (manifest.Permissions?.Paths is not null)
        {
            foreach (var path in manifest.Permissions.Paths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    builder.AppendLine($"(allow file-read* (subpath \"{path}\"))");
                }
            }
        }

        if (manifest.Permissions?.Domains is not null && manifest.Permissions.Domains.Count > 0)
        {
            foreach (var domain in manifest.Permissions.Domains)
            {
                if (!string.IsNullOrWhiteSpace(domain))
                {
                    builder.AppendLine($"(allow network-outbound (remote domain \"{domain}\"))");
                }
            }
        }

        return builder.ToString();
    }

    private static void AppendReadOnlyPath(StringBuilder builder, string path)
    {
        if (Directory.Exists(path))
        {
            builder.AppendLine($"(allow file-read* (subpath \"{path}\"))");
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static void TryLockDownProfile(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        catch
        {
        }
    }

    private static void TryMakeWritable(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            if (File.Exists(path))
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }
        catch
        {
        }
    }
}

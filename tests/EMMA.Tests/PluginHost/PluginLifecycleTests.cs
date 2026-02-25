using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using EMMA.PluginHost.Sandboxing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EMMA.Tests.PluginHost;

public sealed class PluginLifecycleTests
{
    [Fact]
    public async Task ProcessManager_StartStopAndRecover()
    {
        var port = GetFreePort();
        var projectPath = ResolveTestPluginProject();
        EnsureTestPluginBuilt(projectPath);
        var entrypointPath = ResolveTestPluginEntrypoint(projectPath);

        var options = Options.Create(new PluginHostOptions
        {
            StartupTimeoutSeconds = 20,
            StartupProbeIntervalMs = 200,
            TimeoutBackoffSeconds = 1,
            MaxTimeoutRetries = 2,
            SandboxRootDirectory = Path.Combine(Path.GetTempPath(), "emma-plugin-tests", Guid.NewGuid().ToString("N"), "sandbox")
        });

        var entrypointName = CopyEntrypointToSandbox(entrypointPath, options.Value.SandboxRootDirectory, "demo");
        var manifest = new PluginManifest(
            "demo",
            "Demo Plugin",
            "1.0.0",
            new PluginManifestEntry(
                "grpc",
                $"http://localhost:{port}",
                entrypointName),
            null,
            null,
            null,
            null,
            null,
            null);

        var sandbox = new NoOpPluginSandboxManager(options, NullLogger<NoOpPluginSandboxManager>.Instance);
        var signatureOptions = Options.Create(new PluginSignatureOptions());
        var verifier = new HmacPluginSignatureVerifier(signatureOptions);
        var resolver = new PluginEntrypointResolver(options);
        var manager = new PluginProcessManager(
            options,
            sandbox,
            resolver,
            signatureOptions,
            verifier,
            NullLogger<PluginProcessManager>.Instance);
        var current = PluginRuntimeStatus.Unknown();

        var previousPort = Environment.GetEnvironmentVariable("EMMA_TEST_PLUGIN_PORT");
        Environment.SetEnvironmentVariable("EMMA_TEST_PLUGIN_PORT", port.ToString());
        try
        {
            var started = await manager.EnsureStartedAsync(manifest, current, CancellationToken.None);
            Assert.Equal(PluginRuntimeState.Running, started.State);

            await manager.StopAsync(manifest.Id, CancellationToken.None);
            var restarted = await manager.EnsureStartedAsync(manifest, started, CancellationToken.None);
            Assert.Equal(PluginRuntimeState.Running, restarted.State);

            await manager.StopAsync(manifest.Id, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EMMA_TEST_PLUGIN_PORT", previousPort);
        }
    }

    [Fact]
    public async Task TimeoutBackoff_BlocksRestartUntilReady()
    {
        var port = GetFreePort();
        var projectPath = ResolveTestPluginProject();
        EnsureTestPluginBuilt(projectPath);
        var entrypointPath = ResolveTestPluginEntrypoint(projectPath);

        var options = Options.Create(new PluginHostOptions
        {
            StartupTimeoutSeconds = 20,
            StartupProbeIntervalMs = 200,
            TimeoutBackoffSeconds = 1,
            MaxTimeoutRetries = 2,
            SandboxRootDirectory = Path.Combine(Path.GetTempPath(), "emma-plugin-tests", Guid.NewGuid().ToString("N"), "sandbox")
        });

        var entrypointName = CopyEntrypointToSandbox(entrypointPath, options.Value.SandboxRootDirectory, "demo");
        var manifest = new PluginManifest(
            "demo",
            "Demo Plugin",
            "1.0.0",
            new PluginManifestEntry(
                "grpc",
                $"http://localhost:{port}",
                entrypointName),
            null,
            null,
            null,
            null,
            null,
            null);

        var sandbox = new NoOpPluginSandboxManager(options, NullLogger<NoOpPluginSandboxManager>.Instance);
        var signatureOptions = Options.Create(new PluginSignatureOptions());
        var verifier = new HmacPluginSignatureVerifier(signatureOptions);
        var resolver = new PluginEntrypointResolver(options);
        var manager = new PluginProcessManager(
            options,
            sandbox,
            resolver,
            signatureOptions,
            verifier,
            NullLogger<PluginProcessManager>.Instance);
        var current = PluginRuntimeStatus.Unknown().WithRetry(1, DateTimeOffset.UtcNow.AddSeconds(5), "rpc-timeout", "timeout");

        var previousPort = Environment.GetEnvironmentVariable("EMMA_TEST_PLUGIN_PORT");
        Environment.SetEnvironmentVariable("EMMA_TEST_PLUGIN_PORT", port.ToString());
        try
        {
            var blocked = await manager.EnsureStartedAsync(manifest, current, CancellationToken.None);
            Assert.Equal(PluginRuntimeState.Timeout, blocked.State);

            var ready = blocked with { NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
            var started = await manager.EnsureStartedAsync(manifest, ready, CancellationToken.None);
            Assert.Equal(PluginRuntimeState.Running, started.State);

            await manager.StopAsync(manifest.Id, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EMMA_TEST_PLUGIN_PORT", previousPort);
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string ResolveTestPluginProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var solution = Path.Combine(dir.FullName, "EMMA.sln");
            if (File.Exists(solution))
            {
                var path = Path.Combine(
                    dir.FullName,
                    "src",
                    "EMMA.TestPlugin",
                    "EMMA.TestPlugin.csproj");

                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("Test plugin project not found.", path);
                }

                return path;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Failed to locate repository root.");
    }

    private static string ResolveTestPluginEntrypoint(string projectPath)
    {
        var projectDir = Path.GetDirectoryName(projectPath) ?? throw new DirectoryNotFoundException(projectPath);
        var outputDir = Path.Combine(projectDir, "bin", "Debug", "net10.0");
        var baseName = "EMMA.TestPlugin";
        var candidates = new List<string>
        {
            Path.Combine(outputDir, baseName)
        };

        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(outputDir, baseName + ".exe"));
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Test plugin entrypoint not found.", outputDir);
    }

    private static string CopyEntrypointToSandbox(string entrypointPath, string sandboxRoot, string pluginId)
    {
        var pluginRoot = Path.Combine(sandboxRoot, pluginId);
        Directory.CreateDirectory(pluginRoot);
        var sourceDir = Path.GetDirectoryName(entrypointPath) ?? throw new DirectoryNotFoundException(entrypointPath);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var target = Path.Combine(pluginRoot, Path.GetFileName(file));
            File.Copy(file, target, true);
        }
        var fileName = Path.GetFileName(entrypointPath);
        var destination = Path.Combine(pluginRoot, fileName);
        TryMakeExecutable(destination);

        return fileName;
    }

    private static void TryMakeExecutable(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            var mode = File.GetUnixFileMode(path);
            mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode);
        }
        catch
        {
        }
    }

    private static void EnsureTestPluginBuilt(string projectPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{projectPath}\" -c Debug -f net10.0",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start dotnet build.");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet build failed.\n{output}\n{error}");
        }
    }
}

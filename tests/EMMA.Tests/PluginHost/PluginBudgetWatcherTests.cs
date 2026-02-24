using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using EMMA.PluginHost.Sandboxing;
using EMMA.PluginHost.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EMMA.Tests.PluginHost;

public sealed class PluginBudgetWatcherTests
{
    [Fact]
    public async Task ExceedingBudgets_QuarantinesPlugin()
    {
        var options = Options.Create(new PluginHostOptions
        {
            BudgetWatchIntervalSeconds = 1,
            MaxCpuBudgetMs = 100,
            MaxMemoryMb = 100
        });

        var registry = new PluginRegistry();
        var manifest = new PluginManifest(
            "demo",
            "Demo Plugin",
            "1.0.0",
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        var status = new PluginHandshakeStatus(
            true,
            "ok",
            "1.0.0",
            DateTimeOffset.UtcNow,
            [],
            200,
            250,
            [],
            []);

        registry.Upsert(manifest, status, PluginRuntimeStatus.Running());

        var sandbox = new NoOpPluginSandboxManager(options, NullLogger<NoOpPluginSandboxManager>.Instance);
        var signatureOptions = Options.Create(new PluginSignatureOptions());
        var verifier = new HmacPluginSignatureVerifier(signatureOptions);
        var processManager = new PluginProcessManager(
            options,
            sandbox,
            signatureOptions,
            verifier,
            NullLogger<PluginProcessManager>.Instance);
        var watcher = new PluginBudgetWatcher(registry, processManager, options, NullLogger<PluginBudgetWatcher>.Instance);

        await using var run = new BudgetWatchRun(watcher);
        await run.StartAsync();

        var runtime = await WaitForRuntimeAsync(registry, manifest.Id, PluginRuntimeState.Quarantined, TimeSpan.FromSeconds(3));

        Assert.Equal(PluginRuntimeState.Quarantined, runtime.State);
        Assert.Equal("budget-exceeded", runtime.LastErrorCode);
    }

    [Fact]
    public async Task WithinBudgets_DoesNotQuarantine()
    {
        var options = Options.Create(new PluginHostOptions
        {
            BudgetWatchIntervalSeconds = 1,
            MaxCpuBudgetMs = 500,
            MaxMemoryMb = 500
        });

        var registry = new PluginRegistry();
        var manifest = new PluginManifest(
            "demo",
            "Demo Plugin",
            "1.0.0",
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        var status = new PluginHandshakeStatus(
            true,
            "ok",
            "1.0.0",
            DateTimeOffset.UtcNow,
            [],
            200,
            250,
            [],
            []);

        registry.Upsert(manifest, status, PluginRuntimeStatus.Running());

        var sandbox = new NoOpPluginSandboxManager(options, NullLogger<NoOpPluginSandboxManager>.Instance);
        var signatureOptions = Options.Create(new PluginSignatureOptions());
        var verifier = new HmacPluginSignatureVerifier(signatureOptions);
        var processManager = new PluginProcessManager(
            options,
            sandbox,
            signatureOptions,
            verifier,
            NullLogger<PluginProcessManager>.Instance);
        var watcher = new PluginBudgetWatcher(registry, processManager, options, NullLogger<PluginBudgetWatcher>.Instance);

        await using var run = new BudgetWatchRun(watcher);
        await run.StartAsync();

        var runtime = await WaitForRuntimeAsync(registry, manifest.Id, PluginRuntimeState.Running, TimeSpan.FromSeconds(2));
        Assert.Equal(PluginRuntimeState.Running, runtime.State);
    }

    private static async Task<PluginRuntimeStatus> WaitForRuntimeAsync(
        PluginRegistry registry,
        string pluginId,
        PluginRuntimeState expected,
        TimeSpan timeout)
    {
        var stopAt = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < stopAt)
        {
            var record = registry.GetSnapshot().FirstOrDefault(item =>
                string.Equals(item.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));

            if (record is not null && record.Runtime.State == expected)
            {
                return record.Runtime;
            }

            await Task.Delay(50);
        }

        var latest = registry.GetSnapshot().FirstOrDefault(item =>
            string.Equals(item.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        return latest?.Runtime ?? PluginRuntimeStatus.Unknown();
    }

    private sealed class BudgetWatchRun : IAsyncDisposable
    {
        private readonly PluginBudgetWatcher _watcher;
        private readonly CancellationTokenSource _cts = new();

        public BudgetWatchRun(PluginBudgetWatcher watcher)
        {
            _watcher = watcher;
        }

        public async Task StartAsync()
        {
            await _watcher.StartAsync(_cts.Token);
            await Task.Delay(1100);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await _watcher.StopAsync(CancellationToken.None);
            _cts.Dispose();
        }
    }
}

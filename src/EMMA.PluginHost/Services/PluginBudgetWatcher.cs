using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Services;

/// <summary>
/// Checks plugin resource usage against configured budgets and quarantines plugins that exceed them.
/// </summary>
/// <param name="registry">The plugin registry.</param>
/// <param name="processManager">The plugin process manager.</param>
/// <param name="options">The plugin host options.</param>
/// <param name="metrics">The plugin host metrics collector.</param>
/// <param name="logger">The logger used for budget diagnostics.</param>
public sealed class PluginBudgetWatcher(
    PluginRegistry registry,
    PluginProcessManager processManager,
    IOptions<PluginHostOptions> options,
    PluginHostMetrics metrics,
    ILogger<PluginBudgetWatcher> logger) : BackgroundService
{
    private readonly PluginRegistry _registry = registry;
    private readonly PluginProcessManager _processManager = processManager;
    private readonly PluginHostOptions _options = options.Value;
    private readonly PluginHostMetrics _metrics = metrics;
    private readonly ILogger<PluginBudgetWatcher> _logger = logger;

    /// <summary>
    /// Runs the periodic budget enforcement loop.
    /// </summary>
    /// <param name="stoppingToken">The cancellation token that stops the background service.</param>
    /// <returns>A task that completes when the service stops.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.BudgetWatchIntervalSeconds <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.BudgetWatchIntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var snapshot = _registry.GetSnapshot();
            foreach (var record in snapshot)
            {
                if (record.Runtime.State == PluginRuntimeState.Quarantined
                    || record.Runtime.State == PluginRuntimeState.Disabled)
                {
                    continue;
                }

                _metrics.RecordBudgetCheck(record.Manifest.Id);

                var exceededReasons = new List<string>();
                if (_options.MaxCpuBudgetMs > 0 && record.Status.CpuBudgetMs > _options.MaxCpuBudgetMs)
                {
                    exceededReasons.Add($"cpu={record.Status.CpuBudgetMs}ms>{_options.MaxCpuBudgetMs}ms");
                }

                if (_options.MaxMemoryMb > 0 && record.Status.MemoryMb > _options.MaxMemoryMb)
                {
                    exceededReasons.Add($"memory={record.Status.MemoryMb}mb>{_options.MaxMemoryMb}mb");
                }

                if (exceededReasons.Count == 0)
                {
                    continue;
                }

                var reasonText = string.Join(", ", exceededReasons);
                _metrics.RecordBudgetExceeded(record.Manifest.Id, reasonText);
                _logger.LogWarning(
                    "Plugin {PluginId} exceeded host budgets ({Reason}). Quarantining.",
                    record.Manifest.Id,
                    reasonText);

                await _processManager.StopAsync(record.Manifest.Id, stoppingToken);
                var runtime = record.Runtime.Quarantined("budget-exceeded", reasonText);
                _registry.UpdateRuntime(record.Manifest, runtime);
            }
        }
    }
}

using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Services;

/// <summary>
/// No-op budget watcher scaffold.
/// TODO: Replace with real resource enforcement and telemetry.
/// </summary>
public sealed class PluginBudgetWatcher(
    PluginRegistry registry,
    IOptions<PluginHostOptions> options,
    ILogger<PluginBudgetWatcher> logger) : BackgroundService
{
    private readonly PluginRegistry _registry = registry;
    private readonly PluginHostOptions _options = options.Value;
    private readonly ILogger<PluginBudgetWatcher> _logger = logger;

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
                if (_options.MaxCpuBudgetMs > 0 && record.Status.CpuBudgetMs > _options.MaxCpuBudgetMs)
                {
                    _logger.LogWarning(
                        "Plugin {PluginId} CPU budget {CpuBudgetMs} exceeds host max {MaxCpuBudgetMs}.",
                        record.Manifest.Id,
                        record.Status.CpuBudgetMs,
                        _options.MaxCpuBudgetMs);
                }

                if (_options.MaxMemoryMb > 0 && record.Status.MemoryMb > _options.MaxMemoryMb)
                {
                    _logger.LogWarning(
                        "Plugin {PluginId} memory budget {MemoryMb} exceeds host max {MaxMemoryMb}.",
                        record.Manifest.Id,
                        record.Status.MemoryMb,
                        _options.MaxMemoryMb);
                }
            }
        }
    }
}

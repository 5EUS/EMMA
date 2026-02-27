using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Services;

/// <summary>
/// Periodically stops managed plugin processes that have been idle past the configured timeout.
/// </summary>
public sealed class PluginIdleCleanupHostedService(
    PluginProcessManager processManager,
    IOptions<PluginHostOptions> options,
    ILogger<PluginIdleCleanupHostedService> logger) : BackgroundService
{
    private readonly PluginProcessManager _processManager = processManager;
    private readonly PluginHostOptions _options = options.Value;
    private readonly ILogger<PluginIdleCleanupHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sweepSeconds = Math.Max(1, _options.PluginIdleSweepSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(sweepSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var stopped = await _processManager.StopIdleProcessesAsync(stoppingToken);
                if (stopped.Count > 0 && _logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Stopped idle plugin processes: {PluginIds}",
                        string.Join(",", stopped));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(ex, "Idle plugin cleanup sweep failed.");
                }
            }
        }
    }
}

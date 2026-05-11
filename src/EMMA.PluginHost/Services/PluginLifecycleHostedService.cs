using EMMA.PluginHost.Plugins;

namespace EMMA.PluginHost.Services;

/// <summary>
/// Stops managed plugin processes on host shutdown.
/// TODO: Deprecate once a dedicated supervisor service is implemented.
/// </summary>
public sealed class PluginLifecycleHostedService(PluginProcessManager processManager) : IHostedService
{
    private readonly PluginProcessManager _processManager = processManager;

    /// <summary>
    /// Performs no work on startup.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops all managed plugin processes during host shutdown.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when all managed processes have stopped.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _processManager.StopAllAsync(cancellationToken);
    }
}

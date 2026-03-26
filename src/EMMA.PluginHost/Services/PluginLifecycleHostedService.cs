using EMMA.PluginHost.Plugins;

namespace EMMA.PluginHost.Services;

/// <summary>
/// Stops managed plugin processes on host shutdown.
/// TODO: Deprecate once a dedicated supervisor service is implemented.
/// </summary>
public sealed class PluginLifecycleHostedService(PluginProcessManager processManager) : IHostedService
{
    private readonly PluginProcessManager _processManager = processManager;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _processManager.StopAllAsync(cancellationToken);
    }
}

namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Runs plugin handshake on host startup.
/// </summary>
public sealed class PluginHandshakeHostedService(
    PluginHandshakeService handshakeService,
    ILogger<PluginHandshakeHostedService> logger) : IHostedService
{
    private readonly PluginHandshakeService _handshakeService = handshakeService;
    private readonly ILogger<PluginHandshakeHostedService> _logger = logger;
    private CancellationTokenSource? _handshakeCts;
    private Task? _handshakeTask;

    /// <summary>
    /// Starts the background plugin handshake task.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A completed task once startup has been scheduled.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _handshakeTask = Task.Run(async () =>
        {
            try
            {
                await _handshakeService.HandshakeAllAsync(_handshakeCts.Token);
            }
            catch (OperationCanceledException) when (_handshakeCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(ex, "Background startup handshake failed.");
                }
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the background plugin handshake task.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the handshake task has stopped.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _handshakeCts?.Cancel();

        if (_handshakeTask is not null)
        {
            try
            {
                await _handshakeTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _handshakeCts?.Dispose();
        _handshakeCts = null;
        _handshakeTask = null;
    }
}

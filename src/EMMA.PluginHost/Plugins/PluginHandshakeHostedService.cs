namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Runs plugin handshake on host startup.
/// </summary>
public sealed class PluginHandshakeHostedService(PluginHandshakeService handshakeService) : IHostedService
{
    private readonly PluginHandshakeService _handshakeService = handshakeService;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _handshakeService.HandshakeAllAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

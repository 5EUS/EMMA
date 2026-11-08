using EMMA.Domain;

namespace EMMA.PluginHost.Instances;

/// <summary>
/// Starts plugins of one kind of carrier. <c>PluginLoader</c> has already verified the signature and the
/// entrypoint's hash before it calls this, so a transport only runs what it is given; it enforces the
/// manifest's limits and permissions as far as its carrier can, and says so honestly when it cannot.
/// </summary>
public interface IPluginTransport
{
    /// <summary>Gets the transport's name as the manifest spells it, such as <c>wasm</c>.</summary>
    string Name { get; }

    /// <summary>Starts a plugin.</summary>
    /// <param name="launch">What to start and how.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The running instance, or why it could not be started.</returns>
    Task<Outcome<IPluginInstance>> OpenAsync(PluginLaunch launch, CancellationToken cancellationToken);
}

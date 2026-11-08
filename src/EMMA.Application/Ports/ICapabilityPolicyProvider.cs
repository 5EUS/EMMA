using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>Supplies the capabilities granted to each plugin by its signed manifest.</summary>
public interface ICapabilityPolicyProvider
{
    /// <summary>Gets a plugin's policy.</summary>
    /// <param name="plugin">The plugin.</param>
    /// <returns>The granted capabilities; <see cref="CapabilityPolicy.DenyAll"/> for an unknown plugin.</returns>
    CapabilityPolicy GetPolicy(PluginId plugin);
}

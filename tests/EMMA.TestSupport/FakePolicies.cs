using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.TestSupport;

/// <summary>An <see cref="ICapabilityPolicyProvider"/> backed by a dictionary; unknown plugins are denied everything.</summary>
public sealed class FakePolicies : ICapabilityPolicyProvider
{
    private readonly Dictionary<PluginId, CapabilityPolicy> policies = [];

    /// <summary>Grants a plugin access to hosts.</summary>
    /// <param name="plugin">The plugin.</param>
    /// <param name="hosts">Exact hosts or wildcards.</param>
    /// <returns>This instance, for chaining.</returns>
    public FakePolicies Allow(PluginId plugin, params string[] hosts)
    {
        this.policies[plugin] = new CapabilityPolicy(hosts, cacheAllowed: true);
        return this;
    }

    /// <inheritdoc/>
    public CapabilityPolicy GetPolicy(PluginId plugin) =>
        this.policies.GetValueOrDefault(plugin) ?? CapabilityPolicy.DenyAll;
}

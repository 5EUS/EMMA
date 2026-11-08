using EMMA.PluginConformance;
using EMMA.PluginHost.Instances;

namespace EMMA.PluginHost.Tests;

/// <summary>The in-memory reference instance passes the contract suite: it is the yardstick the real transports are held to.</summary>
public sealed class InMemoryInstanceConformanceTests : PluginInstanceConformance
{
    /// <inheritdoc/>
    protected override Task<IPluginInstance> CreateAsync(bool searchAssist) =>
        Task.FromResult<IPluginInstance>(new InMemoryPluginInstance(searchAssist));
}

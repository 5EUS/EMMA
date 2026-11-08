using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Runtime;

/// <summary>Drops plugin log lines.</summary>
internal sealed class DiscardingPluginLog : IPluginLogPort
{
    /// <inheritdoc/>
    public void Write(PluginId plugin, IReadOnlyList<PluginLogEntry> entries)
    {
    }
}

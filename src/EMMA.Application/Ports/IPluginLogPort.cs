using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>Receives what plugins log, so the host can show it next to its own logs with the plugin id attached.</summary>
public interface IPluginLogPort
{
    /// <summary>Records the lines one plugin call logged.</summary>
    /// <param name="plugin">The plugin that logged them.</param>
    /// <param name="entries">The lines, in order.</param>
    void Write(PluginId plugin, IReadOnlyList<PluginLogEntry> entries);
}

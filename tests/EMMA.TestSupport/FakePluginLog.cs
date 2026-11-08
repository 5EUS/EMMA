using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.TestSupport;

/// <summary>An <see cref="IPluginLogPort"/> that remembers what it was given.</summary>
public sealed class FakePluginLog : IPluginLogPort
{
    private readonly List<(PluginId Plugin, PluginLogEntry Entry)> written = [];

    /// <summary>Gets everything written so far, in order.</summary>
    public IReadOnlyList<(PluginId Plugin, PluginLogEntry Entry)> Written => this.written;

    /// <inheritdoc/>
    public void Write(PluginId plugin, IReadOnlyList<PluginLogEntry> entries)
    {
        this.written.AddRange(entries.Select(entry => (plugin, entry)));
    }
}

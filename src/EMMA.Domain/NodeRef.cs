using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace EMMA.Domain;

/// <summary>
/// The only node identity the host and its clients use: the plugin that owns the
/// node plus that plugin's local id. Plugins never see or set the plugin part, so
/// two plugins cannot collide.
/// </summary>
/// <param name="Plugin">The owning plugin.</param>
/// <param name="Id">The plugin-local node id.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NodeRef(PluginId Plugin, LocalNodeId Id)
{
    /// <summary>Parses the canonical <c>{pluginId}:{localId}</c> text form.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="reference">The reference when parsing succeeds.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is a valid reference.</returns>
    public static bool TryParse([NotNullWhen(true)] string? text, out NodeRef reference)
    {
        if (text is not null)
        {
            int split = text.IndexOf(':', StringComparison.Ordinal);
            if (split > 0
                && PluginId.TryCreate(text[..split], out PluginId plugin)
                && LocalNodeId.TryCreate(text[(split + 1)..], out LocalNodeId id))
            {
                reference = new NodeRef(plugin, id);
                return true;
            }
        }

        reference = default;
        return false;
    }

    /// <summary>Parses the canonical text form or throws.</summary>
    /// <param name="text">The text to parse.</param>
    /// <returns>The reference.</returns>
    /// <exception cref="FormatException">The text is not a valid node reference.</exception>
    public static NodeRef Parse(string text) =>
        TryParse(text, out NodeRef reference)
            ? reference
            : throw new FormatException($"'{text}' is not a valid node reference.");

    /// <summary>Formats the canonical <c>{pluginId}:{localId}</c> text form.</summary>
    /// <returns>The canonical text.</returns>
    public override string ToString() => $"{this.Plugin}:{this.Id}";
}

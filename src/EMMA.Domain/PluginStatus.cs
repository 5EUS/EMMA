namespace EMMA.Domain;

/// <summary>Whether an installed plugin may run.</summary>
public enum PluginStatus
{
    /// <summary>The plugin may run.</summary>
    Enabled = 0,

    /// <summary>The user switched the plugin off.</summary>
    Disabled = 1,

    /// <summary>The host blocked the plugin, for example after a signature or integrity failure.</summary>
    Quarantined = 2,
}

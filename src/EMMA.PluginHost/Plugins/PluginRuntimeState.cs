namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Current runtime state for a plugin process.
/// </summary>
public enum PluginRuntimeState
{
    /// <summary>
    /// The plugin runtime state is not yet known.
    /// </summary>
    Unknown,

    /// <summary>
    /// The plugin is handled by an external runtime rather than a managed process.
    /// </summary>
    External,

    /// <summary>
    /// The plugin is in the process of starting.
    /// </summary>
    Starting,

    /// <summary>
    /// The plugin runtime is active and ready.
    /// </summary>
    Running,

    /// <summary>
    /// The plugin has been stopped.
    /// </summary>
    Stopped,

    /// <summary>
    /// The plugin crashed or exited unexpectedly.
    /// </summary>
    Crashed,

    /// <summary>
    /// The plugin exceeded an operation timeout.
    /// </summary>
    Timeout,

    /// <summary>
    /// The plugin has been disabled by policy or validation.
    /// </summary>
    Disabled,

    /// <summary>
    /// The plugin has been quarantined and will not be started automatically.
    /// </summary>
    Quarantined
}

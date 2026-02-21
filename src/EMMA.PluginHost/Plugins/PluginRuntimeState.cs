namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Current runtime state for a plugin process.
/// </summary>
public enum PluginRuntimeState
{
    Unknown,
    External,
    Starting,
    Running,
    Stopped,
    Crashed,
    Timeout,
    Disabled,
    Quarantined
}

namespace EMMA.Domain;

/// <summary>How serious a line a plugin logged is.</summary>
public enum PluginLogLevel
{
    /// <summary>Very detailed tracing.</summary>
    Trace,

    /// <summary>Debugging detail.</summary>
    Debug,

    /// <summary>Normal operation.</summary>
    Info,

    /// <summary>Something unexpected that the plugin coped with.</summary>
    Warn,

    /// <summary>A failure.</summary>
    Error,
}

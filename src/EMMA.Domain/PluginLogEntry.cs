namespace EMMA.Domain;

/// <summary>One line a plugin logged during a call.</summary>
/// <param name="Level">The level.</param>
/// <param name="Message">The message, truncated by the host if very long.</param>
public sealed record PluginLogEntry(PluginLogLevel Level, string Message);

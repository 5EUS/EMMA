using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Api.Composition;

/// <summary>Sends what plugins log to the host's logger. Plugin text is untrusted, so control characters are removed before it can forge a log line.</summary>
/// <param name="logger">The host's logger.</param>
internal sealed class LoggingPluginLog(ILogger<LoggingPluginLog> logger) : IPluginLogPort
{
    /// <inheritdoc/>
    public void Write(PluginId plugin, IReadOnlyList<PluginLogEntry> entries)
    {
        foreach (PluginLogEntry entry in entries)
        {
            LogLevel level = entry.Level switch
            {
                PluginLogLevel.Trace => LogLevel.Trace,
                PluginLogLevel.Debug => LogLevel.Debug,
                PluginLogLevel.Info => LogLevel.Information,
                PluginLogLevel.Warn => LogLevel.Warning,
                _ => LogLevel.Error,
            };
            if (logger.IsEnabled(level))
            {
                logger.Log(level, "Plugin {Plugin}: {Message}", plugin.Value, Clean(entry.Message));
            }
        }
    }

    private static string Clean(string text) => string.Create(text.Length, text, static (span, source) =>
    {
        for (int index = 0; index < source.Length; index++)
        {
            span[index] = char.IsControl(source[index]) ? ' ' : source[index];
        }
    });
}

namespace EMMA.Plugin.Common;

/// <summary>
/// Resolves plugin runtime behavior from environment variables and command-line arguments.
/// </summary>
public static class PluginEnvironment
{
    private const string DevModeEnvVar = "EMMA_PLUGIN_DEV_MODE";

    /// <summary>
    /// Determines whether plugin development mode is enabled from the environment.
    /// </summary>
    /// <returns><see langword="true"/> when development mode is enabled; otherwise, <see langword="false"/>.</returns>
    public static bool IsDevelopmentMode()
    {
        return PluginEnvironmentFlags.IsEnabled(Environment.GetEnvironmentVariable(DevModeEnvVar));
    }

    /// <summary>
    /// Resolves the port for the plugin host from environment variables or CLI arguments.
    /// </summary>
    /// <param name="args">The CLI arguments to inspect for a <c>--port</c> override in development mode.</param>
    /// <param name="defaultPort">The fallback port to use when no override is supplied.</param>
    /// <returns>The resolved port value.</returns>
    public static int GetPort(string[] args, int defaultPort)
    {
        var envPort = Environment.GetEnvironmentVariable("EMMA_PLUGIN_PORT");
        if (!string.IsNullOrWhiteSpace(envPort) && int.TryParse(envPort, out var parsedEnv))
        {
            return parsedEnv;
        }

        if (!IsDevelopmentMode())
        {
            return defaultPort;
        }

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var parsedArg))
            {
                return parsedArg;
            }
        }

        return defaultPort;
    }

    /// <summary>
    /// Determines whether payload diagnostic logging is enabled for WASM operations.
    /// </summary>
    /// <returns><see langword="true"/> when payload diagnostics should be emitted; otherwise, <see langword="false"/>.</returns>
    public static bool ShouldLogPayloadDiagnostics()
    {
        return PluginEnvironmentFlags.IsEnabled(Environment.GetEnvironmentVariable("EMMA_WASM_PAYLOAD_DIAGNOSTICS"));
    }
}

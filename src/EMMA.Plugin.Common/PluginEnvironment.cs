namespace EMMA.Plugin.Common;

public static class PluginEnvironment
{
    private const string DevModeEnvVar = "EMMA_PLUGIN_DEV_MODE";

    public static bool IsDevelopmentMode()
    {
        return PluginEnvironmentFlags.IsEnabled(Environment.GetEnvironmentVariable(DevModeEnvVar));
    }

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

    public static bool ShouldLogPayloadDiagnostics()
    {
        return PluginEnvironmentFlags.IsEnabled(Environment.GetEnvironmentVariable("EMMA_WASM_PAYLOAD_DIAGNOSTICS"));
    }
}

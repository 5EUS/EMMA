namespace EMMA.PluginTemplate.Infrastructure;

public static class PluginEnvironment
{
    private const string DevModeEnvVar = "EMMA_PLUGIN_DEV_MODE";

    public static bool IsDevelopmentMode()
    {
        var value = Environment.GetEnvironmentVariable(DevModeEnvVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (bool.TryParse(value, out var parsedBool))
        {
            return parsedBool;
        }

        return value.Trim() switch
        {
            "1" or "yes" or "on" => true,
            _ => false
        };
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
}

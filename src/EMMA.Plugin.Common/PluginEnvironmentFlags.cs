namespace EMMA.Plugin.Common;

/// <summary>
/// Parses loosely formatted environment flag values.
/// </summary>
public static class PluginEnvironmentFlags
{
    /// <summary>
    /// Parses a loose environment flag value using common truthy string representations.
    /// </summary>
    /// <param name="value">The environment variable value to evaluate.</param>
    /// <returns><see langword="true"/> when the value represents an enabled flag; otherwise, <see langword="false"/>.</returns>
    public static bool IsEnabled(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (bool.TryParse(value, out var parsedBool))
        {
            return parsedBool;
        }

        return value.Trim() is "1" or "yes" or "on";
    }
}
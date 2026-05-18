namespace EMMA.Plugin.Common;

/// <summary>
/// Provides helpers for parsing CLI operation names and arguments.
/// </summary>
public static class PluginCliOperations
{
    /// <summary>
    /// Finds the first recognized operation in the CLI argument list and returns the remaining arguments for that operation.
    /// </summary>
    /// <param name="args">The raw CLI arguments to inspect.</param>
    /// <param name="knownOperations">The operation names that can be matched from the argument list.</param>
    /// <param name="maxProbe">The maximum number of leading arguments to probe for an operation name.</param>
    /// <returns>A tuple containing the normalized operation name and the remaining operation-specific arguments.</returns>
    public static (string Operation, string[] Args) NormalizeOperationArgs(
        string[] args,
        IReadOnlySet<string> knownOperations,
        int maxProbe = 5)
    {
        if (args.Length == 0)
        {
            return (string.Empty, Array.Empty<string>());
        }

        var probe = Math.Min(args.Length, Math.Max(1, maxProbe));
        for (var index = 0; index < probe; index++)
        {
            var candidate = args[index].ToLowerInvariant();
            if (knownOperations.Contains(candidate))
            {
                return (candidate, args.Length > index + 1 ? args[(index + 1)..] : Array.Empty<string>());
            }
        }

        var fallback = args[0].ToLowerInvariant();
        return (fallback, args.Length > 1 ? args[1..] : []);
    }
}
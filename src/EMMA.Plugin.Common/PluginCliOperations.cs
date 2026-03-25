namespace EMMA.Plugin.Common;

public static class PluginCliOperations
{
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
        return (fallback, args.Length > 1 ? args[1..] : Array.Empty<string>());
    }
}
using System.Text.RegularExpressions;

using EMMA.Domain;

namespace EMMA.PluginHost.Signing;

/// <summary>
/// What a delegated key may sign: an exact plugin id, or <c>prefix.*</c> meaning every id
/// under that prefix. Matching is by whole segments, so <c>com.acme.*</c> covers
/// <c>com.acme.manga</c> but not <c>com.acmevil.manga</c> (a raw string-prefix match would).
/// </summary>
public static partial class DelegationScope
{
    /// <summary>Checks that a scope is an exact plugin id or a <c>prefix.*</c> wildcard.</summary>
    /// <param name="scope">The scope text.</param>
    /// <returns><see langword="true"/> when valid.</returns>
    public static bool IsValid(string scope) =>
        PluginId.TryCreate(scope, out _) || Wildcard().IsMatch(scope);

    /// <summary>Decides whether a scope authorizes a plugin id.</summary>
    /// <param name="scope">The scope text.</param>
    /// <param name="plugin">The plugin id.</param>
    /// <returns><see langword="true"/> when the plugin is within the scope.</returns>
    public static bool Covers(string scope, PluginId plugin)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return scope.EndsWith(".*", StringComparison.Ordinal)
            ? plugin.Value.StartsWith(scope[..^1], StringComparison.Ordinal)
            : string.Equals(scope, plugin.Value, StringComparison.Ordinal);
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z][a-z0-9-]*)*\\.\\*$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 250)]
    private static partial Regex Wildcard();
}

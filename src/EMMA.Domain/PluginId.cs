using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace EMMA.Domain;

/// <summary>
/// Stable identifier of a plugin: lower-case reverse-DNS style, for example
/// <c>com.example.manga</c>. Never contains a colon, so <see cref="NodeRef"/> text
/// can always be split on the first colon.
/// </summary>
public readonly partial record struct PluginId
{
    private PluginId(string value) => this.Value = value;

    /// <summary>Gets the identifier text. Empty only for <see langword="default"/>.</summary>
    public string Value { get; } = string.Empty;

    /// <summary>Creates an identifier or throws when the text is not valid.</summary>
    /// <param name="value">The identifier text.</param>
    /// <returns>The validated identifier.</returns>
    /// <exception cref="ArgumentException">The text is not a valid plugin id.</exception>
    public static PluginId Create(string value) =>
        TryCreate(value, out PluginId id)
            ? id
            : throw new ArgumentException($"'{value}' is not a valid plugin id.", nameof(value));

    /// <summary>Validates and creates an identifier without throwing.</summary>
    /// <param name="value">The candidate text.</param>
    /// <param name="id">The identifier when valid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is valid.</returns>
    public static bool TryCreate([NotNullWhen(true)] string? value, out PluginId id)
    {
        if (value?.Length <= ContractLimits.MaxPluginIdLength
            && Pattern().IsMatch(value))
        {
            id = new PluginId(value);
            return true;
        }

        id = default;
        return false;
    }

    /// <inheritdoc/>
    public override string ToString() => this.Value;

    [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z][a-z0-9-]*)+$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 250)]
    private static partial Regex Pattern();
}

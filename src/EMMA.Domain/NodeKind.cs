using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace EMMA.Domain;

/// <summary>
/// Identifies a node kind. Lower-case, dot-separated by convention (<c>manga</c>, <c>manga.chapter</c>). An open vocabulary: the core never switches on it.
/// </summary>
public readonly partial record struct NodeKind
{
    private NodeKind(string value) => this.Value = value;

    /// <summary>Gets the text. Empty only for <see langword="default"/>.</summary>
    public string Value { get; } = string.Empty;

    /// <summary>Creates a value or throws when the text is not valid.</summary>
    /// <param name="value">The text.</param>
    /// <returns>The validated value.</returns>
    /// <exception cref="ArgumentException">The text is not a valid token.</exception>
    public static NodeKind Create(string value) =>
        TryCreate(value, out NodeKind result)
            ? result
            : throw new ArgumentException($"'{value}' is not a valid a node kind.", nameof(value));

    /// <summary>Validates and creates a value without throwing.</summary>
    /// <param name="value">The candidate text.</param>
    /// <param name="result">The value when valid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is valid.</returns>
    public static bool TryCreate([NotNullWhen(true)] string? value, out NodeKind result)
    {
        if (value?.Length <= 128 && Pattern().IsMatch(value))
        {
            result = new NodeKind(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <inheritdoc/>
    public override string ToString() => this.Value;

    [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z][a-z0-9-]*)*$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 250)]
    private static partial Regex Pattern();
}

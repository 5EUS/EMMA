using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace EMMA.Domain;

/// <summary>
/// Identifies an asset role. Names what an asset is for: <c>image</c>, <c>stream</c>, <c>subtitle</c>, <c>audio</c>, <c>text</c>, or a plugin-defined role.
/// </summary>
public readonly partial record struct AssetRole
{
    private AssetRole(string value) => this.Value = value;

    /// <summary>Gets the text. Empty only for <see langword="default"/>.</summary>
    public string Value { get; } = string.Empty;

    /// <summary>Creates a value or throws when the text is not valid.</summary>
    /// <param name="value">The text.</param>
    /// <returns>The validated value.</returns>
    /// <exception cref="ArgumentException">The text is not a valid token.</exception>
    public static AssetRole Create(string value) =>
        TryCreate(value, out AssetRole result)
            ? result
            : throw new ArgumentException($"'{value}' is not a valid an asset role.", nameof(value));

    /// <summary>Validates and creates a value without throwing.</summary>
    /// <param name="value">The candidate text.</param>
    /// <param name="result">The value when valid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is valid.</returns>
    public static bool TryCreate([NotNullWhen(true)] string? value, out AssetRole result)
    {
        if (value?.Length <= 128 && Pattern().IsMatch(value))
        {
            result = new AssetRole(value);
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

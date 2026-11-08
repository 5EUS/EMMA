using System.Diagnostics.CodeAnalysis;

namespace EMMA.Domain;

/// <summary>Identifies the person whose library and progress are being recorded.</summary>
public readonly record struct UserId
{
    private UserId(string value) => this.Value = value;

    /// <summary>Gets the identifier text. Empty only for <see langword="default"/>.</summary>
    public string Value { get; } = string.Empty;

    /// <summary>Creates an identifier or throws when the text is blank or too long.</summary>
    /// <param name="value">The identifier text.</param>
    /// <returns>The validated identifier.</returns>
    /// <exception cref="ArgumentException">The text is blank or longer than 128 characters.</exception>
    public static UserId Create(string value) =>
        TryCreate(value, out UserId id)
            ? id
            : throw new ArgumentException("A user id must be non-blank and at most 128 characters.", nameof(value));

    /// <summary>Validates and creates an identifier without throwing.</summary>
    /// <param name="value">The candidate text.</param>
    /// <param name="id">The identifier when valid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is valid.</returns>
    public static bool TryCreate([NotNullWhen(true)] string? value, out UserId id)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Length <= 128)
        {
            id = new UserId(value.Trim());
            return true;
        }

        id = default;
        return false;
    }

    /// <inheritdoc/>
    public override string ToString() => this.Value;
}

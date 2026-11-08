using System.Diagnostics.CodeAnalysis;

namespace EMMA.Domain;

/// <summary>
/// The name of one of a user's libraries. Names are compared without regard to case, so "Work" and "work" are the
/// same library; the casing a library was created with is the one shown.
/// </summary>
public readonly record struct LibraryName
{
    /// <summary>The longest name, in characters.</summary>
    public const int MaxLength = 64;

    private LibraryName(string value) => this.Value = value;

    /// <summary>Gets the library every user has, created the first time it is needed. It cannot be deleted.</summary>
    public static LibraryName Default { get; } = new("Default");

    /// <summary>Gets the name text. Empty only for <see langword="default"/>.</summary>
    public string Value { get; } = string.Empty;

    /// <summary>Creates a name or throws when the text is not valid.</summary>
    /// <param name="value">The name text.</param>
    /// <returns>The validated name.</returns>
    /// <exception cref="ArgumentException">The text is blank, too long or holds a control character.</exception>
    public static LibraryName Create(string value) =>
        TryCreate(value, out LibraryName name)
            ? name
            : throw new ArgumentException($"A library name must be 1 to {MaxLength} characters without control characters.", nameof(value));

    /// <summary>Validates and creates a name without throwing. Surrounding white space is trimmed.</summary>
    /// <param name="value">The candidate text.</param>
    /// <param name="name">The name when valid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is valid.</returns>
    public static bool TryCreate([NotNullWhen(true)] string? value, out LibraryName name)
    {
        string? trimmed = value?.Trim();
        if (!string.IsNullOrEmpty(trimmed) && trimmed.Length <= MaxLength && !trimmed.Any(char.IsControl))
        {
            name = new LibraryName(trimmed);
            return true;
        }

        name = default;
        return false;
    }

    /// <summary>Tells whether two names are the same library, ignoring case.</summary>
    /// <param name="other">The other name.</param>
    /// <returns><see langword="true"/> when they name the same library.</returns>
    public bool Matches(LibraryName other) => string.Equals(this.Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override string ToString() => this.Value;
}

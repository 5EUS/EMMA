namespace EMMA.Domain;

/// <summary>
/// Strongly typed media identifier.
/// </summary>
public readonly record struct MediaId(string Value)
{
    /// <summary>
    /// Creates a validated identifier from the supplied value.
    /// </summary>
    public static MediaId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("MediaId cannot be empty.", nameof(value));
        }

        return new MediaId(value.Trim());
    }

    public override string ToString() => Value;
}

namespace EMMA.Domain;

/// <summary>
/// A typed value carried by a <see cref="Prop"/>. There is deliberately no JSON or
/// object escape hatch: extensible data stays typed end to end.
/// </summary>
public sealed record PropValue
{
    private static readonly IReadOnlyList<string> NoItems = [];

    private PropValue(PropValueKind kind, string text, long wholeNumber, double number, IReadOnlyList<string> list)
    {
        this.Kind = kind;
        this.Text = text;
        this.WholeNumber = wholeNumber;
        this.Number = number;
        this.TextList = list;
    }

    /// <summary>Gets which payload is meaningful.</summary>
    public PropValueKind Kind { get; }

    /// <summary>Gets the text for <see cref="PropValueKind.Text"/> and <see cref="PropValueKind.Url"/>.</summary>
    public string Text { get; }

    /// <summary>Gets the value for <see cref="PropValueKind.WholeNumber"/> and <see cref="PropValueKind.Timestamp"/>.</summary>
    public long WholeNumber { get; }

    /// <summary>Gets the value for <see cref="PropValueKind.Number"/>.</summary>
    public double Number { get; }

    /// <summary>Gets a value indicating whether a <see cref="PropValueKind.Flag"/> value is set.</summary>
    public bool Flag => this.WholeNumber != 0;

    /// <summary>Gets the items for <see cref="PropValueKind.TextList"/>.</summary>
    public IReadOnlyList<string> TextList { get; }

    /// <summary>Creates a text value.</summary>
    /// <param name="value">The text.</param>
    /// <returns>The value.</returns>
    public static PropValue FromText(string value)
    {
        return new PropValue(PropValueKind.Text, value, 0, 0, NoItems);
    }

    /// <summary>Creates a whole-number value.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The value.</returns>
    public static PropValue FromWholeNumber(long value) => new(PropValueKind.WholeNumber, string.Empty, value, 0, NoItems);

    /// <summary>Creates a number value.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The value.</returns>
    public static PropValue FromNumber(double value) => new(PropValueKind.Number, string.Empty, 0, value, NoItems);

    /// <summary>Creates a boolean value.</summary>
    /// <param name="value">The flag.</param>
    /// <returns>The value.</returns>
    public static PropValue FromFlag(bool value) => new(PropValueKind.Flag, string.Empty, value ? 1 : 0, 0, NoItems);

    /// <summary>Creates a timestamp value.</summary>
    /// <param name="unixMilliseconds">Milliseconds since the Unix epoch, UTC.</param>
    /// <returns>The value.</returns>
    public static PropValue FromTimestamp(long unixMilliseconds) =>
        new(PropValueKind.Timestamp, string.Empty, unixMilliseconds, 0, NoItems);

    /// <summary>Creates a URL value.</summary>
    /// <param name="value">The absolute URL.</param>
    /// <returns>The value.</returns>
    public static PropValue FromUrl(Uri value)
    {
        return new PropValue(PropValueKind.Url, value.AbsoluteUri, 0, 0, NoItems);
    }

    /// <summary>Creates a list-of-text value.</summary>
    /// <param name="values">The items; copied.</param>
    /// <returns>The value.</returns>
    public static PropValue FromTextList(IEnumerable<string> values)
    {
        return new PropValue(PropValueKind.TextList, string.Empty, 0, 0, [.. values]);
    }

    /// <inheritdoc/>
    public bool Equals(PropValue? other) =>
        other is not null
        && this.Kind == other.Kind
        && string.Equals(this.Text, other.Text, StringComparison.Ordinal)
        && this.WholeNumber == other.WholeNumber
        && this.Number.Equals(other.Number)
        && this.TextList.SequenceEqual(other.TextList, StringComparer.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(this.Kind, this.Text, this.WholeNumber, this.Number, this.TextList.Count);
}

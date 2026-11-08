namespace EMMA.Domain;

/// <summary>The closed set of value types a <see cref="Prop"/> can carry.</summary>
public enum PropValueKind
{
    /// <summary>Free text.</summary>
    Text,

    /// <summary>A signed 64-bit whole number (the contract's <c>integer</c>).</summary>
    WholeNumber,

    /// <summary>A double-precision number.</summary>
    Number,

    /// <summary>A boolean.</summary>
    Flag,

    /// <summary>Milliseconds since the Unix epoch, UTC.</summary>
    Timestamp,

    /// <summary>An absolute URL.</summary>
    Url,

    /// <summary>A list of text values.</summary>
    TextList,
}

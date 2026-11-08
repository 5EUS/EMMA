namespace EMMA.Domain;

/// <summary>
/// How a setting is rendered, validated and read. Values always reach a plugin as text:
/// <c>boolean</c> is <c>true</c> or <c>false</c>, <c>number</c> is decimal text with a dot, <c>duration</c> is
/// whole milliseconds, <c>choice</c> is the chosen value and <c>multi-choice</c> is zero or more chosen values.
/// </summary>
public enum SettingType
{
    /// <summary>Free text.</summary>
    Text,

    /// <summary>Text stored encrypted, never logged, exported or shown back to the user.</summary>
    Secret,

    /// <summary>A switch.</summary>
    Boolean,

    /// <summary>A number.</summary>
    Number,

    /// <summary>A length of time in whole milliseconds.</summary>
    Duration,

    /// <summary>Exactly one of the choices.</summary>
    Choice,

    /// <summary>Any subset of the choices.</summary>
    MultiChoice,
}

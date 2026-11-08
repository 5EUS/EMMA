namespace EMMA.Domain;

/// <summary>How a client renders and sends a filter.</summary>
public enum FilterType
{
    /// <summary>A boolean switch.</summary>
    Toggle,

    /// <summary>Exactly one of the choices.</summary>
    SingleChoice,

    /// <summary>Any subset of the choices.</summary>
    MultiChoice,

    /// <summary>Two values: minimum then maximum.</summary>
    NumberRange,

    /// <summary>Free text.</summary>
    Text,

    /// <summary>Free text with server-side completion.</summary>
    Lookup,
}

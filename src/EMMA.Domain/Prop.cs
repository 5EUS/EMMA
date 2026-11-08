namespace EMMA.Domain;

/// <summary>
/// A typed key/value pair. Keys use dotted lower-case names; keys starting with
/// <c>emma.</c> are reserved for the well-known vocabulary.
/// </summary>
/// <param name="Key">The property key.</param>
/// <param name="Value">The typed value.</param>
public sealed record Prop(string Key, PropValue Value);

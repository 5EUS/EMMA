namespace EMMA.Api.Contracts;

/// <summary>Shows a setting only while another has a value.</summary>
/// <param name="Key">The other setting's key.</param>
/// <param name="Value">The value that makes the setting visible.</param>
public sealed record ConditionDto(
    string Key,
    string Value);

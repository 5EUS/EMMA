namespace EMMA.Domain;

/// <summary>One change to a user's remembered choices.</summary>
/// <param name="Key">The choice's key within its scope.</param>
/// <param name="Value">The new value, or <see langword="null"/> to forget the choice.</param>
public sealed record AppSettingChange(string Key, string? Value);

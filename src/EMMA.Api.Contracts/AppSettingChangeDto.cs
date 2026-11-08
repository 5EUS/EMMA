namespace EMMA.Api.Contracts;

/// <summary>A change to one remembered choice.</summary>
/// <param name="Key">The key: 1 to 64 lower-case letters, digits, <c>.</c>, <c>_</c> or <c>-</c>.</param>
/// <param name="Value">The new text, at most 4096 characters. Absent or <see langword="null"/> forgets the choice.</param>
public sealed record AppSettingChangeDto(
    string? Key,
    string? Value);

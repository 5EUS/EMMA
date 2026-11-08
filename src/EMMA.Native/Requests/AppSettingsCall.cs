using EMMA.Api.Contracts;

namespace EMMA.Native.Requests;

/// <summary>Reads or changes what the app remembers under a scope.</summary>
/// <param name="Scope">The scope.</param>
/// <param name="Changes">The changes, for a set; see <c>SetAppSettingsRequest</c>. Absent when reading.</param>
internal sealed record AppSettingsCall(
    string? Scope,
    IReadOnlyList<AppSettingChangeDto>? Changes);

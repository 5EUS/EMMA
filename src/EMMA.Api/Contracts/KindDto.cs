namespace EMMA.Api.Contracts;

/// <summary>A kind of node the plugin produces.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Label">A human label.</param>
/// <param name="Children">Kinds allowed beneath this one.</param>
/// <param name="Roles">Asset roles the kind can resolve.</param>
/// <param name="Profile">A client profile name, if any.</param>
internal sealed record KindDto(
    string Kind,
    string Label,
    IReadOnlyList<string> Children,
    IReadOnlyList<string> Roles,
    string? Profile);

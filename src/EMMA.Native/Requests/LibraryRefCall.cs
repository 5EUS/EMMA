namespace EMMA.Native.Requests;

/// <summary>Names a node and a library.</summary>
/// <param name="Ref">The node reference.</param>
/// <param name="Library">The library name; for an add it may be left out for the default library.</param>
internal sealed record LibraryRefCall(
    string? Ref,
    string? Library);

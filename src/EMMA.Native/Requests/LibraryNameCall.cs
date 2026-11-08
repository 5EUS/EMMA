namespace EMMA.Native.Requests;

/// <summary>Names a library.</summary>
/// <param name="Name">The library name.</param>
internal sealed record LibraryNameCall(
    string? Name);

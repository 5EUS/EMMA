namespace EMMA.Native.Requests;

/// <summary>Lists saved nodes.</summary>
/// <param name="Library">One library, or absent for every library.</param>
internal sealed record LibraryListCall(
    string? Library);

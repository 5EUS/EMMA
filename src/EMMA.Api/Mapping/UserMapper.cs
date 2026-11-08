using EMMA.Api.Contracts;
using EMMA.Domain;

namespace EMMA.Api.Mapping;

/// <summary>Maps a user's own data (progress, library) to its wire form.</summary>
internal static class UserMapper
{
    /// <summary>Maps a progress record.</summary>
    /// <param name="progress">The record.</param>
    /// <returns>The wire form.</returns>
    public static ProgressDto ToDto(Progress progress) => new(
        progress.Node.ToString(),
        progress.Locator.Unit,
        progress.Locator.Value,
        progress.Locator.Total,
        progress.Completed,
        progress.UpdatedAt);

    /// <summary>Maps a library entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The wire form.</returns>
    public static LibraryEntryDto ToDto(LibraryEntry entry) => new(entry.Node.ToString(), entry.AddedAt);
}

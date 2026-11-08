using EMMA.Api.Contracts;
using EMMA.Domain;

namespace EMMA.Api.Mapping;

/// <summary>Maps a user's own data (progress, library) to its wire form.</summary>
public static class UserMapper
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
    public static LibraryEntryDto ToDto(LibraryEntry entry) => new(entry.Node.ToString(), entry.Library.Value, entry.AddedAt);

    /// <summary>Maps a library.</summary>
    /// <param name="library">The library.</param>
    /// <returns>The wire form.</returns>
    public static LibrarySummaryDto ToDto(LibrarySummary library) =>
        new(library.Name.Value, library.CreatedAt, library.Count, library.Name.Matches(LibraryName.Default));

    /// <summary>Maps progress grouped by series.</summary>
    /// <param name="group">The group.</param>
    /// <returns>The wire form.</returns>
    public static ProgressGroupDto ToDto(ProgressGroup group) =>
        new(group.Series.ToString(), ToDto(group.Latest), group.Completed, group.InProgress);

    /// <summary>Maps what a user did beneath one container.</summary>
    /// <param name="series">The progress.</param>
    /// <returns>The wire form.</returns>
    public static SeriesProgressDto ToDto(SeriesProgress series) => new(
        series.Container.ToString(),
        series.Completed,
        series.InProgress,
        series.LatestActivity,
        [.. series.Items.Select(ToDto)]);

    /// <summary>Reads the changes a client asked for.</summary>
    /// <param name="changes">The wire form, or <see langword="null"/> when the request had none.</param>
    /// <returns>The changes, or why the request is malformed. Whether a key or value is acceptable is checked later.</returns>
    public static Outcome<IReadOnlyList<AppSettingChange>> ToChanges(IReadOnlyList<AppSettingChangeDto>? changes)
    {
        if (changes is null || changes.Any(change => change.Key is null))
        {
            return Outcome.Failure<IReadOnlyList<AppSettingChange>>(ErrorCode.InvalidArgument, "'changes' is required and every change needs a 'key'.");
        }

        return Outcome.Success<IReadOnlyList<AppSettingChange>>([.. changes.Select(change => new AppSettingChange(change.Key!, change.Value))]);
    }
}

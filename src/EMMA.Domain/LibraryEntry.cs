namespace EMMA.Domain;

/// <summary>
/// User library entry for a media item.
/// </summary>
public sealed record LibraryEntry(
    string EntryId,
    MediaId MediaId,
    string UserId,
    DateTimeOffset AddedAtUtc,
    string SourceId);

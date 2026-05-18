namespace EMMA.PluginHost.Library;

/// <summary>
/// Represents a failed media refresh attempt.
/// </summary>
/// <param name="MediaId">The media identifier that failed.</param>
/// <param name="SourceId">The optional source identifier associated with the media.</param>
/// <param name="Reason">The failure reason.</param>
public sealed record LibraryMediaRefreshFailure(
    string MediaId,
    string? SourceId,
    string Reason);

/// <summary>
/// Represents newly discovered media updates during a library refresh.
/// </summary>
/// <param name="MediaId">The media identifier.</param>
/// <param name="SourceId">The source identifier.</param>
/// <param name="Title">The media title.</param>
/// <param name="MediaType">The media type.</param>
/// <param name="NewItemsCount">The number of newly discovered items.</param>
public sealed record LibraryMediaDiscoveredUpdate(
    string MediaId,
    string SourceId,
    string Title,
    string MediaType,
    int NewItemsCount);

/// <summary>
/// Represents the result of refreshing media in a library.
/// </summary>
/// <param name="LibraryName">The library name.</param>
/// <param name="TotalItems">The total items considered for refresh.</param>
/// <param name="RefreshedItems">The number of items refreshed.</param>
/// <param name="RefreshedPagedItems">The number of paged items refreshed.</param>
/// <param name="RefreshedChapters">The number of chapters refreshed.</param>
/// <param name="SkippedItems">The number of items skipped.</param>
/// <param name="FailedItems">The number of items that failed.</param>
/// <param name="Failures">The collection of refresh failures.</param>
/// <param name="Updates">The collection of discovered updates.</param>
public sealed record LibraryMediaRefreshResponse(
    string LibraryName,
    int TotalItems,
    int RefreshedItems,
    int RefreshedPagedItems,
    int RefreshedChapters,
    int SkippedItems,
    int FailedItems,
    IReadOnlyList<LibraryMediaRefreshFailure> Failures,
    IReadOnlyList<LibraryMediaDiscoveredUpdate> Updates);

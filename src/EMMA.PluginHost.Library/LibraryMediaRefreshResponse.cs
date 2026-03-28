namespace EMMA.PluginHost.Library;

public sealed record LibraryMediaRefreshFailure(
    string MediaId,
    string? SourceId,
    string Reason);

public sealed record LibraryMediaDiscoveredUpdate(
    string MediaId,
    string SourceId,
    string Title,
    string MediaType,
    int NewItemsCount);

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

namespace EMMA.Api.Contracts;

/// <summary>Everything the caller did beneath one container.</summary>
/// <param name="Container">The container reference.</param>
/// <param name="Completed">How many nodes beneath it were finished.</param>
/// <param name="InProgress">How many were started and not finished.</param>
/// <param name="LatestActivity">When progress was last recorded there; absent when never.</param>
/// <param name="Items">Every node with progress, most recent first (at most 10,000).</param>
public sealed record SeriesProgressDto(
    string Container,
    int Completed,
    int InProgress,
    DateTimeOffset? LatestActivity,
    IReadOnlyList<ProgressDto> Items);

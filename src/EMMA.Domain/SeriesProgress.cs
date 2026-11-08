namespace EMMA.Domain;

/// <summary>Everything a user did beneath one container, for a detail view.</summary>
/// <param name="Container">The series, show or album.</param>
/// <param name="Completed">How many nodes beneath it the user finished.</param>
/// <param name="InProgress">How many the user started and did not finish.</param>
/// <param name="LatestActivity">When the user last recorded progress there, or <see langword="null"/> when never.</param>
/// <param name="Items">Every node with progress, most recent first.</param>
public sealed record SeriesProgress(
    NodeRef Container,
    int Completed,
    int InProgress,
    DateTimeOffset? LatestActivity,
    IReadOnlyList<Progress> Items);

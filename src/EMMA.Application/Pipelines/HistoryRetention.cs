namespace EMMA.Application.Pipelines;

/// <summary>How long a user's history is kept.</summary>
/// <param name="Days">The number of days, or 0 for ever.</param>
/// <param name="Removed">How many entries applying the setting just deleted.</param>
public sealed record HistoryRetention(int Days, int Removed);

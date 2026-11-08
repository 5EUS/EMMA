namespace EMMA.Api.Contracts;

/// <summary>What the caller did within one series, show or album.</summary>
/// <param name="Series">The series reference.</param>
/// <param name="Latest">The most recently updated progress beneath it.</param>
/// <param name="Completed">How many nodes beneath it were finished.</param>
/// <param name="InProgress">How many were started and not finished.</param>
public sealed record ProgressGroupDto(
    string Series,
    ProgressDto Latest,
    int Completed,
    int InProgress);

namespace EMMA.Domain;

/// <summary>
/// What a user did within one series, show or album: the most recent progress and how many of its nodes are
/// finished or under way. A series is the topmost node the host has cached above the progress; a node whose
/// parents are unknown is its own group.
/// </summary>
/// <param name="Series">The series, show or album.</param>
/// <param name="Latest">The most recently updated progress beneath it.</param>
/// <param name="Completed">How many nodes beneath it the user finished.</param>
/// <param name="InProgress">How many the user started and did not finish.</param>
public sealed record ProgressGroup(NodeRef Series, Progress Latest, int Completed, int InProgress);

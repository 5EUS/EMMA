namespace EMMA.Domain;

/// <summary>
/// Explains how a compression request should be fulfilled.
/// </summary>
public sealed record CompressionPlan(
    CompressionRequest Request,
    IReadOnlyList<CompressionPlanEntry> Outputs,
    bool RequiresBackgroundGeneration = false,
    bool AllowSourceFallback = true,
    string? Reason = null);
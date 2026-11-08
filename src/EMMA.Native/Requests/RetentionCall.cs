namespace EMMA.Native.Requests;

/// <summary>Sets how long history is kept.</summary>
/// <param name="Days">The number of days, 0 to 3650; 0 keeps history for ever.</param>
internal sealed record RetentionCall(
    int? Days);

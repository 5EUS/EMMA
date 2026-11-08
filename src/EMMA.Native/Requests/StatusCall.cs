namespace EMMA.Native.Requests;

/// <summary>Changes a plugin's status.</summary>
/// <param name="Plugin">The plugin id.</param>
/// <param name="Status"><c>enabled</c>, <c>disabled</c> or <c>quarantined</c>.</param>
internal sealed record StatusCall(
    string? Plugin,
    string? Status);

namespace EMMA.Native.Requests;

/// <summary>Names a plugin.</summary>
/// <param name="Plugin">The plugin id.</param>
internal sealed record PluginCall(
    string? Plugin);

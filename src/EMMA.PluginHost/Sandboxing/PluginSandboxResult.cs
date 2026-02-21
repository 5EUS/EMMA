namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// Result of preparing a plugin sandbox.
/// </summary>
public sealed record PluginSandboxResult(
    string RootPath,
    bool Requested,
    bool Enforced);

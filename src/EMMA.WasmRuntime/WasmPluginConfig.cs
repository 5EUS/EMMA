namespace EMMA.WasmRuntime;

/// <summary>Everything needed to open one plugin. The caller has already verified the component file.</summary>
/// <param name="ComponentPath">The component file.</param>
/// <param name="Limits">The resource ceilings.</param>
/// <param name="Network">Exact host names or <c>*.example.com</c> wildcards the plugin may fetch.</param>
/// <param name="Cache">Whether the plugin may use the host cache.</param>
/// <param name="Settings">User settings by key, as text values, with secrets already decrypted.</param>
/// <param name="Precompiled">Whether the file is a component precompiled by <c>WasmRuntimeHost.Precompile</c> on this host.</param>
/// <param name="Testing">Test-only switches.</param>
public sealed record WasmPluginConfig(
    string ComponentPath,
    WasmLimits Limits,
    IReadOnlyList<string> Network,
    bool Cache,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Settings,
    bool Precompiled = false,
    WasmTestingOptions? Testing = null);

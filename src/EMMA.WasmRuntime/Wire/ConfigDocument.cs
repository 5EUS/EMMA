namespace EMMA.WasmRuntime.Wire;

/// <summary>The JSON <c>PluginConfig</c> of native/emma-wasm/src/config.rs.</summary>
/// <param name="ComponentPath">The component file.</param>
/// <param name="Precompiled">Whether the file is a host-precompiled component.</param>
/// <param name="Limits">Resource ceilings.</param>
/// <param name="Permissions">What the plugin may ask for.</param>
/// <param name="Settings">User settings by key.</param>
/// <param name="Testing">Test-only switches.</param>
internal sealed record ConfigDocument(
    string ComponentPath,
    bool Precompiled,
    ConfigLimits Limits,
    ConfigPermissions Permissions,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Settings,
    ConfigTesting? Testing);

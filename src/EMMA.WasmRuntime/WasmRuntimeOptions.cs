namespace EMMA.WasmRuntime;

/// <summary>Options for creating a runtime.</summary>
/// <param name="Target">The compile target, such as <c>pulley64</c> for platforms without a JIT; <see langword="null"/> for the native target.</param>
public sealed record WasmRuntimeOptions(string? Target = null);

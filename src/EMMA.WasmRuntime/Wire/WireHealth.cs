namespace EMMA.WasmRuntime.Wire;

/// <summary>The plugin's health report.</summary>
/// <param name="Ok">Whether the plugin and its upstream are reachable.</param>
/// <param name="Detail">What to show the user when not.</param>
internal sealed record WireHealth(bool Ok, string? Detail);

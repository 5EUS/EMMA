namespace EMMA.WasmRuntime;

/// <summary>A plugin's answer to a liveness probe.</summary>
/// <param name="Ok">Whether the plugin and its upstream are reachable.</param>
/// <param name="Detail">What to show the user when <paramref name="Ok"/> is <see langword="false"/>.</param>
public sealed record PluginHealth(bool Ok, string? Detail);

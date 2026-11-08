namespace EMMA.WasmRuntime.Wire;

/// <summary>The permissions section of the host's plugin configuration.</summary>
/// <param name="Network">Allowed hosts.</param>
/// <param name="Cache">Whether the cache is available.</param>
internal sealed record ConfigPermissions(IReadOnlyList<string> Network, bool Cache);

namespace EMMA.WasmRuntime.Wire;

/// <summary>The permissions section of the host's plugin configuration.</summary>
/// <param name="Network">Allowed hosts.</param>
/// <param name="Cache">Whether the cache is available.</param>
/// <param name="AllowLocalNetwork">Whether the plugin may reach loopback and private addresses (a user's own instance).</param>
internal sealed record ConfigPermissions(IReadOnlyList<string> Network, bool Cache, bool AllowLocalNetwork);

namespace EMMA.WasmRuntime.Wire;

/// <summary>How an asset is delivered: exactly one member is set.</summary>
/// <param name="Inline">Bytes returned directly.</param>
/// <param name="Remote">A URL the host fetches.</param>
/// <param name="Proxied">A token only the plugin can read.</param>
internal sealed record WireDelivery(WireInline? Inline, WireRemote? Remote, WireProxied? Proxied);

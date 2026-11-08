namespace EMMA.WasmRuntime.Wire;

/// <summary>A resolved asset.</summary>
/// <param name="Mime">The payload type.</param>
/// <param name="Delivery">How the bytes are delivered.</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="Props">Typed facts about the asset.</param>
internal sealed record WireAsset(string Mime, WireDelivery Delivery, ulong? Size, IReadOnlyList<WireProp> Props);

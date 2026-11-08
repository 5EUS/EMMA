namespace EMMA.Domain;

/// <summary>Small payloads returned directly by the plugin.</summary>
/// <param name="Payload">The bytes.</param>
public sealed record InlineDelivery(ReadOnlyMemory<byte> Payload) : AssetDelivery;

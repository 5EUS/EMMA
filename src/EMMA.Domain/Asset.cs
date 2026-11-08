namespace EMMA.Domain;

/// <summary>One deliverable resource for a node and role.</summary>
/// <param name="Mime">The payload type; adaptive streams use their manifest type.</param>
/// <param name="Delivery">How the bytes are delivered.</param>
/// <param name="Size">The size in bytes, when known.</param>
/// <param name="Props">Typed facts: quality, language, duration, DRM, live.</param>
public sealed record Asset(string Mime, AssetDelivery Delivery, long? Size, IReadOnlyList<Prop> Props);

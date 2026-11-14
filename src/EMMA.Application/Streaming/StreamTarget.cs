using EMMA.Domain;

namespace EMMA.Application.Streaming;

/// <summary>What one address of a stream session leads to.</summary>
/// <param name="Kind">How the bytes are treated when they are served.</param>
/// <param name="Delivery">Where the bytes come from.</param>
/// <param name="Mime">The type to answer with when the upstream gives none.</param>
internal sealed record StreamTarget(StreamPartKind Kind, AssetDelivery Delivery, string Mime);

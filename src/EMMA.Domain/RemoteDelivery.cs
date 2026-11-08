namespace EMMA.Domain;

/// <summary>A resource the host fetches itself. The preferred delivery: bytes never cross the plugin boundary.</summary>
/// <param name="Url">The absolute URL.</param>
/// <param name="Headers">Request headers the fetch must carry (Referer, Cookie, Authorization).</param>
/// <param name="ExpiresAt">When the URL stops working, if known.</param>
public sealed record RemoteDelivery(Uri Url, IReadOnlyList<HttpHeader> Headers, DateTimeOffset? ExpiresAt) : AssetDelivery;

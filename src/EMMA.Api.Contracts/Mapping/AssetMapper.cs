using EMMA.Api.Contracts;
using EMMA.Domain;

namespace EMMA.Api.Mapping;

/// <summary>
/// Maps resolved assets to their wire form. A listing says how an asset is delivered but never where it lives: a remote
/// URL may carry cookies or referer rules and a proxied token is private to the plugin, so clients ask for an asset by
/// index and the host fetches it.
/// </summary>
public static class AssetMapper
{
    /// <summary>Maps the assets of a node for a role.</summary>
    /// <param name="assets">The resolved assets.</param>
    /// <returns>The wire form, with each asset's index.</returns>
    public static AssetListResponse ToResponse(IReadOnlyList<Asset> assets) => new(
        [.. assets.Select((asset, index) => new AssetDto(
            index,
            asset.Mime,
            asset.Size,
            DeliveryName(asset.Delivery),
            PropMapper.ToDtos(asset.Props)))]);

    private static string DeliveryName(AssetDelivery delivery) => delivery switch
    {
        InlineDelivery => "inline",
        RemoteDelivery => "remote",
        _ => "proxied",
    };
}

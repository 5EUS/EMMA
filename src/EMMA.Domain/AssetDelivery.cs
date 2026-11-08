namespace EMMA.Domain;

/// <summary>
/// How the bytes of an <see cref="Asset"/> are delivered. A closed hierarchy:
/// <see cref="InlineDelivery"/>, <see cref="RemoteDelivery"/>, <see cref="ProxiedDelivery"/>.
/// </summary>
public abstract record AssetDelivery
{
    /// <summary>Initializes a new instance of the <see cref="AssetDelivery"/> class. Only this assembly can derive.</summary>
    private protected AssetDelivery()
    {
    }
}

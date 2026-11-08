namespace EMMA.Domain;

/// <summary>Content only the plugin can read (signed requests, decryption), read through its token.</summary>
/// <param name="Token">The opaque token the plugin issued.</param>
public sealed record ProxiedDelivery(string Token) : AssetDelivery;

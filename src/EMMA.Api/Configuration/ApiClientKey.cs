namespace EMMA.Api.Configuration;

/// <summary>A configured API key: who it identifies and the SHA-256 of the key. The key itself is never stored.</summary>
/// <param name="ClientId">The client the key identifies; also the user id its progress and library are kept under.</param>
/// <param name="KeyHash">The 32-byte SHA-256 of the key.</param>
internal sealed record ApiClientKey(string ClientId, byte[] KeyHash);

namespace EMMA.Api.Security;

/// <summary>A newly generated API key. The key is shown once; only the hash is configured.</summary>
/// <param name="Key">The key, to give to the client.</param>
/// <param name="Hash">The hash text to put in the configuration.</param>
internal sealed record GeneratedApiKey(string Key, string Hash);

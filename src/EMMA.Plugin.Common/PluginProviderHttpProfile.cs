namespace EMMA.Plugin.Common;

/// <summary>
/// Describes the HTTP profile used to talk to an upstream provider.
/// </summary>
/// <param name="BaseUri">The provider base URI.</param>
/// <param name="UserAgent">The user agent string to send.</param>
/// <param name="AcceptMediaType">The default accepted media type.</param>
public sealed record PluginProviderHttpProfile(
    Uri BaseUri,
    string UserAgent,
    string AcceptMediaType = "application/json");

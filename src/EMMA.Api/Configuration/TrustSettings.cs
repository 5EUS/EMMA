using EMMA.PluginHost.Signing;

namespace EMMA.Api.Configuration;

/// <summary>Which plugin signatures the host accepts.</summary>
/// <param name="RequireSigned">Whether unsigned plugins are refused.</param>
/// <param name="Repositories">The repositories whose signing roots are trusted.</param>
internal sealed record TrustSettings(bool RequireSigned, IReadOnlyList<TrustedRepository> Repositories);

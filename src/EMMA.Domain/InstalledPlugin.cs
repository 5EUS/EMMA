namespace EMMA.Domain;

/// <summary>A plugin the host has installed.</summary>
/// <param name="Id">The plugin id.</param>
/// <param name="Version">The installed version.</param>
/// <param name="Status">Whether the plugin may run.</param>
/// <param name="ManifestJson">The verified manifest exactly as installed.</param>
/// <param name="Signer">Who vouched for it as <c>repositoryId/keyId</c>, or <see langword="null"/> for an unsigned development install.</param>
/// <param name="InstalledAt">When it was installed.</param>
/// <param name="UpdatedAt">When its record last changed.</param>
public sealed record InstalledPlugin(
    PluginId Id,
    string Version,
    PluginStatus Status,
    string ManifestJson,
    string? Signer,
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt);

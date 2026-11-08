using EMMA.Domain;

namespace EMMA.PluginHost.Manifest;

/// <summary>
/// Identity, trust and limits for one plugin: the part the host reads and verifies before it
/// executes any plugin code. What the plugin serves (kinds, filters, sorts, settings) comes
/// from <c>meta.describe</c> at run time, so it cannot drift from the code.
/// </summary>
/// <param name="Id">The stable plugin id; every node is namespaced by it.</param>
/// <param name="Name">The display name.</param>
/// <param name="Version">The semantic version.</param>
/// <param name="Description">A short description.</param>
/// <param name="Author">The author or publisher.</param>
/// <param name="Kinds">Display-only summary of the kinds served, checked against <c>describe</c> on load.</param>
/// <param name="Icon">How the plugin's icon is presented.</param>
/// <param name="Contract">The WIT interfaces the component implements.</param>
/// <param name="MinHostVersion">The oldest host that can run the plugin.</param>
/// <param name="Entrypoint">The component file and its digest.</param>
/// <param name="Permissions">What the plugin may ask the host for.</param>
/// <param name="Limits">Resource ceilings.</param>
/// <param name="Signature">The repository signature; absent only for unsigned development plugins.</param>
public sealed record PluginManifest(
    PluginId Id,
    string Name,
    string Version,
    string? Description,
    string? Author,
    IReadOnlyList<NodeKind> Kinds,
    ManifestIcon? Icon,
    ManifestContract Contract,
    string? MinHostVersion,
    ManifestEntrypoint Entrypoint,
    ManifestPermissions Permissions,
    ManifestLimits Limits,
    ManifestSignature? Signature);

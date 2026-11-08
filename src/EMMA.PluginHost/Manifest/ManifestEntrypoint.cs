namespace EMMA.PluginHost.Manifest;

/// <summary>The component file inside the package and its digest, so the signature covers the code itself.</summary>
/// <param name="Path">The relative path, validated: no traversal, ends in <c>.wasm</c> or <c>.cwasm</c>.</param>
/// <param name="Sha256">The lower-case hex SHA-256 of the file.</param>
public sealed record ManifestEntrypoint(string Path, string Sha256);

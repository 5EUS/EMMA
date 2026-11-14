using System.Security.Cryptography;

using EMMA.PluginHost.Signing;

namespace EMMA.PluginHost.Manifest;

/// <summary>
/// The digest a signature binds to. It is computed from the parsed fields in a fixed order
/// through length-prefixed framing, never from JSON text, so whitespace, key order and
/// formatting cannot change it, and a field that is not part of the digest cannot exist
/// (the parser rejects unknown properties). The signature block itself is excluded.
/// </summary>
/// <remarks>
/// Adding a manifest field means adding it here and bumping the domain tag below; a test
/// changes every field in turn and fails if one does not affect the digest.
/// </remarks>
public static class ManifestDigest
{
    private const string Domain = "EMMA-MANIFEST/1";

    /// <summary>Computes the SHA-256 digest of a manifest, ignoring its signature.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The 32-byte digest.</returns>
    public static byte[] Compute(PluginManifest manifest)
    {
        FrameWriter frame = new FrameWriter()
            .Text(Domain)
            .Integer(1)
            .Text(manifest.Id.Value)
            .Text(manifest.Name)
            .Text(manifest.Version)
            .OptionalText(manifest.Description)
            .OptionalText(manifest.Author)
            .TextList([.. manifest.Kinds.Select(kind => kind.Value)])
            .OptionalText(manifest.MinHostVersion)
            .Text("wasm");

        WriteIcon(frame, manifest.Icon);
        frame
            .TextList(manifest.Contract.Required)
            .TextList(manifest.Contract.Optional)
            .Text(manifest.Entrypoint.Path)
            .Text(manifest.Entrypoint.Sha256)
            .TextList(manifest.Permissions.Network)
            .Flag(manifest.Permissions.Cache)
            .Integer(manifest.Limits.CpuBudgetMs)
            .Integer(manifest.Limits.MemoryMb);

        // Appended only when present, so a manifest that does not use the field keeps the digest it was signed with. A
        // trailing frame cannot be mistaken for its absence.
        if (manifest.Permissions.NetworkSettings.Count > 0)
        {
            frame.TextList(manifest.Permissions.NetworkSettings);
        }

        return SHA256.HashData(frame.ToArray());
    }

    /// <summary>Computes the digest as lower-case hex, the form stored in a signature.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>64 lower-case hex characters.</returns>
    public static string ComputeHex(PluginManifest manifest) => Convert.ToHexStringLower(Compute(manifest));

    private static void WriteIcon(FrameWriter frame, ManifestIcon? icon)
    {
        frame.Flag(icon is not null);
        if (icon is null)
        {
            return;
        }

        frame
            .OptionalInteger(icon.AspectRatio is { } ratio ? BitConverter.DoubleToInt64Bits(ratio) : null)
            .OptionalText(icon.Fit)
            .OptionalInteger(icon.Width)
            .OptionalInteger(icon.Height);
    }
}

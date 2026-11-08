using System.IO.Compression;
using System.Security.Cryptography;

using EMMA.PluginHost.Manifest;

namespace EMMA.TestSupport;

/// <summary>Builds plugin packages (zip files) for installer tests, including hostile ones.</summary>
public static class TestPackages
{
    /// <summary>Gets some stand-in component bytes.</summary>
    /// <param name="content">Distinguishes different components.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Component(string content = "component") => System.Text.Encoding.UTF8.GetBytes(content);

    /// <summary>Builds a manifest whose entrypoint digest matches the given component bytes.</summary>
    /// <param name="component">The component the manifest describes.</param>
    /// <param name="id">The plugin id.</param>
    /// <param name="version">The version.</param>
    /// <param name="entrypointPath">The entrypoint path.</param>
    /// <param name="sign">Whether to sign it with the test PKI's delegated key.</param>
    /// <returns>The manifest.</returns>
    public static PluginManifest Manifest(
        byte[] component,
        string id = "com.example.test",
        string version = "1.0.0",
        string entrypointPath = "plugin.wasm",
        bool sign = true)
    {
        PluginManifest manifest = TestManifests.Valid(id, Convert.ToHexStringLower(SHA256.HashData(component))) with
        {
            Version = version,
            Entrypoint = new ManifestEntrypoint(entrypointPath, Convert.ToHexStringLower(SHA256.HashData(component))),
        };
        return sign ? TestPki.Sign(manifest) : manifest;
    }

    /// <summary>Packages a built fixture plugin (see <see cref="NativeArtifacts"/>) as an installable archive.</summary>
    /// <param name="fixture">The built component's file name without extension, such as <c>fixture</c>.</param>
    /// <param name="id">The plugin id to install it under.</param>
    /// <param name="sign">Whether to sign the manifest with the test signer.</param>
    /// <param name="adjust">Changes the manifest before it is signed.</param>
    /// <param name="signedAt">The moment to sign at, for hosts that read the real clock; defaults to the test PKI's fixed moment.</param>
    /// <returns>The archive bytes.</returns>
    public static async Task<byte[]> FixtureAsync(string fixture, string id, bool sign = true, Func<PluginManifest, PluginManifest>? adjust = null, DateTimeOffset? signedAt = null)
    {
        byte[] component = await File.ReadAllBytesAsync(NativeArtifacts.Plugin(fixture)).ConfigureAwait(false);
        PluginManifest manifest = Manifest(component, id, sign: false);
        manifest = manifest with { Kinds = [], Permissions = manifest.Permissions with { Network = [] } };
        manifest = adjust?.Invoke(manifest) ?? manifest;
        return Standard(sign ? Sign(manifest, signedAt) : manifest, component);
    }

    /// <summary>Builds a well-formed package: the manifest and the entrypoint.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="component">The component bytes.</param>
    /// <returns>The zip bytes.</returns>
    public static byte[] Standard(PluginManifest manifest, byte[] component) =>
        Zip(("plugin.json", ManifestWriter.Write(manifest)), (manifest.Entrypoint.Path, component));

    /// <summary>Builds a zip with exactly the given entries; names are used verbatim, so hostile or duplicate names are possible.</summary>
    /// <param name="entries">The entry names and contents.</param>
    /// <returns>The zip bytes.</returns>
    public static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using Stream target = entry.Open();
                target.Write(content);
            }
        }

        return stream.ToArray();
    }

    /// <summary>Builds a package whose entrypoint is stored as a Unix symbolic link entry.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The zip bytes.</returns>
    public static byte[] WithSymbolicLink(PluginManifest manifest)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry manifestEntry = archive.CreateEntry("plugin.json");
            using (Stream target = manifestEntry.Open())
            {
                target.Write(ManifestWriter.Write(manifest));
            }

            ZipArchiveEntry link = archive.CreateEntry(manifest.Entrypoint.Path);
            link.ExternalAttributes = (0xA000 | 0x1FF) << 16;
            using Stream linkTarget = link.Open();
            linkTarget.Write("/etc/passwd"u8);
        }

        return stream.ToArray();
    }

    private static PluginManifest Sign(PluginManifest manifest, DateTimeOffset? signedAt) =>
        signedAt is { } moment
            ? TestPki.Sign(manifest, issuedAt: moment.AddHours(-1), expiresAt: moment.AddDays(7))
            : TestPki.Sign(manifest);
}

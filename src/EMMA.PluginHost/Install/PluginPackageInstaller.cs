using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.PluginHost.Manifest;
using EMMA.PluginHost.Signing;

namespace EMMA.PluginHost.Install;

/// <summary>
/// Installs a plugin package (a zip) from an untrusted source. In order: the package is checked
/// structurally, its manifest is parsed strictly, its signature chain is verified (or an unsigned
/// plugin is accepted only when the trust policy allows development installs), the package may
/// hold nothing but the manifest and the one signed entrypoint, the entrypoint's hash must match
/// the manifest, a downgrade is refused, and the verified files appear in one atomic move.
/// A failure at any step leaves nothing installed.
/// </summary>
/// <param name="options">Where plugins live and the package limits.</param>
/// <param name="policy">Whether unsigned plugins are allowed.</param>
/// <param name="verifier">Checks signatures.</param>
/// <param name="registry">The record of installed plugins.</param>
/// <param name="time">The time source.</param>
public sealed class PluginPackageInstaller(
    PluginHostOptions options,
    TrustPolicy policy,
    PluginSignatureVerifier verifier,
    IPluginRegistryPort registry,
    TimeProvider time)
{
    private const string StagingDirectoryName = ".staging";

    /// <summary>Installs a package.</summary>
    /// <param name="package">The package; it must be seekable.</param>
    /// <param name="repositoryId">The repository the package came from, as known to the host (never the manifest's own claim).</param>
    /// <param name="cancellationToken">Cancels the install.</param>
    /// <returns>The installed plugin's record, or the reason it was refused.</returns>
    public async Task<Outcome<InstalledPlugin>> InstallAsync(Stream package, string repositoryId, CancellationToken cancellationToken)
    {
        if (!package.CanSeek || package.Length > options.MaxPackageBytes)
        {
            return Refuse(ErrorCode.InvalidArgument, "The package is missing, not seekable, or larger than allowed.");
        }

        ZipArchive archive;
        try
        {
            archive = await ZipArchive.CreateAsync(package, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return Refuse(ErrorCode.InvalidArgument, "The package is not a valid zip file.");
        }

        await using (archive.ConfigureAwait(false))
        {
            string? problem = PackageValidator.FindProblem(archive, options);
            return problem is not null
                ? Refuse(ErrorCode.InvalidArgument, $"The package was refused: {problem}.")
                : await this.InstallValidatedAsync(archive, repositoryId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Outcome<InstalledPlugin> Refuse(ErrorCode code, string message) =>
        Outcome.Failure<InstalledPlugin>(code, message);

    private static ZipArchiveEntry? FindFile(IReadOnlyList<ZipArchiveEntry> files, string name) =>
        files.FirstOrDefault(entry => string.Equals(entry.FullName, name, StringComparison.Ordinal));

    private static void DeleteQuietly(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: leftover staging files are ignored and overwritten, never loaded.
        }
    }

    private static void RemoveIfEmpty(string directory)
    {
        try
        {
            // Non-recursive: this only succeeds when nothing else (another version) lives there.
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not empty, or not ours to remove: leave it.
        }
    }

    private async Task<Outcome<InstalledPlugin>> InstallValidatedAsync(ZipArchive archive, string repositoryId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ZipArchiveEntry> files = PackageValidator.Files(archive);
        ZipArchiveEntry? manifestEntry = FindFile(files, PackageValidator.ManifestEntryName);
        if (manifestEntry is null)
        {
            return Refuse(ErrorCode.InvalidArgument, $"The package has no {PackageValidator.ManifestEntryName}.");
        }

        byte[]? manifestBytes = await PackageExtractor.ReadSmallAsync(manifestEntry, ManifestParser.MaxManifestBytes, cancellationToken).ConfigureAwait(false);
        if (manifestBytes is null)
        {
            return Refuse(ErrorCode.InvalidArgument, "The package manifest is larger than allowed.");
        }

        Outcome<PluginManifest> parsed = ManifestParser.Parse(manifestBytes, requireSignature: false);
        if (!parsed.IsSuccess)
        {
            return Outcome.Failure<InstalledPlugin>(parsed.Error);
        }

        PluginManifest manifest = parsed.Value;
        Outcome<string?> signer = await this.CheckSignatureAsync(manifest, repositoryId, cancellationToken).ConfigureAwait(false);
        if (!signer.IsSuccess)
        {
            return Outcome.Failure<InstalledPlugin>(signer.Error);
        }

        string? unexpected = files
            .Select(entry => entry.FullName)
            .FirstOrDefault(name => name is not PackageValidator.ManifestEntryName && !string.Equals(name, manifest.Entrypoint.Path, StringComparison.Ordinal));
        if (unexpected is not null)
        {
            return Refuse(ErrorCode.InvalidArgument, $"The package contains an unexpected file ('{unexpected}'). Only {PackageValidator.ManifestEntryName} and the entrypoint are allowed.");
        }

        ZipArchiveEntry? entrypoint = FindFile(files, manifest.Entrypoint.Path);
        if (entrypoint is null)
        {
            return Refuse(ErrorCode.InvalidArgument, $"The package does not contain its entrypoint '{manifest.Entrypoint.Path}'.");
        }

        Outcome<InstalledPlugin>? refusal = await this.CheckVersionAsync(manifest, cancellationToken).ConfigureAwait(false);
        return refusal ?? await this.StageAndCommitAsync(manifest, manifestBytes, entrypoint, signer.Value, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Outcome<string?>> CheckSignatureAsync(PluginManifest manifest, string repositoryId, CancellationToken cancellationToken)
    {
        if (manifest.Signature is null)
        {
            return policy.RequireSigned
                ? Outcome.Failure<string?>(ErrorCode.Unauthorized, "Unsigned plugins are not allowed by this host.")
                : Outcome.Success<string?>(null);
        }

        // A signature that is present must verify, even when unsigned plugins are allowed.
        Outcome<VerifiedSignature> verified = await verifier.VerifyAsync(manifest, repositoryId, cancellationToken).ConfigureAwait(false);
        return verified.IsSuccess
            ? Outcome.Success<string?>(verified.Value.Signer)
            : Outcome.Failure<string?>(verified.Error);
    }

    private async Task<Outcome<InstalledPlugin>?> CheckVersionAsync(PluginManifest manifest, CancellationToken cancellationToken)
    {
        InstalledPlugin? existing = await registry.GetAsync(manifest.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null && PluginVersion.Compare(manifest.Version, existing.Version) < 0)
        {
            return Refuse(ErrorCode.InvalidArgument, $"Version {manifest.Version} is older than the installed {existing.Version}; downgrades are refused.");
        }

        return Directory.Exists(options.PluginDirectory(manifest.Id, manifest.Version))
            ? Refuse(ErrorCode.InvalidArgument, $"Version {manifest.Version} of '{manifest.Id}' is already installed.")
            : null;
    }

    private async Task<Outcome<InstalledPlugin>> StageAndCommitAsync(
        PluginManifest manifest,
        byte[] manifestBytes,
        ZipArchiveEntry entrypoint,
        string? signer,
        CancellationToken cancellationToken)
    {
        string staging = Path.Combine(Path.GetFullPath(options.InstallRoot), StagingDirectoryName, Guid.NewGuid().ToString("N"));
        string target = Path.GetFullPath(options.PluginDirectory(manifest.Id, manifest.Version));
        bool committed = false;
        try
        {
            Directory.CreateDirectory(staging);
            await File.WriteAllBytesAsync(Path.Combine(staging, PackageValidator.ManifestEntryName), manifestBytes, cancellationToken).ConfigureAwait(false);

            Outcome<string> hash = await PackageExtractor.ExtractAsync(entrypoint, staging, manifest.Entrypoint.Path, options.MaxUnpackedBytes, cancellationToken).ConfigureAwait(false);
            if (!hash.IsSuccess)
            {
                return Outcome.Failure<InstalledPlugin>(hash.Error);
            }

            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash.Value), Encoding.ASCII.GetBytes(manifest.Entrypoint.Sha256)))
            {
                return Refuse(ErrorCode.Unauthorized, "The entrypoint does not match the digest in the manifest.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                Directory.Move(staging, target);
            }
            catch (IOException) when (Directory.Exists(target))
            {
                // Another install of the same version won the race. Leave its files alone.
                return Refuse(ErrorCode.InvalidArgument, $"Version {manifest.Version} of '{manifest.Id}' is already installed.");
            }

            committed = true;

            DateTimeOffset now = time.GetUtcNow();
            InstalledPlugin record = new(manifest.Id, manifest.Version, PluginStatus.Enabled, Encoding.UTF8.GetString(manifestBytes), signer, now, now);
            await registry.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
            return Outcome.Success(record);
        }
        catch
        {
            // The files were moved but the record was not stored: take them back out so nothing is half-installed.
            if (committed)
            {
                DeleteQuietly(target);
                RemoveIfEmpty(Path.GetDirectoryName(target)!);
            }

            throw;
        }
        finally
        {
            DeleteQuietly(staging);
        }
    }
}

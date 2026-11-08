using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using EMMA.Domain;
using EMMA.PluginHost.Manifest;
using EMMA.PluginHost.Signing;

namespace EMMA.Cli.Commands;

/// <summary>Turns a plugin directory (a <c>plugin.json</c> and the component it names) into an installable package.</summary>
internal static class PackCommand
{
    private const string ManifestFile = "plugin.json";

    /// <summary>Packs a plugin directory and writes the zip.</summary>
    /// <param name="context">The command.</param>
    /// <param name="directory">The plugin directory.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(CommandContext context, string directory)
    {
        Outcome<SigningRequest?> signing = await ReadSigningAsync(context).ConfigureAwait(false);
        if (!signing.IsSuccess)
        {
            return await context.MisuseAsync(signing.Error.Message).ConfigureAwait(false);
        }

        Outcome<PluginPackage> built = await BuildAsync(directory, signing.Value, TimeProvider.System, context.CancellationToken).ConfigureAwait(false);
        if (!built.IsSuccess)
        {
            return await context.FailAsync(built.Error).ConfigureAwait(false);
        }

        string fileName = $"{built.Value.Manifest.Id}-{built.Value.Manifest.Version}.zip";
        string target = context.Arguments.Option("out") ?? ".";
        string path = Directory.Exists(target) || target.EndsWith('/') || target.EndsWith('\\') ? Path.Combine(target, fileName) : target;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllBytesAsync(path, built.Value.Archive, context.CancellationToken).ConfigureAwait(false);
        PluginManifest packed = built.Value.Manifest;
        string signer = packed.Signature is { } signature ? "signed by " + signature.RepositoryId + "/" + signature.KeyId : "unsigned";
        await context.SayAsync($"Packed {path} ({signer}).").ConfigureAwait(false);
        return CommandContext.Success;
    }

    /// <summary>Builds a package from a plugin directory: fills in the component's digest, validates the manifest the way the installer will, optionally signs it, and zips it.</summary>
    /// <param name="directory">The directory holding <c>plugin.json</c> and the component.</param>
    /// <param name="signing">How to sign, or <see langword="null"/> for an unsigned development package.</param>
    /// <param name="time">The clock, for the signature's validity window.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The package, or why the directory cannot be packed.</returns>
    public static async Task<Outcome<PluginPackage>> BuildAsync(string directory, SigningRequest? signing, TimeProvider time, CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(directory, ManifestFile);
        if (!File.Exists(manifestPath))
        {
            return Fail($"{manifestPath} does not exist; run this in a plugin directory.");
        }

        JsonNode? document;
        try
        {
            document = JsonNode.Parse(await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException ex)
        {
            return Fail($"{manifestPath} is not valid JSON: {ex.Message}");
        }

        if (document is not JsonObject manifestObject || manifestObject["entrypoint"] is not JsonObject entrypoint || entrypoint["path"]?.GetValue<string>() is not { } entrypointPath)
        {
            return Fail($"{manifestPath} needs an 'entrypoint' with a 'path'.");
        }

        string componentPath = Path.GetFullPath(Path.Combine(directory, entrypointPath));
        string directoryPrefix = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        if (!componentPath.StartsWith(directoryPrefix, StringComparison.Ordinal) || !File.Exists(componentPath))
        {
            return Fail($"The component '{entrypointPath}' named in {ManifestFile} was not found inside the plugin directory. Build it first.");
        }

        byte[] component = await File.ReadAllBytesAsync(componentPath, cancellationToken).ConfigureAwait(false);
        entrypoint["sha256"] = Convert.ToHexStringLower(SHA256.HashData(component));
        manifestObject.Remove("signature");

        // Validate with the same parser the installer uses, so a manifest that would be refused is refused here, with the reason.
        Outcome<PluginManifest> parsed = ManifestParser.Parse(Encoding.UTF8.GetBytes(manifestObject.ToJsonString()), requireSignature: false);
        if (!parsed.IsSuccess)
        {
            return Fail($"{ManifestFile} is not valid: {parsed.Error.Message}");
        }

        PluginManifest manifest = parsed.Value;
        if (signing is not null)
        {
            DateTimeOffset now = time.GetUtcNow();
            manifest = PluginSigner.SignManifest(manifest, signing.Repository, signing.KeyId, signing.PrivateKeyPem, now.AddMinutes(-5), now.AddDays(signing.ValidDays));
        }

        using MemoryStream stream = new();
        ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true);
        await using (archive.ConfigureAwait(false))
        {
            await AddAsync(archive, ManifestFile, ManifestWriter.Write(manifest), cancellationToken).ConfigureAwait(false);
            await AddAsync(archive, manifest.Entrypoint.Path, component, cancellationToken).ConfigureAwait(false);
        }

        return Outcome.Success(new PluginPackage(manifest, stream.ToArray()));
    }

    private static async Task<Outcome<SigningRequest?>> ReadSigningAsync(CommandContext context)
    {
        string? keyFile = context.Arguments.Option("sign-key");
        if (keyFile is null)
        {
            return Outcome.Success<SigningRequest?>(null);
        }

        string? keyId = context.Arguments.Option("key-id");
        string? repository = context.Arguments.Option("repository");
        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(repository))
        {
            return Outcome.Failure<SigningRequest?>(ErrorCode.InvalidArgument, "--sign-key also needs --key-id and --repository.");
        }

        if (!int.TryParse(context.Arguments.Option("valid-days") ?? "30", NumberStyles.None, CultureInfo.InvariantCulture, out int days) || days < 1)
        {
            return Outcome.Failure<SigningRequest?>(ErrorCode.InvalidArgument, "--valid-days must be a whole number of at least 1.");
        }

        return File.Exists(keyFile)
            ? Outcome.Success<SigningRequest?>(new SigningRequest(repository, keyId, await File.ReadAllTextAsync(keyFile, context.CancellationToken).ConfigureAwait(false), days))
            : Outcome.Failure<SigningRequest?>(ErrorCode.InvalidArgument, $"The key file {keyFile} does not exist.");
    }

    private static async Task AddAsync(ZipArchive archive, string name, byte[] content, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        Stream stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Outcome<PluginPackage> Fail(string message) => Outcome.Failure<PluginPackage>(ErrorCode.InvalidArgument, message);
}

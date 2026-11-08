using System.Security.Cryptography;
using System.Text;

using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.PluginHost.Install;
using EMMA.PluginHost.Manifest;
using EMMA.PluginHost.Signing;
using EMMA.WasmRuntime;

namespace EMMA.PluginHost.Runtime;

/// <summary>
/// Opens an installed plugin. It trusts nothing it stored earlier: it re-parses the manifest, re-verifies
/// the signature, re-hashes the entrypoint on disk, and cross-checks the manifest against what the
/// component declares, before any plugin code runs.
/// </summary>
/// <param name="options">Where plugins are installed.</param>
/// <param name="policy">The trust policy.</param>
/// <param name="verifier">The signature verifier.</param>
/// <param name="registry">The installed plugins.</param>
/// <param name="settings">The user's settings.</param>
/// <param name="runtime">The native Wasm host.</param>
/// <param name="time">The clock.</param>
public sealed class PluginLoader(
    PluginHostOptions options,
    TrustPolicy policy,
    PluginSignatureVerifier verifier,
    IPluginRegistryPort registry,
    IPluginSettingsPort settings,
    WasmRuntimeHost runtime,
    TimeProvider time)
{
    /// <summary>Verifies and opens an installed plugin.</summary>
    /// <param name="id">The plugin.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The loaded plugin, or why it was refused.</returns>
    public async Task<Outcome<LoadedPlugin>> LoadAsync(PluginId id, CancellationToken cancellationToken)
    {
        InstalledPlugin? installed = await registry.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (installed is null)
        {
            return Refuse(ErrorCode.NotFound, $"Plugin '{id}' is not installed.");
        }

        if (installed.Status != PluginStatus.Enabled)
        {
            return Refuse(ErrorCode.Unavailable, $"Plugin '{id}' is {installed.Status}.");
        }

        Outcome<PluginManifest> manifest = await this.VerifiedManifestAsync(installed, cancellationToken).ConfigureAwait(false);
        if (!manifest.IsSuccess)
        {
            return Outcome.Failure<LoadedPlugin>(manifest.Error);
        }

        Outcome<string> path = await this.VerifiedEntrypointAsync(installed, manifest.Value, cancellationToken).ConfigureAwait(false);
        if (!path.IsSuccess)
        {
            return Outcome.Failure<LoadedPlugin>(path.Error);
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> values = await settings.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return await this.OpenAsync(manifest.Value, path.Value, values, cancellationToken).ConfigureAwait(false);
    }

    private static Outcome<LoadedPlugin> Refuse(ErrorCode code, string message) => Outcome.Failure<LoadedPlugin>(code, message);

    private async Task<Outcome<PluginManifest>> VerifiedManifestAsync(InstalledPlugin installed, CancellationToken cancellationToken)
    {
        Outcome<PluginManifest> parsed = ManifestParser.Parse(Encoding.UTF8.GetBytes(installed.ManifestJson), requireSignature: policy.RequireSigned);
        if (!parsed.IsSuccess)
        {
            return Outcome.Failure<PluginManifest>(ErrorCode.InvalidArgument, $"The stored manifest of '{installed.Id}' is invalid: {parsed.Error.Message}");
        }

        PluginManifest manifest = parsed.Value;
        if (manifest.Id != installed.Id || !string.Equals(manifest.Version, installed.Version, StringComparison.Ordinal))
        {
            return Outcome.Failure<PluginManifest>(ErrorCode.Unauthorized, $"The stored manifest does not match the installed record of '{installed.Id}'.");
        }

        if (manifest.Signature is null)
        {
            return policy.RequireSigned
                ? Outcome.Failure<PluginManifest>(ErrorCode.Unauthorized, "Unsigned plugins are not allowed by this host.")
                : Outcome.Success(manifest);
        }

        // The installer checked the signature's repository against the one the package came from, so
        // the repository named in the signature is the one to verify against again.
        Outcome<VerifiedSignature> verified = await verifier.VerifyAsync(manifest, manifest.Signature.RepositoryId, cancellationToken).ConfigureAwait(false);
        return verified.IsSuccess ? Outcome.Success(manifest) : Outcome.Failure<PluginManifest>(verified.Error);
    }

    private async Task<Outcome<string>> VerifiedEntrypointAsync(InstalledPlugin installed, PluginManifest manifest, CancellationToken cancellationToken)
    {
        string directory = Path.GetFullPath(options.PluginDirectory(installed.Id, installed.Version));
        string path = Path.GetFullPath(Path.Combine(directory, manifest.Entrypoint.Path));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return Outcome.Failure<string>(ErrorCode.InvalidArgument, "The entrypoint is outside the plugin directory.");
        }

        string actual;
        try
        {
            FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Outcome.Failure<string>(ErrorCode.Unavailable, $"The entrypoint of '{installed.Id}' cannot be read: {ex.Message}");
        }

        if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(manifest.Entrypoint.Sha256)))
        {
            return Outcome.Success(path);
        }

        // The file no longer matches what was signed: someone changed it after install. Block the plugin.
        await registry.SetStatusAsync(installed.Id, PluginStatus.Quarantined, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return Outcome.Failure<string>(ErrorCode.Unauthorized, $"The entrypoint of '{installed.Id}' does not match its signed digest; the plugin was quarantined.");
    }

    private async Task<Outcome<LoadedPlugin>> OpenAsync(
        PluginManifest manifest,
        string path,
        IReadOnlyDictionary<string, IReadOnlyList<string>> values,
        CancellationToken cancellationToken)
    {
        WasmPluginConfig config = new(
            path,
            new WasmLimits(manifest.Limits.CpuBudgetMs, manifest.Limits.MemoryMb),
            manifest.Permissions.Network,
            manifest.Permissions.Cache,
            values);
        Outcome<WasmPlugin> opened = runtime.Open(config);
        if (!opened.IsSuccess)
        {
            return Outcome.Failure<LoadedPlugin>(opened.Error);
        }

        WasmPlugin plugin = opened.Value;
        PluginCall<PluginInfo> described = await plugin.DescribeAsync(cancellationToken).ConfigureAwait(false);
        if (!described.Outcome.IsSuccess)
        {
            plugin.Dispose();
            return Outcome.Failure<LoadedPlugin>(described.Outcome.Error);
        }

        HashSet<NodeKind> declared = [.. described.Outcome.Value.Kinds.Select(kind => kind.Kind)];
        if (manifest.Kinds.Count > 0 && !declared.SetEquals(manifest.Kinds))
        {
            plugin.Dispose();
            return Refuse(ErrorCode.InvalidArgument, $"The kinds in the manifest of '{manifest.Id}' do not match what the plugin declares.");
        }

        return Outcome.Success(new LoadedPlugin(plugin, manifest, described.Outcome.Value));
    }
}

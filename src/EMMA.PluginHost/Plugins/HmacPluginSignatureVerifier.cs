using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using EMMA.PluginHost.Configuration;

namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Backward-compatible signature verifier wrapper that delegates to the RSA-based verifier implementation.
/// </summary>
public sealed class HmacPluginSignatureVerifier(
    IOptions<PluginSignatureOptions> options,
    IOptions<PluginHostOptions> hostOptions,
    ILogger<DelegatedPluginSignatureVerifier> logger) : IPluginSignatureVerifier
{
    private readonly DelegatedPluginSignatureVerifier _inner = new(options, hostOptions, logger);

    /// <summary>
    /// Creates a verifier using default host options and a null logger.
    /// </summary>
    /// <param name="options">The plugin signature options.</param>
    public HmacPluginSignatureVerifier(IOptions<PluginSignatureOptions> options)
        : this(
            options,
            Options.Create(new PluginHostOptions()),
            NullLogger<DelegatedPluginSignatureVerifier>.Instance)
    {
    }

    /// <summary>
    /// Verifies that the supplied plugin manifest contains a valid signature.
    /// </summary>
    /// <param name="manifest">The plugin manifest to verify.</param>
    /// <param name="reason">When verification fails, receives a descriptive failure reason.</param>
    /// <returns><see langword="true"/> when the manifest signature is valid; otherwise, <see langword="false"/>.</returns>
    public bool Verify(PluginManifest manifest, out string? reason)
    {
        return _inner.Verify(manifest, out reason);
    }
}

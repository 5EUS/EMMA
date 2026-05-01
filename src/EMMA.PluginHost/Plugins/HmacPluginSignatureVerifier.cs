using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using EMMA.PluginHost.Configuration;

namespace EMMA.PluginHost.Plugins;

public sealed class HmacPluginSignatureVerifier(
    IOptions<PluginSignatureOptions> options,
    IOptions<PluginHostOptions> hostOptions,
    ILogger<DelegatedPluginSignatureVerifier> logger) : IPluginSignatureVerifier
{
    private readonly DelegatedPluginSignatureVerifier _inner = new(options, hostOptions, logger);

    public HmacPluginSignatureVerifier(IOptions<PluginSignatureOptions> options)
        : this(
            options,
            Options.Create(new PluginHostOptions()),
            NullLogger<DelegatedPluginSignatureVerifier>.Instance)
    {
    }

    public bool Verify(PluginManifest manifest, out string? reason)
    {
        return _inner.Verify(manifest, out reason);
    }
}

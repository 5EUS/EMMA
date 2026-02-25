namespace EMMA.PluginHost.Plugins;

public sealed class PluginSignatureOptions
{
    public bool RequireSignedPlugins { get; init; } = false; // TODO default to true in a future major version
    public string? HmacKeyBase64 { get; init; }
}

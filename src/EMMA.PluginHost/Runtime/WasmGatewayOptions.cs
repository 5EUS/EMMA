namespace EMMA.PluginHost.Runtime;

/// <summary>Settings for <see cref="WasmPluginGateway"/>.</summary>
/// <param name="MaxConsecutiveTraps">How many calls in a row may crash or be stopped before the plugin is quarantined.</param>
public sealed record WasmGatewayOptions(int MaxConsecutiveTraps = 5);

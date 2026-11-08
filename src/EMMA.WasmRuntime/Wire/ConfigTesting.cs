namespace EMMA.WasmRuntime.Wire;

/// <summary>The test-only section of the host's plugin configuration.</summary>
/// <param name="AllowPrivateAddresses">Lets plugins reach loopback and private addresses.</param>
/// <param name="FixedNowMs">Freezes the plugin's clock.</param>
internal sealed record ConfigTesting(bool AllowPrivateAddresses, long? FixedNowMs);

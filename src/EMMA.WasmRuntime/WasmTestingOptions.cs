namespace EMMA.WasmRuntime;

/// <summary>Switches that exist only so tests can run hermetically. Production code never sets them.</summary>
/// <param name="AllowPrivateAddresses">Lets plugins reach loopback and private addresses (a local test server).</param>
/// <param name="FixedNowMs">Freezes the plugin's clock at this many milliseconds since the Unix epoch.</param>
public sealed record WasmTestingOptions(bool AllowPrivateAddresses = false, long? FixedNowMs = null);

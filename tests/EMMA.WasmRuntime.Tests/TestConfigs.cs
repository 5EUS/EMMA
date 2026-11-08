namespace EMMA.WasmRuntime.Tests;

/// <summary>Plugin configurations for the tests.</summary>
internal static class TestConfigs
{
    /// <summary>Builds a configuration for a built fixture plugin with generous limits and no permissions.</summary>
    /// <param name="plugin">The fixture name.</param>
    /// <param name="cpuBudgetMs">The CPU budget per call.</param>
    /// <param name="settings">User settings, if any.</param>
    /// <param name="testing">Test-only switches, if any.</param>
    /// <returns>The configuration.</returns>
    internal static WasmPluginConfig Fixture(
        string plugin = "fixture",
        int cpuBudgetMs = 5000,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? settings = null,
        WasmTestingOptions? testing = null) =>
        new(
            NativeArtifacts.Plugin(plugin),
            new WasmLimits(cpuBudgetMs, 128),
            [],
            Cache: false,
            settings ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            Testing: testing);
}

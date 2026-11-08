using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.WasmRuntime.Tests;

/// <summary>Tests for plugin loading through the native host.</summary>
/// <param name="runtime">The shared native runtime.</param>
public sealed class PluginLoadingTests(RuntimeFixture runtime) : IClassFixture<RuntimeFixture>
{
    /// <summary>Optional search assist is probed and used when present.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task OptionalSearchAssistIsProbedAndUsedWhenPresent()
    {
        using WasmPlugin assisted = runtime.Open(TestConfigs.Fixture("fixture-assist"));
        using WasmPlugin plain = runtime.Open(TestConfigs.Fixture());
        CancellationToken cancel = TestContext.Current.CancellationToken;

        PluginCall<IReadOnlyList<string>> suggestions = await assisted.SuggestAsync("ber", 2, cancel).ConfigureAwait(true);
        PluginCall<IReadOnlyList<PluginNode>> enriched = await assisted.EnrichAsync([LocalNodeId.Create("s1")], cancel).ConfigureAwait(true);
        PluginCall<IReadOnlyList<string>> unsupported = await plain.SuggestAsync("ber", 2, cancel).ConfigureAwait(true);

        Assert.True(assisted.SearchAssist);
        Assert.False(plain.SearchAssist);
        Assert.Equal(["ber 1", "ber 2"], suggestions.Outcome.Value);
        Assert.Equal("Enriched s1", enriched.Outcome.Value.Single().Title);
        Assert.Equal(ErrorCode.Unsupported, unsupported.Outcome.Error?.Code);
    }

    /// <summary>A compatible newer patch release of the contract loads.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ACompatibleNewerPatchReleaseOfTheContractLoads()
    {
        using WasmPlugin plugin = runtime.Open(TestConfigs.Fixture("fixture-newer-patch"));

        PluginCall<PluginNodePage> call = await plugin.SearchAsync(Text("series"), TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(call.Outcome.IsSuccess);
    }

    /// <summary>A component precompiled by the host opens and runs.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AComponentPrecompiledByTheHostOpensAndRuns()
    {
        string cwasm = Path.Combine(Path.GetTempPath(), $"emma-test-{Guid.NewGuid():N}.cwasm");
        try
        {
            byte[] source = await File.ReadAllBytesAsync(NativeArtifacts.Plugin("fixture"), TestContext.Current.CancellationToken).ConfigureAwait(true);
            Outcome<byte[]> compiled = runtime.Host.Precompile(source);
            Assert.True(compiled.IsSuccess, compiled.Error?.Message);
            await File.WriteAllBytesAsync(cwasm, compiled.Value, TestContext.Current.CancellationToken).ConfigureAwait(true);

            using WasmPlugin plugin = runtime.Open(TestConfigs.Fixture() with { ComponentPath = cwasm, Precompiled = true });
            PluginCall<PluginNodePage> call = await plugin.SearchAsync(Text("series"), TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.True(call.Outcome.IsSuccess);
            Assert.False(string.IsNullOrWhiteSpace(runtime.Host.Key()));
        }
        finally
        {
            File.Delete(cwasm);
        }
    }

    /// <summary>Garbage is refused at precompile and at open.</summary>
    [Fact]
    public void GarbageIsRefusedAtPrecompileAndAtOpen()
    {
        string garbage = Path.Combine(Path.GetTempPath(), $"emma-test-{Guid.NewGuid():N}.wasm");
        try
        {
            File.WriteAllBytes(garbage, [1, 2, 3, 4, 5]);

            Outcome<byte[]> compiled = runtime.Host.Precompile(new byte[] { 1, 2, 3 });
            Outcome<WasmPlugin> opened = runtime.Host.Open(TestConfigs.Fixture() with { ComponentPath = garbage });
            Outcome<WasmPlugin> missing = runtime.Host.Open(TestConfigs.Fixture() with { ComponentPath = garbage + ".missing" });

            Assert.False(compiled.IsSuccess);
            Assert.False(opened.IsSuccess);
            Assert.False(missing.IsSuccess);
        }
        finally
        {
            File.Delete(garbage);
        }
    }

    /// <summary>Limits outside the schema ranges are refused.</summary>
    [Fact]
    public void LimitsOutsideTheSchemaRangesAreRefused()
    {
        WasmPluginConfig config = TestConfigs.Fixture() with { Limits = new WasmLimits(0, 128) };

        Outcome<WasmPlugin> opened = runtime.Host.Open(config);

        Assert.Equal(ErrorCode.InvalidArgument, opened.Error?.Code);
    }

    private static Query Text(string text) => new(text, [], [], null, null, null);
}

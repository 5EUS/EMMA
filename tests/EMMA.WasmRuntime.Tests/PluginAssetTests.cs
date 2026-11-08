using EMMA.Domain;

using Xunit;

namespace EMMA.WasmRuntime.Tests;

/// <summary>Tests for plugin asset through the native host.</summary>
/// <param name="runtime">The shared native runtime.</param>
public sealed class PluginAssetTests(RuntimeFixture runtime) : IClassFixture<RuntimeFixture>
{
    /// <summary>Resolve returns each delivery kind.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ResolveReturnsEachDeliveryKind()
    {
        using WasmPlugin plugin = runtime.Open(TestConfigs.Fixture());
        CancellationToken cancel = TestContext.Current.CancellationToken;

        Asset remote = (await plugin.ResolveAsync(Resolve("s1-c1-p1", "image"), cancel).ConfigureAwait(true)).Outcome.Value.Single();
        Asset inline = (await plugin.ResolveAsync(Resolve("inline1", "text"), cancel).ConfigureAwait(true)).Outcome.Value.Single();
        Asset proxied = (await plugin.ResolveAsync(Resolve("proxied1", "image"), cancel).ConfigureAwait(true)).Outcome.Value.Single();
        PluginCall<IReadOnlyList<Asset>> unsupported = await plugin.ResolveAsync(Resolve("s1", "image"), cancel).ConfigureAwait(true);

        RemoteDelivery delivery = Assert.IsType<RemoteDelivery>(remote.Delivery);
        Assert.Equal("files.example.com", delivery.Url.Host);
        Assert.Equal("/s1-c1-p1.png", delivery.Url.AbsolutePath);
        Assert.Equal(new HttpHeader("referer", "https://example.com/"), delivery.Headers.Single());
        Assert.Null(delivery.ExpiresAt);
        Assert.Equal("image/png", remote.Mime);
        Assert.Equal(PropValue.FromText("original"), remote.Props.Single().Value);
        Assert.Equal("hello"u8.ToArray(), Assert.IsType<InlineDelivery>(inline.Delivery).Payload.ToArray());
        Assert.Equal(5, inline.Size);
        Assert.Equal("tok-1", Assert.IsType<ProxiedDelivery>(proxied.Delivery).Token);
        Assert.Equal(ErrorCode.Unsupported, unsupported.Outcome.Error?.Code);
    }

    /// <summary>Read returns bytes honouring ranges.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ReadReturnsBytesHonouringRanges()
    {
        using WasmPlugin plugin = runtime.Open(TestConfigs.Fixture());
        CancellationToken cancel = TestContext.Current.CancellationToken;

        Chunk whole = (await plugin.ReadAsync("tok-1", null, cancel).ConfigureAwait(true)).Outcome.Value;
        Chunk part = (await plugin.ReadAsync("tok-1", new ByteRange(10, 5), cancel).ConfigureAwait(true)).Outcome.Value;
        PluginCall<Chunk> missing = await plugin.ReadAsync("nope", null, cancel).ConfigureAwait(true);

        Assert.Equal(1000, whole.Bytes.Length);
        Assert.Equal(1000, whole.Total);
        Assert.Equal(new byte[] { 10, 11, 12, 13, 14 }, part.Bytes.ToArray());
        Assert.Equal(1000, part.Total);
        Assert.Equal(ErrorCode.NotFound, missing.Outcome.Error?.Code);
    }

    /// <summary>Read carries megabytes without corruption.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ReadCarriesMegabytesWithoutCorruption()
    {
        using WasmPlugin plugin = runtime.Open(TestConfigs.Fixture());

        Chunk big = (await plugin.ReadAsync("big", null, TestContext.Current.CancellationToken).ConfigureAwait(true)).Outcome.Value;

        Assert.Equal(5 * 1024 * 1024, big.Bytes.Length);
        Assert.Equal((byte)0, big.Bytes.Span[251]);
        Assert.Equal((byte)(1_000_003 % 251), big.Bytes.Span[1_000_003]);
    }

    private static ResolveRequest Resolve(string node, string role) => new(LocalNodeId.Create(node), AssetRole.Create(role), []);
}

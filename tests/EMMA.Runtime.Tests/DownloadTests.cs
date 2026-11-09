using System.Text;

using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Runtime.Tests;

/// <summary>Tests downloads over the real stores and files: a leaf is stored on disk, survives a restart, and is served before the plugin is asked.</summary>
public sealed class DownloadTests
{
    private static readonly AssetRole Image = AssetRole.Create("image");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A downloaded chapter is read back from disk, by whole or by range, even after a restart with a plugin that now fails.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ADownloadIsServedFromDiskAfterARestart()
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            FakePluginGateway gateway = Gateway();
            EmmaRuntime first = host.Open(options => options with { GatewayOverride = gateway });
            await using (first.ConfigureAwait(true))
            {
                await first.InitializeAsync(Ct).ConfigureAwait(true);
                await first.Nodes.UpsertAsync([new Node(TestData.Ref("c1"), NodeKind.Create("chapter"), "C1", null, null, [])], Ct).ConfigureAwait(true);
                DownloadJob job = (await first.Downloads.EnqueueAsync(TestData.Ref("c1"), Image, null, Ct).ConfigureAwait(true)).Value;
                await first.Downloads.RunUntilIdleAsync(Ct).ConfigureAwait(true);

                Assert.Equal(DownloadState.Completed, (await first.Downloads.GetAsync(job.Id, Ct).ConfigureAwait(true)).Value.Job.State);
                int resolves = gateway.ResolveCalls;
                Outcome<AssetBytes> served = await first.ReadAssetAsync(TestData.Ref("c1p1"), Image, 0, null, null, Ct).ConfigureAwait(true);
                Assert.Equal("c1p1", Encoding.UTF8.GetString(served.Value.Bytes.Span));
                Assert.Equal(resolves, gateway.ResolveCalls);
            }

            FakePluginGateway broken = new() { OnResolve = (_, _) => Outcome.Failure<IReadOnlyList<Asset>>(ErrorCode.NotFound, "offline") };
            EmmaRuntime second = host.Open(options => options with { GatewayOverride = broken });
            await using (second.ConfigureAwait(true))
            {
                await second.InitializeAsync(Ct).ConfigureAwait(true);

                Outcome<AssetBytes> whole = await second.ReadAssetAsync(TestData.Ref("c1p2"), Image, 0, null, null, Ct).ConfigureAwait(true);
                Outcome<AssetBytes> part = await second.ReadAssetAsync(TestData.Ref("c1p2"), Image, 0, new ByteRange(1, 2), null, Ct).ConfigureAwait(true);
                Outcome<AssetBytes> tail = await second.ReadAssetAsync(TestData.Ref("c1p2"), Image, 0, new ByteRange(2, null), null, Ct).ConfigureAwait(true);
                Outcome<AssetBytes> beyond = await second.ReadAssetAsync(TestData.Ref("c1p2"), Image, 0, new ByteRange(50, 10), null, Ct).ConfigureAwait(true);
                Outcome<AssetBytes> otherIndex = await second.ReadAssetAsync(TestData.Ref("c1p2"), Image, 1, null, null, Ct).ConfigureAwait(true);

                Assert.Equal("c1p2", Encoding.UTF8.GetString(whole.Value.Bytes.Span));
                Assert.Equal("image/png", whole.Value.Mime);
                Assert.Equal("1p", Encoding.UTF8.GetString(part.Value.Bytes.Span));
                Assert.Equal((1L, 4L), (part.Value.RangeStart, part.Value.Total));
                Assert.Equal("p2", Encoding.UTF8.GetString(tail.Value.Bytes.Span));
                Assert.True(beyond.Value.RangeNotSatisfiable);
                Assert.False(otherIndex.IsSuccess);

                // Only the read of an asset that was not downloaded (index 1) went to the plugin.
                Assert.Equal(1, broken.ResolveCalls);
            }
        }
    }

    /// <summary>A job a killed process left running is picked up by the next runtime.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AnInterruptedJobResumesAfterARestart()
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            Guid id;
            EmmaRuntime first = host.Open(options => options with { GatewayOverride = Gateway() });
            await using (first.ConfigureAwait(true))
            {
                await first.InitializeAsync(Ct).ConfigureAwait(true);
                await first.Nodes.UpsertAsync([new Node(TestData.Ref("c1"), NodeKind.Create("chapter"), "C1", null, null, [])], Ct).ConfigureAwait(true);
                id = (await first.Downloads.EnqueueAsync(TestData.Ref("c1"), Image, null, Ct).ConfigureAwait(true)).Value.Id;
            }

            EmmaRuntime second = host.Open(options => options with { GatewayOverride = Gateway() });
            await using (second.ConfigureAwait(true))
            {
                await second.InitializeAsync(Ct).ConfigureAwait(true);
                await second.Downloads.RunUntilIdleAsync(Ct).ConfigureAwait(true);

                Assert.Equal(DownloadState.Completed, (await second.Downloads.GetAsync(id, Ct).ConfigureAwait(true)).Value.Job.State);
            }
        }
    }

    /// <summary>With nothing downloaded, the hint goes to the plugin when an asset is read.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TheHintReachesThePluginWhenNothingIsStored()
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            FakePluginGateway gateway = Gateway();
            IReadOnlyList<Prop>? seen = null;
            Func<PluginId, ResolveRequest, Outcome<IReadOnlyList<Asset>>> inner = gateway.OnResolve;
            gateway.OnResolve = (plugin, request) =>
            {
                seen = request.Hints;
                return inner(plugin, request);
            };
            EmmaRuntime runtime = host.Open(options => options with { GatewayOverride = gateway });
            await using (runtime.ConfigureAwait(true))
            {
                await runtime.InitializeAsync(Ct).ConfigureAwait(true);

                Outcome<AssetBytes> read = await runtime.ReadAssetAsync(TestData.Ref("c1p1"), Image, 0, null, "720p", Ct).ConfigureAwait(true);

                Assert.True(read.IsSuccess);
                Prop hint = Assert.Single(seen!);
                Assert.Equal(("emma.quality", "720p"), (hint.Key, hint.Value.Text));
            }
        }
    }

    private static FakePluginGateway Gateway() => new()
    {
        OnChildren = (_, parent, _) => Outcome.Success(string.Equals(parent?.Value, "c1", StringComparison.Ordinal)
            ? TestData.RawPage(null, TestData.RawNode("c1p1", "page"), TestData.RawNode("c1p2", "page"))
            : TestData.RawPage(null)),
        OnResolve = (_, request) =>
            Outcome.Success<IReadOnlyList<Asset>>([new Asset("image/png", new InlineDelivery(Encoding.UTF8.GetBytes(request.Node.Value)), null, [])]),
    };
}

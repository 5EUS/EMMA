using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.Infrastructure.InMemory;
using EMMA.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace EMMA.Application.Tests;

/// <summary>Tests for <see cref="ProgressPipeline"/>.</summary>
public sealed class ProgressPipelineTests
{
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly InMemoryNodeStore nodes = new();
    private readonly ProgressPipeline pipeline;

    /// <summary>Initializes a new instance of the <see cref="ProgressPipelineTests"/> class.</summary>
    public ProgressPipelineTests() =>
        this.pipeline = new ProgressPipeline(new InMemoryProgressStore(this.nodes), this.time);

    /// <summary>Progress is stamped by the injected clock and can be read back.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SetStampsWithTheClockAndStores()
    {
        Outcome<Progress> set = await this.pipeline.SetAsync(TestData.User, TestData.Ref("c1"), new ProgressLocator("page", 3, 20), false, CancellationToken.None).ConfigureAwait(true);
        Progress? read = await this.pipeline.GetAsync(TestData.User, TestData.Ref("c1"), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(this.time.GetUtcNow(), set.Value.UpdatedAt);
        Assert.Equal(set.Value, read);
    }

    /// <summary>Malformed locators are rejected and nothing is stored.</summary>
    /// <param name="unit">The unit.</param>
    /// <param name="value">The position.</param>
    /// <param name="total">The extent.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("Page", 1.0, 2.0)]
    [InlineData("", 1.0, 2.0)]
    [InlineData("page", -1.0, 2.0)]
    [InlineData("page", double.NaN, 2.0)]
    [InlineData("page", 5.0, 2.0)]
    [InlineData("page", 1.0, double.PositiveInfinity)]
    public async Task InvalidLocatorsAreRejected(string unit, double value, double total)
    {
        Outcome<Progress> set = await this.pipeline.SetAsync(TestData.User, TestData.Ref("c1"), new ProgressLocator(unit, value, total), false, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, set.Error?.Code);
        Assert.Null(await this.pipeline.GetAsync(TestData.User, TestData.Ref("c1"), CancellationToken.None).ConfigureAwait(true));
    }

    /// <summary>A locator without a total is valid; the unit is never interpreted.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UnknownUnitsAreAccepted()
    {
        Outcome<Progress> set = await this.pipeline.SetAsync(TestData.User, TestData.Ref("t1"), new ProgressLocator("paragraph", 12, null), true, CancellationToken.None).ConfigureAwait(true);

        Assert.True(set.IsSuccess);
        Assert.True(set.Value.Completed);
    }

    /// <summary>Continue returns the most recently updated progress within a container, however deep.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ContinueFindsTheLatestDescendant()
    {
        NodeKind kind = NodeKind.Create("series");
        await this.nodes.UpsertAsync(
            [
                new Node(TestData.Ref("s1"), kind, "S", null, null, []),
                new Node(TestData.Ref("c1"), kind, "C1", TestData.Ref("s1"), null, []),
                new Node(TestData.Ref("c2"), kind, "C2", TestData.Ref("s1"), null, []),
                new Node(TestData.Ref("other"), kind, "O", null, null, []),
            ],
            CancellationToken.None).ConfigureAwait(true);

        await this.pipeline.SetAsync(TestData.User, TestData.Ref("c1"), new ProgressLocator("page", 1, null), false, CancellationToken.None).ConfigureAwait(true);
        this.time.Advance(TimeSpan.FromMinutes(1));
        await this.pipeline.SetAsync(TestData.User, TestData.Ref("c2"), new ProgressLocator("page", 2, null), false, CancellationToken.None).ConfigureAwait(true);
        this.time.Advance(TimeSpan.FromMinutes(1));
        await this.pipeline.SetAsync(TestData.User, TestData.Ref("other"), new ProgressLocator("page", 9, null), false, CancellationToken.None).ConfigureAwait(true);

        Progress? latest = await this.pipeline.ContinueAsync(TestData.User, TestData.Ref("s1"), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(TestData.Ref("c2"), latest?.Node);
    }

    /// <summary>Recent progress is ordered newest first and the limit is clamped to at least one entry.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RecentIsNewestFirstAndClamped()
    {
        await this.pipeline.SetAsync(TestData.User, TestData.Ref("a"), new ProgressLocator("page", 1, null), false, CancellationToken.None).ConfigureAwait(true);
        this.time.Advance(TimeSpan.FromMinutes(1));
        await this.pipeline.SetAsync(TestData.User, TestData.Ref("b"), new ProgressLocator("page", 1, null), false, CancellationToken.None).ConfigureAwait(true);

        IReadOnlyList<Progress> all = await this.pipeline.RecentAsync(TestData.User, 10, CancellationToken.None).ConfigureAwait(true);
        IReadOnlyList<Progress> clamped = await this.pipeline.RecentAsync(TestData.User, 0, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal([TestData.Ref("b"), TestData.Ref("a")], all.Select(entry => entry.Node));
        Assert.Single(clamped);
    }
}

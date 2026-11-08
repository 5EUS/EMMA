using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace EMMA.Application.Tests;

/// <summary>Tests for <see cref="AppSettingsPipeline"/>.</summary>
public sealed class AppSettingsPipelineTests
{
    private readonly AppSettingsPipeline pipeline = new(new FakeAppSettings(), new FakeTimeProvider());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A choice is remembered, replaced and forgotten, and each call answers with the scope as it now stands.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SetsReplacesAndForgets()
    {
        Outcome<IReadOnlyDictionary<string, string>> first = await this.pipeline.SetAsync(TestData.User, "series:a", [new("reverse", "1"), new("groups", "x")], Ct).ConfigureAwait(true);
        Outcome<IReadOnlyDictionary<string, string>> second = await this.pipeline.SetAsync(TestData.User, "series:a", [new("reverse", "0"), new("groups", null)], Ct).ConfigureAwait(true);
        Outcome<IReadOnlyDictionary<string, string>> read = await this.pipeline.GetAsync(TestData.User, "series:a", Ct).ConfigureAwait(true);

        Assert.Equal(2, first.Value.Count);
        Assert.Equal([KeyValuePair.Create("reverse", "0")], second.Value);
        Assert.Equal(second.Value, read.Value);
    }

    /// <summary>Scopes the host keeps for itself, and malformed scopes, are refused for reading and writing.</summary>
    /// <param name="scope">The scope.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("emma.history")]
    [InlineData("EMMA.anything")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("bad\nscope")]
    public async Task ReservedAndMalformedScopesAreRefused(string? scope)
    {
        Outcome<IReadOnlyDictionary<string, string>> read = await this.pipeline.GetAsync(TestData.User, scope, Ct).ConfigureAwait(true);
        Outcome<IReadOnlyDictionary<string, string>> write = await this.pipeline.SetAsync(TestData.User, scope, [new("k", "v")], Ct).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, read.Error?.Code);
        Assert.Equal(ErrorCode.InvalidArgument, write.Error?.Code);
    }

    /// <summary>Bad keys, repeated keys and oversized values are refused, and nothing is applied.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task BadChangesAreRefusedWholesale()
    {
        AppSettingChange good = new("good", "1");
        AppSettingChange[][] bad =
        [
            [good, new("Bad Key", "1")],
            [good, new(string.Empty, "1")],
            [good, new(new string('k', 65), "1")],
            [good, new("good", "2")],
            [good, new("big", new string('v', AppSettingsPipeline.MaxValueLength + 1))],
            [good, new("nul", "a\0b")],
            [.. Enumerable.Range(0, AppSettingsPipeline.MaxChangesPerCall + 1).Select(index => new AppSettingChange($"k{index}", "v"))],
        ];

        foreach (AppSettingChange[] changes in bad)
        {
            Outcome<IReadOnlyDictionary<string, string>> result = await this.pipeline.SetAsync(TestData.User, "scope", changes, Ct).ConfigureAwait(true);
            Assert.Equal(ErrorCode.InvalidArgument, result.Error?.Code);
        }

        Assert.Empty((await this.pipeline.GetAsync(TestData.User, "scope", Ct).ConfigureAwait(true)).Value);
    }

    /// <summary>A scope holds a bounded number of keys, but replacing or forgetting keys at the limit still works.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AScopeHoldsABoundedNumberOfKeys()
    {
        for (int start = 0; start < AppSettingsPipeline.MaxKeysPerScope; start += AppSettingsPipeline.MaxChangesPerCall)
        {
            AppSettingChange[] batch = [.. Enumerable.Range(start, AppSettingsPipeline.MaxChangesPerCall).Select(index => new AppSettingChange($"k{index}", "v"))];
            Assert.True((await this.pipeline.SetAsync(TestData.User, "full", batch, Ct).ConfigureAwait(true)).IsSuccess);
        }

        Outcome<IReadOnlyDictionary<string, string>> overflow = await this.pipeline.SetAsync(TestData.User, "full", [new("one-more", "v")], Ct).ConfigureAwait(true);
        Outcome<IReadOnlyDictionary<string, string>> replace = await this.pipeline.SetAsync(TestData.User, "full", [new("k0", "changed")], Ct).ConfigureAwait(true);
        Outcome<IReadOnlyDictionary<string, string>> swap = await this.pipeline.SetAsync(TestData.User, "full", [new("k1", null), new("one-more", "v")], Ct).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, overflow.Error?.Code);
        Assert.True(replace.IsSuccess);
        Assert.True(swap.IsSuccess);
        Assert.Equal(AppSettingsPipeline.MaxKeysPerScope, swap.Value.Count);
    }
}

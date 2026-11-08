using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Application.Tests;

/// <summary>Tests for checking client requests against a plugin's declaration.</summary>
public sealed class QueryValidatorTests
{
    private static readonly PluginInfo Info = TestData.Info(
        [
            TestData.Filter("status", FilterType.SingleChoice, required: false, "ongoing", "done"),
            TestData.Filter("tags", FilterType.MultiChoice, required: false, "a", "b"),
            TestData.Filter("adult", FilterType.Toggle),
            TestData.Filter("year", FilterType.NumberRange),
            TestData.Filter("author", FilterType.Text),
            TestData.Filter("lang", FilterType.SingleChoice, required: true, "en", "ja"),
        ],
        [new Choice("recent", "Recent", null, true)]);

    /// <summary>A query that satisfies the declaration passes.</summary>
    [Fact]
    public void ValidQueryPasses() => Assert.Null(QueryValidator.ValidateQuery(Info, Query(Value("status", "done"))));

    /// <summary>Unknown kinds, filters and sorts are named in the error.</summary>
    [Fact]
    public void UnknownNamesAreRejected()
    {
        Assert.Contains("podcast", QueryValidator.ValidateQuery(Info, Query() with { Kinds = [NodeKind.Create("podcast")] })?.Message, StringComparison.Ordinal);
        Assert.Contains("nope", QueryValidator.ValidateQuery(Info, Query(Value("nope", "1")))?.Message, StringComparison.Ordinal);
        Assert.Contains("hot", QueryValidator.ValidateQuery(Info, Query() with { Sort = "hot" })?.Message, StringComparison.Ordinal);
        Assert.Null(QueryValidator.ValidateQuery(Info, Query() with { Sort = "recent" }));
    }

    /// <summary>A required filter must be supplied.</summary>
    [Fact]
    public void RequiredFilterIsEnforced() =>
        Assert.Contains("lang", QueryValidator.ValidateQuery(Info, new Query("x", [], []))?.Message, StringComparison.Ordinal);

    /// <summary>A filter supplied twice is rejected.</summary>
    [Fact]
    public void RepeatedFilterIsRejected() =>
        Assert.NotNull(QueryValidator.ValidateQuery(Info, Query(Value("status", "done"), Value("status", "ongoing"))));

    /// <summary>Value counts and membership are checked per filter type.</summary>
    /// <param name="id">The filter id.</param>
    /// <param name="values">The values.</param>
    /// <param name="valid">Whether the values should pass.</param>
    [Theory]
    [InlineData("status", new[] { "done" }, true)]
    [InlineData("status", new[] { "other" }, false)]
    [InlineData("status", new string[0], false)]
    [InlineData("status", new[] { "done", "ongoing" }, false)]
    [InlineData("tags", new[] { "a", "b" }, true)]
    [InlineData("tags", new[] { "a", "z" }, false)]
    [InlineData("adult", new string[0], true)]
    [InlineData("adult", new[] { "true" }, true)]
    [InlineData("adult", new[] { "true", "true" }, false)]
    [InlineData("author", new[] { "someone" }, true)]
    [InlineData("author", new string[0], false)]
    public void FilterValuesAreCheckedByType(string id, string[] values, bool valid) =>
        Assert.Equal(valid, QueryValidator.ValidateQuery(Info, Query(Value(id, values))) is null);

    /// <summary>Ranges need two values, each empty or a finite number, with the minimum not above the maximum.</summary>
    /// <param name="min">The minimum text.</param>
    /// <param name="max">The maximum text.</param>
    /// <param name="valid">Whether the range should pass.</param>
    [Theory]
    [InlineData("2000", "2020", true)]
    [InlineData("", "2020", true)]
    [InlineData("2000", "", true)]
    [InlineData("2020", "2000", false)]
    [InlineData("abc", "2000", false)]
    [InlineData("NaN", "2000", false)]
    [InlineData("1,5", "2000", false)]
    public void RangeBoundsAreChecked(string min, string max, bool valid) =>
        Assert.Equal(valid, QueryValidator.ValidateQuery(Info, Query(Value("year", min, max))) is null);

    /// <summary>A range with the wrong number of bounds is rejected.</summary>
    [Fact]
    public void RangeNeedsTwoBounds() => Assert.NotNull(QueryValidator.ValidateQuery(Info, Query(Value("year", "2000"))));

    /// <summary>Over-long text and bad paging values are rejected.</summary>
    [Fact]
    public void LimitsAreEnforced()
    {
        Assert.NotNull(QueryValidator.ValidateQuery(Info, Query() with { Text = new string('x', ContractLimits.MaxQueryTextLength + 1) }));
        Assert.NotNull(QueryValidator.ValidateQuery(Info, Query() with { Limit = 0 }));
        Assert.NotNull(QueryValidator.ValidateQuery(Info, Query() with { Limit = ContractLimits.MaxNodesPerPage + 1 }));
        Assert.NotNull(QueryValidator.ValidateQuery(Info, Query() with { Cursor = string.Empty }));
        Assert.Null(QueryValidator.ValidateQuery(Info, Query() with { Limit = ContractLimits.MaxNodesPerPage, Cursor = "c" }));
    }

    /// <summary>A required filter scoped to other kinds does not apply to a query for different kinds.</summary>
    [Fact]
    public void RequiredFilterOnlyAppliesToItsKinds()
    {
        FilterSpec scoped = TestData.Filter("lang", FilterType.Text, required: true) with { Kinds = [NodeKind.Create("series")] };
        PluginInfo info = TestData.Info([scoped]);

        Assert.NotNull(QueryValidator.ValidateQuery(info, new Query("x", [NodeKind.Create("series")], [])));
        Assert.Null(QueryValidator.ValidateQuery(info, new Query("x", [NodeKind.Create("page")], [])));
    }

    private static Query Query(params FilterValue[] filters) => new(
        "x",
        [],
        [new FilterValue("lang", ["en"]), .. filters]);

    private static FilterValue Value(string id, params string[] values) => new(id, values);
}

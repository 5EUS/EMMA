using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Application.Tests;

/// <summary>Tests for <see cref="PluginInfoValidator"/>: what a plugin may declare in <c>describe</c>.</summary>
public sealed class PluginInfoValidatorTests
{
    private static readonly SettingCategory Connection = new("connection", "Connection", null);

    private static readonly Choice[] Languages =
    [
        new("en", "English", null, true),
        new("ja", "Japanese", null, false),
    ];

    /// <summary>A realistic set of typed settings, with categories, choices, defaults and a visibility rule, is valid.</summary>
    [Fact]
    public void ARealisticDeclarationIsValid()
    {
        PluginInfo info = WithSettings(
            [Connection],
            Setting("connection.user", SettingType.Text) with { Category = "connection", MaxLength = 120, Placeholder = "name" },
            Setting("connection.token", SettingType.Secret) with { Category = "connection", Required = true },
            Setting("search.language", SettingType.Choice) with { Choices = Languages, DefaultValues = ["en"] },
            Setting("search.maturity", SettingType.MultiChoice) with { Choices = Languages, DefaultValues = ["en", "ja"] },
            Setting("advanced.verbose", SettingType.Boolean) with { DefaultValues = ["false"] },
            Setting("advanced.timeout", SettingType.Duration) with { DefaultValues = ["15000"], VisibleWhen = new SettingCondition("advanced.verbose", "true") },
            Setting("advanced.ratio", SettingType.Number) with { DefaultValues = ["1.5"] });

        Assert.Null(PluginInfoValidator.FindProblem(info));
    }

    /// <summary>Each broken declaration is refused with a reason that names the setting.</summary>
    [Fact]
    public void BrokenSettingsAreRefused()
    {
        (string Why, PluginInfo Info)[] cases =
        [
            ("duplicate key", WithSettings([], Setting("a", SettingType.Text), Setting("a", SettingType.Text))),
            ("bad key", WithSettings([], Setting("1 bad", SettingType.Text))),
            ("unknown category", WithSettings([], Setting("a", SettingType.Text) with { Category = "nope" })),
            ("choice without choices", WithSettings([], Setting("a", SettingType.Choice))),
            ("text with choices", WithSettings([], Setting("a", SettingType.Text) with { Choices = Languages })),
            ("duplicate choices", WithSettings([], Setting("a", SettingType.Choice) with { Choices = [Languages[0], Languages[0]] })),
            ("secret default", WithSettings([], Setting("a", SettingType.Secret) with { DefaultValues = ["hunter2"] })),
            ("two defaults on a single choice", WithSettings([], Setting("a", SettingType.Choice) with { Choices = Languages, DefaultValues = ["en", "ja"] })),
            ("default not a choice", WithSettings([], Setting("a", SettingType.Choice) with { Choices = Languages, DefaultValues = ["xx"] })),
            ("bad boolean default", WithSettings([], Setting("a", SettingType.Boolean) with { DefaultValues = ["yes"] })),
            ("bad number default", WithSettings([], Setting("a", SettingType.Number) with { DefaultValues = ["NaN"] })),
            ("bad duration default", WithSettings([], Setting("a", SettingType.Duration) with { DefaultValues = ["00:00:15"] })),
            ("default over max length", WithSettings([], Setting("a", SettingType.Text) with { MaxLength = 2, DefaultValues = ["abc"] })),
            ("max length on a number", WithSettings([], Setting("a", SettingType.Number) with { MaxLength = 5 })),
            ("depends on itself", WithSettings([], Setting("a", SettingType.Boolean) with { VisibleWhen = new SettingCondition("a", "true") })),
            ("depends on an unknown setting", WithSettings([], Setting("a", SettingType.Text) with { VisibleWhen = new SettingCondition("zzz", "x") })),
            ("depends on a value the other cannot have", WithSettings([], Setting("a", SettingType.Boolean), Setting("b", SettingType.Text) with { VisibleWhen = new SettingCondition("a", "maybe") })),
            ("visibility cycle", WithSettings([], Setting("a", SettingType.Boolean) with { VisibleWhen = new SettingCondition("b", "true") }, Setting("b", SettingType.Boolean) with { VisibleWhen = new SettingCondition("a", "true") })),
            ("duplicate category", WithSettings([Connection, Connection])),
        ];

        foreach ((string why, PluginInfo info) in cases)
        {
            Assert.True(PluginInfoValidator.FindProblem(info) is not null, $"should refuse: {why}");
        }
    }

    /// <summary>A kind that lists a child kind it never declared, a repeated kind, or a filter for an unknown kind is refused.</summary>
    [Fact]
    public void BrokenKindsAreRefused()
    {
        PluginInfo orphanChild = new([TestData.Kind("series", ["chapter"])], [], [], [], []);
        PluginInfo repeated = new([TestData.Kind("series"), TestData.Kind("series")], [], [], [], []);
        PluginInfo filterForUnknown = TestData.Info([TestData.Filter("x", FilterType.Text) with { Kinds = [NodeKind.Create("podcast")] }]);

        Assert.Contains("chapter", PluginInfoValidator.FindProblem(orphanChild), StringComparison.Ordinal);
        Assert.Contains("twice", PluginInfoValidator.FindProblem(repeated), StringComparison.Ordinal);
        Assert.Contains("podcast", PluginInfoValidator.FindProblem(filterForUnknown), StringComparison.Ordinal);
    }

    /// <summary>The standard test declaration is valid, and duplicate filters or sorts are refused.</summary>
    [Fact]
    public void DuplicatesAndTheBaselineAreHandled()
    {
        Assert.Null(PluginInfoValidator.FindProblem(TestData.Info()));
        Assert.NotNull(PluginInfoValidator.FindProblem(TestData.Info([TestData.Filter("a", FilterType.Text), TestData.Filter("a", FilterType.Text)])));
        Assert.NotNull(PluginInfoValidator.FindProblem(TestData.Info(sorts: [new Choice("x", "X", null, false), new Choice("x", "X", null, false)])));
    }

    /// <summary>The catalog pipeline refuses a plugin whose declaration is invalid, without searching it.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ThePipelineRefusesAnInvalidDeclarationBeforeSearching()
    {
        FakePluginGateway gateway = new()
        {
            OnDescribe = _ => Outcome.Success(WithSettings([], Setting("a", SettingType.Secret) with { DefaultValues = ["leak"] })),
        };
        CatalogPipeline pipeline = new(gateway, new EMMA.Infrastructure.InMemory.InMemoryNodeStore(), new EMMA.Infrastructure.InMemory.InMemoryNodeListingStore(), new PluginCallExecutor(PluginCallOptions.Default, TimeProvider.System), TimeProvider.System);

        Outcome<NodePage> result = await pipeline.SearchAsync(TestData.Plugin, TestData.TextQuery("x"), TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(ErrorCode.Internal, result.Error?.Code);
        Assert.Contains("secret", result.Error?.Message, StringComparison.Ordinal);
        Assert.Equal(0, gateway.SearchCalls);
    }

    private static SettingSpec Setting(string key, SettingType type) =>
        new(key, key, null, null, type, null, false, [], [], null, null, null);

    private static PluginInfo WithSettings(IReadOnlyList<SettingCategory> categories, params SettingSpec[] settings) =>
        TestData.Info() with { Categories = categories, Settings = settings };
}

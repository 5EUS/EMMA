using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Application.Tests;

/// <summary>Tests for <see cref="PluginSettingsPipeline"/>: what a user may set is whatever the plugin declared.</summary>
public sealed class PluginSettingsPipelineTests
{
    private static readonly Choice[] Languages =
    [
        new("en", "English", null, true),
        new("ja", "Japanese", null, false),
        new("de", "German", null, false),
    ];

    private readonly FakePluginGateway gateway = new();
    private readonly FakePluginSettings store = new();
    private readonly PluginSettingsPipeline pipeline;

    /// <summary>Initializes a new instance of the <see cref="PluginSettingsPipelineTests"/> class.</summary>
    public PluginSettingsPipelineTests()
    {
        this.gateway.OnDescribe = _ => Outcome.Success(TestData.Info() with { Settings = Declared() });
        this.pipeline = new PluginSettingsPipeline(this.gateway, this.store);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Every declared setting is listed in declaration order, and a required one with no value and no default is flagged.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ListsEveryDeclaredSettingAndFlagsWhatIsMissing()
    {
        Outcome<IReadOnlyList<SettingState>> states = await this.pipeline.GetAsync(TestData.Plugin, Ct).ConfigureAwait(true);

        Assert.True(states.IsSuccess, states.Error?.Message);
        Assert.Equal(["user", "token", "language", "languages", "verbose", "timeout"], states.Value.Select(state => state.Key));
        Assert.All(states.Value, state => Assert.False(state.IsSet));
        Assert.Equal(["token"], states.Value.Where(state => state.Missing).Select(state => state.Key));
    }

    /// <summary>Changes are stored and the settings come back as they now stand, with a secret known to be set but not shown.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SetStoresAndReturnsTheNewState()
    {
        Outcome<IReadOnlyList<SettingState>> states = await this.pipeline.SetAsync(
            TestData.Plugin,
            [
                new SettingChange("user", ["me"]),
                new SettingChange("token", ["abc123"]),
                new SettingChange("languages", ["ja", "de"]),
            ],
            Ct).ConfigureAwait(true);

        Assert.True(states.IsSuccess, states.Error?.Message);
        SettingState user = Named(states.Value, "user");
        SettingState token = Named(states.Value, "token");
        Assert.Equal(["me"], user.Values);
        Assert.True(token.IsSet);
        Assert.Empty(token.Values);
        Assert.False(token.Missing);
        Assert.Equal(["ja", "de"], Named(states.Value, "languages").Values);
        Assert.Equal(["abc123"], (await this.store.GetAsync(TestData.Plugin, Ct).ConfigureAwait(true))["token"]);
    }

    /// <summary>Only a secret setting is stored as a secret.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task OnlySecretSettingsAreStoredAsSecrets()
    {
        await this.pipeline.SetAsync(TestData.Plugin, [new SettingChange("user", ["me"]), new SettingChange("token", ["abc"])], Ct).ConfigureAwait(true);

        IReadOnlyList<StoredSetting> stored = await this.store.ListAsync(TestData.Plugin, Ct).ConfigureAwait(true);

        Assert.False(Stored(stored, "user").IsSecret);
        Assert.True(Stored(stored, "token").IsSecret);
    }

    /// <summary>Clearing a setting makes its default apply again.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ClearingGoesBackToTheDefault()
    {
        await this.pipeline.SetAsync(TestData.Plugin, [new SettingChange("language", ["ja"])], Ct).ConfigureAwait(true);

        Outcome<IReadOnlyList<SettingState>> states = await this.pipeline.SetAsync(TestData.Plugin, [new SettingChange("language", [])], Ct).ConfigureAwait(true);

        Assert.False(Named(states.Value, "language").IsSet);
        Assert.Empty(await this.store.GetAsync(TestData.Plugin, Ct).ConfigureAwait(true));
    }

    /// <summary>A setting the plugin did not declare cannot be set.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UndeclaredSettingIsRefused()
    {
        Outcome<IReadOnlyList<SettingState>> states = await this.pipeline.SetAsync(TestData.Plugin, [new SettingChange("nope", ["x"])], Ct).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, states.Error?.Code);
        Assert.Contains("nope", states.Error!.Message, StringComparison.Ordinal);
    }

    /// <summary>Values that do not suit the setting's type are refused with a message that names the setting.</summary>
    /// <param name="key">The setting.</param>
    /// <param name="value">The value.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("verbose", "yes")]
    [InlineData("timeout", "-5")]
    [InlineData("timeout", "1.5")]
    [InlineData("timeout", "soon")]
    [InlineData("language", "fr")]
    [InlineData("languages", "fr")]
    [InlineData("token", "")]
    public async Task ValueThatDoesNotSuitTheTypeIsRefused(string key, string value)
    {
        Outcome<IReadOnlyList<SettingState>> states = await this.pipeline.SetAsync(TestData.Plugin, [new SettingChange(key, [value])], Ct).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, states.Error?.Code);
        Assert.Contains(key, states.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(await this.store.GetAsync(TestData.Plugin, Ct).ConfigureAwait(true));
    }

    /// <summary>Text is held to the setting's own length limit and to the contract's.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TextLengthIsLimited()
    {
        Outcome<IReadOnlyList<SettingState>> tooLongForSetting = await this.pipeline.SetAsync(TestData.Plugin, [new SettingChange("user", [new string('a', 121)])], Ct).ConfigureAwait(true);
        Outcome<IReadOnlyList<SettingState>> tooLongForContract = await this.pipeline.SetAsync(TestData.Plugin, [new SettingChange("token", [new string('a', ContractLimits.MaxSettingValueLength + 1)])], Ct).ConfigureAwait(true);
        Outcome<IReadOnlyList<SettingState>> atTheLimit = await this.pipeline.SetAsync(TestData.Plugin, [new SettingChange("user", [new string('a', 120)])], Ct).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, tooLongForSetting.Error?.Code);
        Assert.Equal(ErrorCode.InvalidArgument, tooLongForContract.Error?.Code);
        Assert.True(atTheLimit.IsSuccess);
    }

    /// <summary>Only a multi-choice setting takes several values, and never the same one twice.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task OnlyMultiChoiceTakesSeveralDistinctValues()
    {
        Outcome<IReadOnlyList<SettingState>> several = await this.pipeline.SetAsync(TestData.Plugin, [new SettingChange("language", ["en", "ja"])], Ct).ConfigureAwait(true);
        Outcome<IReadOnlyList<SettingState>> twice = await this.pipeline.SetAsync(TestData.Plugin, [new SettingChange("languages", ["en", "en"])], Ct).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, several.Error?.Code);
        Assert.Equal(ErrorCode.InvalidArgument, twice.Error?.Code);
    }

    /// <summary>One bad change in a request means nothing is stored, not even the good ones.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ARequestIsAppliedWholeOrNotAtAll()
    {
        Outcome<IReadOnlyList<SettingState>> states = await this.pipeline.SetAsync(
            TestData.Plugin,
            [new SettingChange("user", ["me"]), new SettingChange("verbose", ["maybe"])],
            Ct).ConfigureAwait(true);

        Assert.False(states.IsSuccess);
        Assert.Empty(await this.store.GetAsync(TestData.Plugin, Ct).ConfigureAwait(true));
    }

    /// <summary>Changing the same setting twice in one request is ambiguous, so it is refused.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SameSettingTwiceIsRefused()
    {
        Outcome<IReadOnlyList<SettingState>> states = await this.pipeline.SetAsync(TestData.Plugin, [new SettingChange("user", ["a"]), new SettingChange("user", ["b"])], Ct).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, states.Error?.Code);
    }

    /// <summary>A plugin that cannot say what it declares (disabled, say) cannot have settings read or changed.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task PluginThatCannotDescribeItselfIsRefused()
    {
        this.gateway.OnDescribe = _ => Outcome.Failure<PluginInfo>(ErrorCode.Unsupported, "disabled");

        Outcome<IReadOnlyList<SettingState>> read = await this.pipeline.GetAsync(TestData.Plugin, Ct).ConfigureAwait(true);
        Outcome<IReadOnlyList<SettingState>> write = await this.pipeline.SetAsync(TestData.Plugin, [new SettingChange("user", ["a"])], Ct).ConfigureAwait(true);

        Assert.Equal(ErrorCode.Unsupported, read.Error?.Code);
        Assert.Equal(ErrorCode.Unsupported, write.Error?.Code);
    }

    private static SettingState Named(IReadOnlyList<SettingState> states, string key) =>
        states.Single(state => string.Equals(state.Key, key, StringComparison.Ordinal));

    private static StoredSetting Stored(IReadOnlyList<StoredSetting> settings, string key) =>
        settings.Single(setting => string.Equals(setting.Key, key, StringComparison.Ordinal));

    private static SettingSpec[] Declared() =>
    [
        Spec("user", SettingType.Text) with { MaxLength = 120 },
        Spec("token", SettingType.Secret) with { Required = true },
        Spec("language", SettingType.Choice) with { Choices = Languages, DefaultValues = ["en"] },
        Spec("languages", SettingType.MultiChoice) with { Choices = Languages },
        Spec("verbose", SettingType.Boolean) with { DefaultValues = ["false"] },
        Spec("timeout", SettingType.Duration) with { DefaultValues = ["15000"] },
    ];

    private static SettingSpec Spec(string key, SettingType type) =>
        new(key, key, null, null, type, null, false, [], [], null, null, null);
}

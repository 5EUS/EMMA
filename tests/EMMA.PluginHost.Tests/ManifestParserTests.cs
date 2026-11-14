using System.Text;

using EMMA.Domain;
using EMMA.PluginHost.Manifest;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.PluginHost.Tests;

/// <summary>Tests for <see cref="ManifestParser"/>: strict reading of untrusted manifests.</summary>
public sealed class ManifestParserTests
{
    private static readonly string ValidJson = TestManifests.Json(TestManifests.Valid());

    /// <summary>A manifest survives writing and parsing unchanged.</summary>
    [Fact]
    public void RoundTrips()
    {
        PluginManifest original = TestManifests.Valid();

        Outcome<PluginManifest> parsed = ManifestParser.Parse(ManifestWriter.Write(original), requireSignature: false);

        Assert.True(parsed.IsSuccess, parsed.Error?.Message);
        Assert.Equal(ManifestWriter.Write(original), ManifestWriter.Write(parsed.Value));
        Assert.Equal(original.Id, parsed.Value.Id);
        Assert.Equal(original.Permissions.Network, parsed.Value.Permissions.Network);
        Assert.Equal(original.Kinds, parsed.Value.Kinds);
    }

    /// <summary>Setting-derived hosts survive writing and parsing, and a repeated key is refused.</summary>
    [Fact]
    public void NetworkSettingsRoundTripAndMustBeUnique()
    {
        PluginManifest original = TestManifests.Valid();
        original = original with { Permissions = original.Permissions with { NetworkSettings = ["instance_url"] } };

        Outcome<PluginManifest> parsed = ManifestParser.Parse(ManifestWriter.Write(original), requireSignature: false);

        Assert.True(parsed.IsSuccess, parsed.Error?.Message);
        Assert.Equal(["instance_url"], parsed.Value.Permissions.NetworkSettings);

        PluginManifest twice = original with { Permissions = original.Permissions with { NetworkSettings = ["instance_url", "instance_url"] } };
        Outcome<PluginManifest> refused = ManifestParser.Parse(ManifestWriter.Write(twice), requireSignature: false);

        Assert.False(refused.IsSuccess);
    }

    /// <summary>Optional fields may be left out entirely.</summary>
    [Fact]
    public void MinimalManifestIsValid()
    {
        const string Minimal = @"{""schema"":1,""id"":""com.example.min"",""name"":""M"",""version"":""0.0.1"",""contract"":{""required"":[""emma:core/meta@1""]},""entrypoint"":{""path"":""p.wasm"",""sha256"":""%HASH%""},""permissions"":{""network"":[],""cache"":false},""limits"":{""cpuBudgetMs"":1,""memoryMb"":1}}";

        Outcome<PluginManifest> parsed = Parse(Minimal.Replace("%HASH%", new string('b', 64), StringComparison.Ordinal));

        Assert.True(parsed.IsSuccess, parsed.Error?.Message);
        Assert.Empty(parsed.Value.Permissions.Network);
        Assert.Null(parsed.Value.Signature);
    }

    /// <summary>A signature is required when the host demands one, and the error names the field.</summary>
    [Fact]
    public void MissingSignatureIsRejectedWhenRequired()
    {
        Outcome<PluginManifest> parsed = ManifestParser.Parse(Encoding.UTF8.GetBytes(ValidJson), requireSignature: true);

        Assert.Contains("$.signature", parsed.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>Each malformed manifest is rejected, naming the offending field.</summary>
    /// <param name="find">Text in the valid manifest to replace.</param>
    /// <param name="replace">The replacement.</param>
    /// <param name="field">The field path the error must mention.</param>
    [Theory]
    [InlineData("\"schema\": 1", "\"schema\": 2", "$.schema")]
    [InlineData("\"id\": \"com.example.test\"", "\"id\": \"Com.Example\"", "$.id")]
    [InlineData("\"id\": \"com.example.test\"", "\"id\": \"com.example:evil\"", "$.id")]
    [InlineData("\"name\": \"Test plugin\"", "\"name\": \"   \"", "$.name")]
    [InlineData("\"version\": \"1.0.0\"", "\"version\": \"one\"", "$.version")]
    [InlineData("\"transport\": \"wasm\"", "\"transport\": \"process\"", "$.transport")]
    [InlineData("\"path\": \"plugin.wasm\"", "\"path\": \"../plugin.wasm\"", "$.entrypoint.path")]
    [InlineData("\"path\": \"plugin.wasm\"", "\"path\": \"/abs/plugin.wasm\"", "$.entrypoint.path")]
    [InlineData("\"path\": \"plugin.wasm\"", "\"path\": \"a/../b.wasm\"", "$.entrypoint.path")]
    [InlineData("\"path\": \"plugin.wasm\"", "\"path\": \"a\\\\b.wasm\"", "$.entrypoint.path")]
    [InlineData("\"path\": \"plugin.wasm\"", "\"path\": \"C:/b.wasm\"", "$.entrypoint.path")]
    [InlineData("\"path\": \"plugin.wasm\"", "\"path\": \"plugin.exe\"", "$.entrypoint.path")]
    [InlineData("\"path\": \"plugin.wasm\"", "\"path\": \"plugin.cwasm\"", "$.entrypoint.path")]
    [InlineData("\"path\": \"plugin.wasm\"", "\"path\": \"a//b.wasm\"", "$.entrypoint.path")]
    [InlineData("\"api.example.com\"", "\"API.example.com\"", "$.permissions.network")]
    [InlineData("\"api.example.com\"", "\"*\"", "$.permissions.network")]
    [InlineData("\"api.example.com\"", "\"*.com\\u0000\"", "$.permissions.network")]
    [InlineData("\"cpuBudgetMs\": 5000", "\"cpuBudgetMs\": 0", "$.limits.cpuBudgetMs")]
    [InlineData("\"cpuBudgetMs\": 5000", "\"cpuBudgetMs\": 5000.5", "$.limits.cpuBudgetMs")]
    [InlineData("\"memoryMb\": 128", "\"memoryMb\": 99999", "$.limits.memoryMb")]
    [InlineData("\"cache\": true", "\"cache\": \"yes\"", "$.permissions.cache")]
    [InlineData("\"emma:core/meta@1\"", "\"emma:core/meta\"", "$.contract.required")]
    [InlineData("\"fit\": \"cover\"", "\"fit\": \"stretch\"", "$.icon.fit")]
    [InlineData("\"minHostVersion\": \"1.0.0\"", "\"minHostVersion\": \"1.0\"", "$.runtime.minHostVersion")]
    [InlineData("\"series.chapter\"", "\"Series\"", "$.kinds")]
    public void MalformedFieldsAreRejected(string find, string replace, string field)
    {
        Assert.Contains(find, ValidJson, StringComparison.Ordinal);

        Outcome<PluginManifest> parsed = Parse(ValidJson.Replace(find, replace, StringComparison.Ordinal));

        Assert.False(parsed.IsSuccess);
        Assert.Contains(field, parsed.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>A property the schema does not know is refused at the root and in nested objects.</summary>
    [Fact]
    public void UnknownPropertiesAreRefused()
    {
        string root = ValidJson.Replace("\"schema\": 1,", "\"schema\": 1, \"extra\": true,", StringComparison.Ordinal);
        string nested = ValidJson.Replace("\"cache\": true", "\"cache\": true, \"filesystem\": [\"/\"]", StringComparison.Ordinal);

        Assert.Contains("$.extra", Parse(root).Error?.Message, StringComparison.Ordinal);
        Assert.Contains("$.permissions.filesystem", Parse(nested).Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>A repeated property is refused, so two readers can never disagree about which value wins.</summary>
    [Fact]
    public void DuplicatePropertiesAreRefused()
    {
        string duplicated = ValidJson.Replace("\"name\": \"Test plugin\",", "\"name\": \"Test plugin\", \"name\": \"Other\",", StringComparison.Ordinal);

        Assert.False(Parse(duplicated).IsSuccess);
    }

    /// <summary>Input that is not a JSON object, or is oversized, empty, or uses comments or trailing commas, is refused.</summary>
    /// <param name="text">The input.</param>
    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{")]
    [InlineData("{\"schema\":1,}")]
    [InlineData("{/* hi */\"schema\":1}")]
    public void NonManifestInputIsRefused(string text) => Assert.False(Parse(text).IsSuccess);

    /// <summary>A manifest over the size limit is refused before parsing.</summary>
    [Fact]
    public void OversizedInputIsRefused() =>
        Assert.False(ManifestParser.Parse(new byte[ManifestParser.MaxManifestBytes + 1], requireSignature: false).IsSuccess);

    /// <summary>Deeply nested input does not exhaust the stack.</summary>
    [Fact]
    public void DeepNestingIsRefused() =>
        Assert.False(Parse(new string('[', 5000) + new string(']', 5000)).IsSuccess);

    private static Outcome<PluginManifest> Parse(string json) =>
        ManifestParser.Parse(Encoding.UTF8.GetBytes(json), requireSignature: false);
}

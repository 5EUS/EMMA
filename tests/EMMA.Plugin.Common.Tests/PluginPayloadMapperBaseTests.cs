using System.Text.Json;

namespace EMMA.Plugin.Common.Tests;

public sealed class PluginPayloadMapperBaseTests
{
    [Fact]
    public void ParseStructArray_SkipsMissingValues()
    {
        using var doc = JsonDocument.Parse("""
            {
              "data": [
                { "id": "one" },
                { },
                { "id": "two" }
              ]
            }
            """);

        var results = PluginPayloadMapperBase.ParseStructArray<TestEntry>(
            doc.RootElement,
            "data",
            item =>
            {
                var id = PluginPayloadMapperBase.GetString(item, "id");
                return string.IsNullOrWhiteSpace(id) ? null : new TestEntry(id);
            });

        Assert.Collection(
            results,
            first => Assert.Equal("one", first.Id),
            second => Assert.Equal("two", second.Id));
    }

    [Fact]
    public void ParseObjectMetadataByKey_OnlyAddsPopulatedEntries()
    {
        using var doc = JsonDocument.Parse("""
            {
              "statistics": {
                "one": { "value": "a" },
                "two": { },
                "three": { "value": "c" }
              }
            }
            """);

        var results = PluginPayloadMapperBase.ParseObjectMetadataByKey<string>(
            doc.RootElement,
            "statistics",
            property =>
            {
                var value = PluginPayloadMapperBase.GetString(property.Value, "value");
                return string.IsNullOrWhiteSpace(value) ? [] : [value];
            });

        Assert.Equal(new[] { "one", "three" }, results.Keys.OrderBy(key => key).ToArray());
        Assert.Equal("a", results["one"].Single());
        Assert.Equal("c", results["three"].Single());
    }

    private readonly record struct TestEntry(string Id);
}
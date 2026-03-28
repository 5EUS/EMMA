using EMMA.Plugin.Common;

namespace EMMA.Plugin.Common.Tests;

public class PluginSearchQueryTests
{
    [Fact]
    public void Parse_ReturnsDefaults_WhenArgsJsonMissing()
    {
        var query = PluginSearchQuery.Parse(null, "fallback");

        Assert.Equal("fallback", query.Query);
        Assert.Empty(query.MediaTypes);
        Assert.Empty(query.Filters);
        Assert.Empty(query.QueryAdditions);
        Assert.Null(query.Sort);
        Assert.Null(query.Page);
        Assert.Null(query.PageSize);
    }

    [Fact]
    public void Parse_MapsStructuredPayload()
    {
        const string argsJson = """
        {
          "query": "one piece",
          "mediaTypes": ["paged", "video"],
          "filters": [
            {
              "id": "core.tags",
              "values": ["action", "adventure"],
              "operation": "any"
            }
          ],
          "queryAdditions": [
            {
              "id": "core.language",
              "value": "en",
              "type": "string"
            }
          ],
          "sort": "title",
          "page": "2",
          "pageSize": 20
        }
        """;

        var query = PluginSearchQuery.Parse(argsJson);

        Assert.Equal("one piece", query.Query);
        Assert.Equal(new[] { "paged", "video" }, query.MediaTypes);
        Assert.Equal("title", query.Sort);
        Assert.Equal(2, query.Page);
        Assert.Equal(20, query.PageSize);

        Assert.Equal(new[] { "action", "adventure" }, query.GetFilterValues("core.tags"));
        Assert.Equal("en", query.GetQueryAddition("core.language"));
    }

    [Fact]
    public void Parse_UsesFallbackQuery_WhenRootQueryMissing()
    {
        const string argsJson = """
        {
          "mediaTypes": ["paged"]
        }
        """;

        var query = PluginSearchQuery.Parse(argsJson, "fallback-query");

        Assert.Equal("fallback-query", query.Query);
        Assert.Equal(new[] { "paged" }, query.MediaTypes);
    }

    [Fact]
    public void Parse_ReturnsDefaults_OnInvalidJson()
    {
        var query = PluginSearchQuery.Parse("{not json", "fallback-query");

        Assert.Equal("fallback-query", query.Query);
        Assert.Empty(query.MediaTypes);
        Assert.Empty(query.Filters);
        Assert.Empty(query.QueryAdditions);
    }
}

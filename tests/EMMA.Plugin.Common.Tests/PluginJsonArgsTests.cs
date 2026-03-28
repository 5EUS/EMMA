using EMMA.Plugin.Common;

namespace EMMA.Plugin.Common.Tests;

public class PluginJsonArgsTests
{
    [Fact]
    public void GetString_ReturnsValue_AndEmptyWhenMissing()
    {
        const string argsJson = """
        {
          "mediaId": "demo-123"
        }
        """;

        Assert.Equal("demo-123", PluginJsonArgs.GetString(argsJson, "mediaId"));
        Assert.Equal(string.Empty, PluginJsonArgs.GetString(argsJson, "missing"));
    }

    [Fact]
    public void GetUInt32_And_GetInt32_Parse_Number_And_String()
    {
        const string argsJson = """
        {
          "u32Number": 42,
          "u32String": "43",
          "i32Number": -3,
          "i32String": "44"
        }
        """;

        Assert.Equal((uint)42, PluginJsonArgs.GetUInt32(argsJson, "u32Number"));
        Assert.Equal((uint)43, PluginJsonArgs.GetUInt32(argsJson, "u32String"));
        Assert.Equal(-3, PluginJsonArgs.GetInt32(argsJson, "i32Number"));
        Assert.Equal(44, PluginJsonArgs.GetInt32(argsJson, "i32String"));
    }

    [Fact]
    public void GetBool_Parses_Boolean_And_StringValues()
    {
        const string argsJson = """
        {
          "boolTrue": true,
          "boolFalse": false,
          "stringTrue": "true",
          "stringFalse": "False"
        }
        """;

        Assert.True(PluginJsonArgs.GetBool(argsJson, "boolTrue"));
        Assert.False(PluginJsonArgs.GetBool(argsJson, "boolFalse"));
        Assert.True(PluginJsonArgs.GetBool(argsJson, "stringTrue"));
        Assert.False(PluginJsonArgs.GetBool(argsJson, "stringFalse"));
        Assert.Null(PluginJsonArgs.GetBool(argsJson, "missing"));
    }

    [Fact]
    public void GetStringArray_ReturnsTrimmedNonEmptyStringValues()
    {
        const string argsJson = """
        {
          "tags": [" one ", "", "two", "   ", "three"],
          "notArray": "x"
        }
        """;

        var tags = PluginJsonArgs.GetStringArray(argsJson, "tags");
        Assert.Equal(new[] { "one", "two", "three" }, tags);

        Assert.Empty(PluginJsonArgs.GetStringArray(argsJson, "missing"));
        Assert.Empty(PluginJsonArgs.GetStringArray(argsJson, "notArray"));
    }

    [Fact]
    public void Helpers_ReturnDefaults_OnInvalidJson()
    {
        const string argsJson = "{not json";

        Assert.Equal(string.Empty, PluginJsonArgs.GetString(argsJson, "x"));
        Assert.Null(PluginJsonArgs.GetUInt32(argsJson, "x"));
        Assert.Null(PluginJsonArgs.GetInt32(argsJson, "x"));
        Assert.Null(PluginJsonArgs.GetBool(argsJson, "x"));
        Assert.Empty(PluginJsonArgs.GetStringArray(argsJson, "x"));
    }
}

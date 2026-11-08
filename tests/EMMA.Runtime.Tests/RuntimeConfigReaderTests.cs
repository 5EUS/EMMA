using System.Text;

using EMMA.Domain;

using Xunit;

namespace EMMA.Runtime.Tests;

/// <summary>Tests for the configuration document the native library and the CLI read.</summary>
public sealed class RuntimeConfigReaderTests
{
    /// <summary>With only a data directory, the user is <c>local</c> and nothing is trusted, so nothing can be installed.</summary>
    [Fact]
    public void DefaultsAreRestrictive()
    {
        Outcome<RuntimeConfig> config = Parse(@"{""data_dir"":""/var/emma""}");

        Assert.True(config.IsSuccess);
        Assert.Equal(Path.GetFullPath("/var/emma"), config.Value.DataDirectory);
        Assert.Equal("local", config.Value.User.Value);
        Assert.True(config.Value.Trust.RequireSigned);
        Assert.Empty(config.Value.Trust.Repositories);
        Assert.Null(config.Value.NativeLibraryDirectory);
    }

    /// <summary>Every field is read.</summary>
    [Fact]
    public void AllFieldsAreRead()
    {
        Outcome<RuntimeConfig> config = Parse(@"{""data_dir"":""/d"",""user"":""kim"",""native_dir"":""/n"",""trust"":{""require_signed"":false,""repositories"":[{""id"":""main"",""root_keys"":{""k1"":""PEM""}}]}}");

        Assert.Equal("kim", config.Value.User.Value);
        Assert.Equal(Path.GetFullPath("/n"), config.Value.NativeLibraryDirectory);
        Assert.False(config.Value.Trust.RequireSigned);
        Assert.Equal("PEM", config.Value.Trust.Repositories["main"].RootKeys["k1"]);
    }

    /// <summary>Documents that are wrong in any way are refused with a reason.</summary>
    /// <param name="json">The document.</param>
    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData(@"{""data_dir"":""  ""}")]
    [InlineData(@"{""data_dir"":""/d"",""user"":"" ""}")]
    [InlineData(@"{""data_dir"":""/d"",""extra"":true}")]
    [InlineData(@"{""data_dir"":""/d"",""trust"":{""repositories"":[{""id"":""a"",""root_keys"":{}}]}}")]
    [InlineData(@"{""data_dir"":""/d"",""trust"":{""repositories"":[{""root_keys"":{""k"":""PEM""}}]}}")]
    [InlineData(@"{""data_dir"":""/d"",""trust"":{""repositories"":[{""id"":""a"",""root_keys"":{""k"":"" ""}}]}}")]
    public void InvalidDocumentsAreRefused(string json)
    {
        Outcome<RuntimeConfig> config = Parse(json);

        Assert.False(config.IsSuccess);
        Assert.Equal(ErrorCode.InvalidArgument, config.Error.Code);
    }

    private static Outcome<RuntimeConfig> Parse(string json) => RuntimeConfigReader.Parse(Encoding.UTF8.GetBytes(json));
}

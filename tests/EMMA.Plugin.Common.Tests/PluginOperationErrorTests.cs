using EMMA.Plugin.Common;

namespace EMMA.Plugin.Common.Tests;

public class PluginOperationErrorTests
{
    [Theory]
    [InlineData("unsupported-operation:search", PluginOperationErrorKind.UnsupportedOperation, "search")]
    [InlineData("invalid-arguments:missing mediaId", PluginOperationErrorKind.InvalidArguments, "missing mediaId")]
    [InlineData("failed:network down", PluginOperationErrorKind.Failed, "network down")]
    [InlineData("plain failure", PluginOperationErrorKind.Failed, "plain failure")]
    public void TryParse_ParsesKnownAndFallbackFormats(string input, PluginOperationErrorKind expectedKind, string expectedMessage)
    {
        var ok = PluginOperationError.TryParse(input, out var parsed);

        Assert.True(ok);
        Assert.Equal(expectedKind, parsed.Kind);
        Assert.Equal(expectedMessage, parsed.Message);
    }

    [Fact]
    public void TryParse_ReturnsFalse_ForEmpty()
    {
        var ok = PluginOperationError.TryParse("   ", out var parsed);

        Assert.False(ok);
        Assert.Equal(default, parsed);
    }
}

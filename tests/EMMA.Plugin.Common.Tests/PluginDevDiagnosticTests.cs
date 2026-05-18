using EMMA.Cli;

namespace EMMA.Plugin.Common.Tests;

public sealed class PluginDevDiagnosticTests
{
    [Theory]
    [InlineData("unauthenticated", "auth")]
    [InlineData("timeout", "runtime")]
    [InlineData("upstream_failure", "runtime")]
    [InlineData("invalid_request", "request")]
    [InlineData("not_found", "request")]
    [InlineData("session.host_bridge.auth_token_missing", "host_bridge")]
    [InlineData("plugin:runtime:timeout", "runtime")]
    public void InferType_MapsStructuredCodesToStableCategories(string code, string expectedType)
    {
        Assert.Equal(expectedType, PluginDevDiagnostic.InferType(code));
    }
}
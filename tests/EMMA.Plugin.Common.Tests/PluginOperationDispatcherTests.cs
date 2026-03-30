using EMMA.Plugin.Common;

namespace EMMA.Plugin.Common.Tests;

public class PluginOperationDispatcherTests
{
    [Fact]
    public void Dispatch_ReturnsUnsupportedOperation_WhenMissingHandler()
    {
        var dispatcher = new PluginOperationDispatcher();
        var request = new OperationRequest("unknown", null, null, null, null);

        var result = dispatcher.Dispatch(request);

        Assert.True(result.isError);
        Assert.StartsWith("unsupported-operation:", result.error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dispatch_InvokesRegisteredHandler()
    {
        var dispatcher = new PluginOperationDispatcher()
            .Register("search", _ => new OperationResult(false, null, "application/json", "[]"));

        var result = dispatcher.Dispatch(new OperationRequest("search", null, null, null, null));

        Assert.False(result.isError);
        Assert.Equal("[]", result.payloadJson);
    }

    [Fact]
    public void Dispatch_MapsExceptionsToFailedResult()
    {
        var dispatcher = new PluginOperationDispatcher()
            .Register("search", _ => throw new InvalidOperationException("boom"));

        var result = dispatcher.Dispatch(new OperationRequest("search", null, null, null, null));

        Assert.True(result.isError);
        Assert.Equal("failed:boom", result.error);
    }

    [Fact]
    public void PayloadRouter_UsesProvidedPayloadFirst()
    {
        var router = new PluginOperationPayloadRouter()
            .Register("search", _ => "ignored");

        var request = new OperationRequest("search", null, null, null, "{\"provided\":1}");

        var payload = router.Resolve(request, (_, _) => "{\"fallback\":2}");

        Assert.Equal("{\"provided\":1}", payload);
    }

    [Fact]
    public void PayloadRouter_UsesResolvedHintForFallback()
    {
        var router = new PluginOperationPayloadRouter()
            .Register("search", _ => "hint://search");

        var request = new OperationRequest("search", null, null, null, "");

        var payload = router.Resolve(request, (operation, hint) => $"{{\"op\":\"{operation}\",\"hint\":\"{hint}\"}}");

        Assert.Equal("{\"op\":\"search\",\"hint\":\"hint://search\"}", payload);
    }
}

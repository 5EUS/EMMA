using EMMA.Plugin.Common;

namespace EMMA.Plugin.Common.Tests;

public class PluginTypedExportScaffoldTests
{
    [Fact]
    public void InvokeWithOperationErrorHandling_ReturnsInnerResult_WhenSuccessful()
    {
        var result = PluginTypedExportScaffold.InvokeWithOperationErrorHandling<string, InvalidOperationException>(
            static () => "ok",
            static message => new InvalidOperationException(message));

        Assert.Equal("ok", result);
    }

    [Fact]
    public void InvokeWithOperationErrorHandling_RethrowsKnownOperationErrors()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypedExportScaffold.InvokeWithOperationErrorHandling<string, InvalidOperationException>(
                static () => throw new InvalidOperationException("known"),
                static message => new InvalidOperationException($"wrapped:{message}")));

        Assert.Equal("known", ex.Message);
    }

    [Fact]
    public void InvokeWithOperationErrorHandling_ConvertsUnexpectedExceptions_ToFailedError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypedExportScaffold.InvokeWithOperationErrorHandling<string, InvalidOperationException>(
                static () => throw new ArgumentException("payload resolution failed"),
                static message => new InvalidOperationException($"failed:{message}")));

        Assert.Equal("failed:payload resolution failed", ex.Message);
    }
}
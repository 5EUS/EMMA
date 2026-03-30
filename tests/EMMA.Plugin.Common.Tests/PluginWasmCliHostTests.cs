using System.Text;
using EMMA.Plugin.Common;

namespace EMMA.Plugin.Common.Tests;

public class PluginWasmCliHostTests
{
    [Fact]
    public void Run_WritesJsonAndReturnsZero_WhenOperationSucceeds()
    {
        var output = new StringWriter(new StringBuilder());
        var error = new StringWriter(new StringBuilder());

        var exitCode = PluginWasmCliHost.Run(
            ["search"],
            PluginOperationNames.WasmCliKnownOperations,
            (operation, args, payload) => $"{{\"op\":\"{operation}\"}}",
            output: output,
            error: error);

        Assert.Equal(0, exitCode);
        Assert.Equal("{\"op\":\"search\"}" + Environment.NewLine, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_ReturnsTwo_WhenOperationProducesNoOutput()
    {
        var output = new StringWriter(new StringBuilder());
        var error = new StringWriter(new StringBuilder());

        var exitCode = PluginWasmCliHost.Run(
            ["unknown"],
            PluginOperationNames.WasmCliKnownOperations,
            (_, _, _) => string.Empty,
            output: output,
            error: error);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("Unsupported or invalid operation.", error.ToString(), StringComparison.Ordinal);
    }
}
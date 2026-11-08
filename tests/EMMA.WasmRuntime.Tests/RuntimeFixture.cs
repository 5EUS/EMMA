using EMMA.Domain;

using Xunit;

namespace EMMA.WasmRuntime.Tests;

/// <summary>One native runtime shared by a test class, as in production.</summary>
public sealed class RuntimeFixture : IDisposable
{
    /// <summary>Initializes a new instance of the <see cref="RuntimeFixture"/> class.</summary>
    public RuntimeFixture() => this.Host = WasmRuntimeHost.Create();

    /// <summary>Gets the shared runtime.</summary>
    public WasmRuntimeHost Host { get; }

    /// <inheritdoc/>
    public void Dispose() => this.Host.Dispose();

    /// <summary>Opens a plugin and fails the test when it cannot be opened.</summary>
    /// <param name="config">What to open.</param>
    /// <returns>The plugin; the caller disposes it.</returns>
    internal WasmPlugin Open(WasmPluginConfig config)
    {
        Outcome<WasmPlugin> opened = this.Host.Open(config);
        Assert.True(opened.IsSuccess, opened.Error?.Message);
        return opened.Value;
    }
}

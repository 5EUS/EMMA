using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EMMA.Plugin.Common;

/// <summary>
/// Represents the immutable result of building a WASM host configuration.
/// </summary>
/// <param name="CliHandlers">The CLI handlers keyed by operation name.</param>
/// <param name="InvokeDispatcher">The invoke dispatcher used for structured operations.</param>
public sealed record PluginWasmHostBuildResult(
    IReadOnlyDictionary<string, Func<string[], string, string>> CliHandlers,
    PluginOperationDispatcher InvokeDispatcher);

/// <summary>
/// Builds CLI and invoke dispatch configuration for WASM plugins.
/// </summary>
public sealed class PluginWasmHostBuilder
{
    private readonly Dictionary<string, Func<string[], string, string>> _cliHandlers = new(StringComparer.Ordinal);
    private readonly PluginOperationDispatcher _invokeDispatcher = new();

    /// <summary>
    /// Registers a raw CLI handler for an operation.
    /// </summary>
    /// <param name="operation">The operation name to register.</param>
    /// <param name="handler">The handler that produces CLI output for the operation.</param>
    /// <returns>The current builder instance.</returns>
    public PluginWasmHostBuilder AddCliHandler(string operation, Func<string[], string, string> handler)
    {
        _cliHandlers[operation] = handler;
        return this;
    }

    /// <summary>
    /// Registers a CLI handler that returns a typed result serialized with the supplied JSON type info.
    /// </summary>
    /// <param name="operation">The operation name to register.</param>
    /// <param name="handler">The handler that produces a typed result for the operation.</param>
    /// <param name="typeInfo">The JSON type metadata used to serialize the handler result.</param>
    /// <returns>The current builder instance.</returns>
    public PluginWasmHostBuilder AddCliJson<T>(
        string operation,
        Func<string[], string, T> handler,
        JsonTypeInfo<T> typeInfo)
    {
        _cliHandlers[operation] = (args, payload) => JsonSerializer.Serialize(handler(args, payload), typeInfo);
        return this;
    }

    /// <summary>
    /// Configures the invoke dispatcher used by the WASM host.
    /// </summary>
    /// <param name="configure">The callback that registers invoke handlers on the dispatcher.</param>
    /// <returns>The current builder instance.</returns>
    public PluginWasmHostBuilder ConfigureInvoke(Func<PluginOperationDispatcher, PluginOperationDispatcher> configure)
    {
        configure(_invokeDispatcher);
        return this;
    }

    /// <summary>
    /// Builds the immutable host configuration from the registered handlers.
    /// </summary>
    /// <returns>The completed WASM host build result.</returns>
    public PluginWasmHostBuildResult Build()
    {
        return new PluginWasmHostBuildResult(_cliHandlers, _invokeDispatcher);
    }
}

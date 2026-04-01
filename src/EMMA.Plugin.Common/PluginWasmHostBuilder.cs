using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EMMA.Plugin.Common;

public sealed record PluginWasmHostBuildResult(
    IReadOnlyDictionary<string, Func<string[], string, string>> CliHandlers,
    PluginOperationDispatcher InvokeDispatcher);

public sealed class PluginWasmHostBuilder
{
    private readonly Dictionary<string, Func<string[], string, string>> _cliHandlers = new(StringComparer.Ordinal);
    private readonly PluginOperationDispatcher _invokeDispatcher = new();

    public PluginWasmHostBuilder AddCliHandler(string operation, Func<string[], string, string> handler)
    {
        _cliHandlers[operation] = handler;
        return this;
    }

    public PluginWasmHostBuilder AddCliJson<T>(
        string operation,
        Func<string[], string, T> handler,
        JsonTypeInfo<T> typeInfo)
    {
        _cliHandlers[operation] = (args, payload) => JsonSerializer.Serialize(handler(args, payload), typeInfo);
        return this;
    }

    public PluginWasmHostBuilder ConfigureInvoke(Func<PluginOperationDispatcher, PluginOperationDispatcher> configure)
    {
        configure(_invokeDispatcher);
        return this;
    }

    public PluginWasmHostBuildResult Build()
    {
        return new PluginWasmHostBuildResult(_cliHandlers, _invokeDispatcher);
    }
}

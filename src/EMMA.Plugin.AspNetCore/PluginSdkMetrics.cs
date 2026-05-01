using System.Diagnostics.Metrics;
using EMMA.Plugin.Common;
using Microsoft.Extensions.DependencyInjection;

namespace EMMA.Plugin.AspNetCore;

public interface IPluginSdkMetrics
{
    void RecordRpc(string service, string method, string outcome, double durationMs);
}

public static class PluginSdkMetricsServiceCollectionExtensions
{
    public static IServiceCollection AddEmmaPluginMetrics(this IServiceCollection services, string pluginId)
    {
        ArgumentNullException.ThrowIfNull(services);

        var identity = string.IsNullOrWhiteSpace(pluginId) ? "unknown" : pluginId.Trim();
        services.AddSingleton<IPluginSdkMetrics>(_ => new MeteredPluginSdkMetrics(identity));
        return services;
    }
}

internal sealed class MeteredPluginSdkMetrics : IPluginSdkMetrics, IDisposable
{
    private readonly Meter _meter = new(EmmaTelemetry.PluginSdkMeterName);
    private readonly Counter<long> _rpcRequests;
    private readonly Histogram<double> _rpcDurationMs;
    private readonly string _pluginId;

    public MeteredPluginSdkMetrics(string pluginId)
    {
        _pluginId = string.IsNullOrWhiteSpace(pluginId) ? "unknown" : pluginId.Trim();
        _rpcRequests = _meter.CreateCounter<long>("plugin_rpc_requests_total");
        _rpcDurationMs = _meter.CreateHistogram<double>("plugin_rpc_duration_ms");
    }

    public void RecordRpc(string service, string method, string outcome, double durationMs)
    {
        _rpcRequests.Add(
            1,
            KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.PluginId, _pluginId),
            KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.Service, service),
            KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.Method, method),
            KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.Outcome, outcome));

        _rpcDurationMs.Record(
            durationMs,
            KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.PluginId, _pluginId),
            KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.Service, service),
            KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.Method, method),
            KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.Outcome, outcome));
    }

    public void Dispose()
    {
        _meter.Dispose();
    }
}

internal sealed class NoOpPluginSdkMetrics : IPluginSdkMetrics
{
    public void RecordRpc(string service, string method, string outcome, double durationMs)
    {
    }
}
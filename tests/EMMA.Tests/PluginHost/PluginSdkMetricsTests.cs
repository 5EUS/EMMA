using System.Diagnostics.Metrics;
using EMMA.Plugin.AspNetCore;
using EMMA.Plugin.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EMMA.Tests.PluginHost;

public sealed class PluginSdkMetricsTests
{
    [Fact]
    public void AddEmmaPluginMetrics_EmitsRpcMeasurements()
    {
        var longMeasurements = new List<(string Instrument, long Value, Dictionary<string, object?> Tags)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(instrument.Meter.Name, EmmaTelemetry.PluginSdkMeterName, StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            longMeasurements.Add((instrument.Name, value, ToDictionary(tags)));
        });
        listener.Start();

        var services = new ServiceCollection();
        services.AddEmmaPluginMetrics("demo-plugin");
        using var provider = services.BuildServiceProvider();

        var metrics = provider.GetRequiredService<IPluginSdkMetrics>();
        metrics.RecordRpc("search", "Search", EmmaTelemetry.Outcomes.Ok, 7.0);

        Assert.Contains(longMeasurements, item => item.Instrument == "plugin_rpc_requests_total" && item.Value == 1);
        Assert.Contains(longMeasurements, item => Equals(item.Tags[EmmaTelemetry.Tags.PluginId], "demo-plugin"));
        Assert.Contains(longMeasurements, item => Equals(item.Tags[EmmaTelemetry.Tags.Service], "search"));
        Assert.Contains(longMeasurements, item => Equals(item.Tags[EmmaTelemetry.Tags.Method], "Search"));
    }

    private static Dictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            result[tag.Key] = tag.Value;
        }

        return result;
    }
}

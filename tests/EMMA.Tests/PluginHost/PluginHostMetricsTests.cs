using System.Diagnostics.Metrics;
using EMMA.Plugin.Common;
using EMMA.PluginHost.Services;
using Xunit;

namespace EMMA.Tests.PluginHost;

public sealed class PluginHostMetricsTests
{
    [Fact]
    public void RecordWasmOperation_EmitsCounterAndDurationMeasurements()
    {
        var longMeasurements = new List<(string Instrument, long Value, Dictionary<string, object?> Tags)>();
        var doubleMeasurements = new List<(string Instrument, double Value, Dictionary<string, object?> Tags)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(instrument.Meter.Name, EmmaTelemetry.PluginHostMeterName, StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            longMeasurements.Add((instrument.Name, value, ToDictionary(tags)));
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            doubleMeasurements.Add((instrument.Name, value, ToDictionary(tags)));
        });
        listener.Start();

        using var metrics = new PluginHostMetrics();
        metrics.RecordWasmOperation("demo", "search", EmmaTelemetry.Outcomes.Ok, 12.5);

        Assert.Contains(longMeasurements, item => item.Instrument == "plugin_wasm_operations_total" && item.Value == 1);
        Assert.Contains(doubleMeasurements, item => item.Instrument == "plugin_wasm_operation_duration_ms" && item.Value >= 12.5);
        Assert.Contains(longMeasurements, item => Equals(item.Tags[EmmaTelemetry.Tags.PluginId], "demo"));
        Assert.Contains(longMeasurements, item => Equals(item.Tags[EmmaTelemetry.Tags.Operation], "search"));
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

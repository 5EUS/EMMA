using System.Diagnostics.Metrics;
using EMMA.Plugin.Common;

namespace EMMA.PluginHost.Services;

public sealed class PluginHostMetrics : IDisposable
{
    private readonly Meter _meter = new(EmmaTelemetry.PluginHostMeterName);
    private readonly Counter<long> _budgetChecks;
    private readonly Counter<long> _budgetExceeded;
    private readonly Counter<long> _wasmOperations;
    private readonly Histogram<double> _wasmOperationDurationMs;

    public PluginHostMetrics()
    {
        _budgetChecks = _meter.CreateCounter<long>("plugin_budget_checks_total");
        _budgetExceeded = _meter.CreateCounter<long>("plugin_budget_exceeded_total");
        _wasmOperations = _meter.CreateCounter<long>("plugin_wasm_operations_total");
        _wasmOperationDurationMs = _meter.CreateHistogram<double>("plugin_wasm_operation_duration_ms");
    }

    public void RecordBudgetCheck(string pluginId)
    {
        _budgetChecks.Add(1, KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.PluginId, pluginId));
    }

    public void RecordBudgetExceeded(string pluginId, string reason)
    {
        _budgetExceeded.Add(
            1,
            KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.PluginId, pluginId),
            KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.Reason, reason));
    }

    public void RecordWasmOperation(string pluginId, string operation, string outcome, double durationMs)
    {
        _wasmOperations.Add(
            1,
            KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.PluginId, pluginId),
            KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.Operation, operation),
            KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.Outcome, outcome));

        _wasmOperationDurationMs.Record(
            durationMs,
            KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.PluginId, pluginId),
            KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.Operation, operation),
            KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.Outcome, outcome));
    }

    public void Dispose()
    {
        _meter.Dispose();
    }
}
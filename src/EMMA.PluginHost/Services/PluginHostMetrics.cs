using System.Diagnostics.Metrics;
using EMMA.Plugin.Common;

namespace EMMA.PluginHost.Services;

/// <summary>
/// Emits telemetry counters and histograms for plugin host activity.
/// </summary>
public sealed class PluginHostMetrics : IDisposable
{
    private readonly Meter _meter = new(EmmaTelemetry.PluginHostMeterName);
    private readonly Counter<long> _budgetChecks;
    private readonly Counter<long> _budgetExceeded;
    private readonly Counter<long> _wasmOperations;
    private readonly Histogram<double> _wasmOperationDurationMs;

    /// <summary>
    /// Initializes the plugin host metrics instruments.
    /// </summary>
    public PluginHostMetrics()
    {
        _budgetChecks = _meter.CreateCounter<long>("plugin_budget_checks_total");
        _budgetExceeded = _meter.CreateCounter<long>("plugin_budget_exceeded_total");
        _wasmOperations = _meter.CreateCounter<long>("plugin_wasm_operations_total");
        _wasmOperationDurationMs = _meter.CreateHistogram<double>("plugin_wasm_operation_duration_ms");
    }

    /// <summary>
    /// Records a plugin budget check.
    /// </summary>
    /// <param name="pluginId">The plugin identifier.</param>
    public void RecordBudgetCheck(string pluginId)
    {
        _budgetChecks.Add(1, KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.PluginId, pluginId));
    }

    /// <summary>
    /// Records a plugin budget violation.
    /// </summary>
    /// <param name="pluginId">The plugin identifier.</param>
    /// <param name="reason">The budget violation reason.</param>
    public void RecordBudgetExceeded(string pluginId, string reason)
    {
        _budgetExceeded.Add(
            1,
            KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.PluginId, pluginId),
            KeyValuePair.Create<string, object?>(EmmaTelemetry.Tags.Reason, reason));
    }

    /// <summary>
    /// Records a WASM operation invocation and its duration.
    /// </summary>
    /// <param name="pluginId">The plugin identifier.</param>
    /// <param name="operation">The operation name.</param>
    /// <param name="outcome">The operation outcome.</param>
    /// <param name="durationMs">The operation duration in milliseconds.</param>
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

    /// <summary>
    /// Disposes the underlying telemetry meter.
    /// </summary>
    public void Dispose()
    {
        _meter.Dispose();
    }
}
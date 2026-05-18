namespace EMMA.Plugin.Common;

/// <summary>
/// Declares the operation host and JSON context types exported by a WASM plugin.
/// </summary>
/// <param name="operationHostType">The type that owns the exported WASM operations.</param>
/// <param name="jsonContextType">The source-generated JSON context type used for serialization.</param>
/// <param name="additionalSerializableTypes">Additional types that should be considered part of the exported serialization surface.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PluginWasmExportsAttribute(
    Type operationHostType,
    Type jsonContextType,
    params Type[] additionalSerializableTypes) : Attribute
{
    /// <summary>
    /// Gets the type that owns the exported WASM operations.
    /// </summary>
    public Type OperationHostType { get; } = operationHostType;

    /// <summary>
    /// Gets the source-generated JSON context type used for serialization.
    /// </summary>
    public Type JsonContextType { get; } = jsonContextType;

    /// <summary>
    /// Gets additional serializable types that are part of the export surface.
    /// </summary>
    public IReadOnlyList<Type> AdditionalSerializableTypes { get; } = additionalSerializableTypes;

    /// <summary>
    /// Gets or sets the namespace that should receive the generated standard WIT export bridge.
    /// </summary>
    public string? ExportBridgeNamespace { get; init; }
}
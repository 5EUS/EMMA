namespace EMMA.Plugin.Common;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PluginWasmExportsAttribute(
    Type operationHostType,
    Type jsonContextType,
    params Type[] additionalSerializableTypes) : Attribute
{
    public Type OperationHostType { get; } = operationHostType;

    public Type JsonContextType { get; } = jsonContextType;

    public IReadOnlyList<Type> AdditionalSerializableTypes { get; } = additionalSerializableTypes;
}
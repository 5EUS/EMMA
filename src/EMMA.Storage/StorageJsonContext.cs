using System.Text.Json;
using System.Text.Json.Serialization;

namespace EMMA.Storage;

/// <summary>
/// Source-generated JSON serialization context for Storage types.
/// Required for NativeAOT compatibility where reflection-based serialization is disabled.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(PageAssetStorageCache.PageAssetMetadata))]
public partial class StorageJsonContext : JsonSerializerContext
{
}

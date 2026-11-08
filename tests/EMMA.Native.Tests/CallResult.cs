using System.Text.Json.Nodes;

namespace EMMA.Native.Tests;

/// <summary>The outcome of one library call.</summary>
/// <param name="Status">The ABI status.</param>
/// <param name="Json">The parsed envelope.</param>
/// <param name="Blob">The binary content, when the operation returns any.</param>
internal sealed record CallResult(int Status, JsonNode Json, byte[]? Blob)
{
    /// <summary>Gets the result of a successful operation.</summary>
    public JsonNode Ok => this.Json["ok"] ?? throw new InvalidOperationException($"Expected ok but got {this.Json.ToJsonString()}");

    /// <summary>Gets the error code of a failed operation, or <see langword="null"/> when it succeeded.</summary>
    public string? ErrorCode => this.Json["error"]?["code"]?.GetValue<string>();
}

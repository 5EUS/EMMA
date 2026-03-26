namespace EMMA.Plugin.Common;

public sealed record HandshakeResponse(string version, string message);

public sealed record SearchItem(
    string id,
    string source,
    string title,
    string mediaType,
    string? thumbnailUrl = null,
    string? description = null);

public sealed record CapabilityItem(string name, string[] mediaTypes, string[] operations);

public sealed record ChapterItem(string id, int number, string title);

public sealed record PageItem(string id, int index, string contentUri);

public sealed record OperationRequest(
    string operation,
    string? mediaId,
    string? mediaType,
    string? argsJson,
    string? payloadJson);

public sealed record OperationResult(
    bool isError,
    string? error,
    string contentType,
    string payloadJson)
{
    public static OperationResult Error(string error)
        => new(true, error, "application/problem+json", "");
}

public sealed record BenchmarkResult(int iterations, long checksum, int generatedBytes, long elapsedMs);

public sealed record NetworkBenchmarkResult(string query, int payloadBytes, int itemCount, long elapsedMs);
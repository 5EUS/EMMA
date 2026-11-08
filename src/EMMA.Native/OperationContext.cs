using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using EMMA.Domain;
using EMMA.Runtime;

namespace EMMA.Native;

/// <summary>What an operation can use: the runtime, the user the handle belongs to, its request and a token that fires when the handle is closed.</summary>
/// <param name="Runtime">The assembled EMMA.</param>
/// <param name="User">The user whose progress and library the handle reads and writes.</param>
/// <param name="Request">The UTF-8 JSON request; empty means <c>{}</c>.</param>
/// <param name="CancellationToken">Fires when the handle is being closed.</param>
internal sealed record OperationContext(EmmaRuntime Runtime, UserId User, ReadOnlyMemory<byte> Request, CancellationToken CancellationToken)
{
    private static readonly byte[] EmptyObject = "{}"u8.ToArray();

    /// <summary>Reads the request.</summary>
    /// <typeparam name="T">The request type.</typeparam>
    /// <param name="typeInfo">How to read it.</param>
    /// <returns>The request, or why it is not valid.</returns>
    public Outcome<T> Read<T>(JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            T? value = JsonSerializer.Deserialize(this.Request.IsEmpty ? EmptyObject : this.Request.Span, typeInfo);
            return value is null
                ? Outcome.Failure<T>(ErrorCode.InvalidArgument, "The request is empty.")
                : Outcome.Success(value);
        }
        catch (JsonException)
        {
            return Outcome.Failure<T>(ErrorCode.InvalidArgument, "The request is not valid for this operation.");
        }
    }
}

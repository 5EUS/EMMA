using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using EMMA.Domain;

using Microsoft.Extensions.Primitives;

namespace EMMA.Api.Http;

/// <summary>
/// Reads and validates what a request carries. Every method answers with an <see cref="Outcome{T}"/>, so a bad
/// request is described once and the handler maps it to a response the same way as any other failure.
/// </summary>
internal static class RequestReader
{
    /// <summary>Reads a plugin id from a route value.</summary>
    /// <param name="text">The value.</param>
    /// <returns>The id, or why it is invalid.</returns>
    public static Outcome<PluginId> Plugin(string? text) =>
        PluginId.TryCreate(text, out PluginId id)
            ? Outcome.Success(id)
            : Outcome.Failure<PluginId>(ErrorCode.InvalidArgument, "The plugin id is not valid.");

    /// <summary>Reads exactly one node reference from a query parameter.</summary>
    /// <param name="request">The request.</param>
    /// <param name="name">The parameter name.</param>
    /// <returns>The reference, or why it is missing or invalid.</returns>
    public static Outcome<NodeRef> Node(HttpRequest request, string name)
    {
        StringValues values = request.Query[name];
        return values.Count == 1 && NodeRef.TryParse(values[0], out NodeRef reference)
            ? Outcome.Success(reference)
            : Outcome.Failure<NodeRef>(ErrorCode.InvalidArgument, $"'{name}' must be one node reference such as 'com.example.plugin:abc'.");
    }

    /// <summary>Reads one or more node references from a repeated query parameter.</summary>
    /// <param name="request">The request.</param>
    /// <param name="name">The parameter name.</param>
    /// <returns>The references, or why one is invalid.</returns>
    public static Outcome<IReadOnlyList<NodeRef>> Nodes(HttpRequest request, string name)
    {
        StringValues values = request.Query[name];
        if (values.Count is 0 or > ContractLimits.MaxIdsPerGet)
        {
            return Outcome.Failure<IReadOnlyList<NodeRef>>(
                ErrorCode.InvalidArgument,
                $"Give between 1 and {ContractLimits.MaxIdsPerGet} '{name}' parameters.");
        }

        List<NodeRef> references = [];
        foreach (string? value in values)
        {
            if (!NodeRef.TryParse(value, out NodeRef reference))
            {
                return Outcome.Failure<IReadOnlyList<NodeRef>>(ErrorCode.InvalidArgument, $"'{name}' holds an invalid node reference.");
            }

            references.Add(reference);
        }

        return Outcome.Success<IReadOnlyList<NodeRef>>(references);
    }

    /// <summary>Reads the <c>cursor</c> and <c>limit</c> parameters.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The page request, or why a parameter is invalid.</returns>
    public static Outcome<PageRequest> Page(HttpRequest request)
    {
        string? cursor = request.Query["cursor"].Count == 1 ? request.Query["cursor"][0] : null;
        if (request.Query["cursor"].Count > 1 || (cursor is not null && !ValidCursor(cursor)))
        {
            return Outcome.Failure<PageRequest>(ErrorCode.InvalidArgument, "'cursor' is not valid.");
        }

        Outcome<int?> limit = Limit(request, ContractLimits.MaxNodesPerPage);
        if (!limit.IsSuccess)
        {
            return Outcome.Failure<PageRequest>(limit.Error);
        }

        return Outcome.Success(new PageRequest(string.IsNullOrEmpty(cursor) ? null : cursor, limit.Value));
    }

    /// <summary>Reads the optional <c>limit</c> parameter.</summary>
    /// <param name="request">The request.</param>
    /// <param name="max">The largest accepted value.</param>
    /// <returns>The limit, <see langword="null"/> when absent, or why it is invalid.</returns>
    public static Outcome<int?> Limit(HttpRequest request, int max)
    {
        StringValues values = request.Query["limit"];
        if (values.Count == 0)
        {
            return Outcome.Success<int?>(null);
        }

        if (values.Count == 1
            && int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out int limit)
            && limit >= 1
            && limit <= max)
        {
            return Outcome.Success<int?>(limit);
        }

        return Outcome.Failure<int?>(ErrorCode.InvalidArgument, $"'limit' must be a whole number from 1 to {max}.");
    }

    /// <summary>Reads exactly one value of a required query parameter.</summary>
    /// <param name="request">The request.</param>
    /// <param name="name">The parameter name.</param>
    /// <returns>The value, or why it is missing.</returns>
    public static Outcome<string> Required(HttpRequest request, string name)
    {
        StringValues values = request.Query[name];
        return values.Count == 1 && !string.IsNullOrWhiteSpace(values[0])
            ? Outcome.Success(values[0]!)
            : Outcome.Failure<string>(ErrorCode.InvalidArgument, $"'{name}' is required.");
    }

    /// <summary>Reads a JSON request body.</summary>
    /// <typeparam name="T">The body type.</typeparam>
    /// <param name="request">The request.</param>
    /// <param name="typeInfo">How to read the body.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The body, or why it could not be read. The size limit is enforced by the server.</returns>
    public static async Task<Outcome<T>> BodyAsync<T>(HttpRequest request, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        if (!request.HasJsonContentType())
        {
            return Outcome.Failure<T>(ErrorCode.InvalidArgument, "The body must be application/json.");
        }

        try
        {
            T? body = await request.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false);
            return body is null
                ? Outcome.Failure<T>(ErrorCode.InvalidArgument, "The body is empty.")
                : Outcome.Success(body);
        }
        catch (JsonException)
        {
            return Outcome.Failure<T>(ErrorCode.InvalidArgument, "The body is not valid for this request.");
        }
        catch (BadHttpRequestException)
        {
            return Outcome.Failure<T>(ErrorCode.InvalidArgument, "The body is too large or could not be read.");
        }
    }

    private static bool ValidCursor(string cursor) => Encoding.UTF8.GetByteCount(cursor) <= ContractLimits.MaxCursorBytes;
}

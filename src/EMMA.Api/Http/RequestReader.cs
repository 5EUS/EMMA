using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using EMMA.Api.Contracts;
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
    public static Outcome<PluginId> Plugin(string? text) => RequestParsing.Plugin(text);

    /// <summary>Reads exactly one node reference from a query parameter.</summary>
    /// <param name="request">The request.</param>
    /// <param name="name">The parameter name.</param>
    /// <returns>The reference, or why it is missing or invalid.</returns>
    public static Outcome<NodeRef> Node(HttpRequest request, string name)
    {
        StringValues values = request.Query[name];
        return RequestParsing.Node(values.Count == 1 ? values[0] : null, name);
    }

    /// <summary>Reads one or more node references from a repeated query parameter.</summary>
    /// <param name="request">The request.</param>
    /// <param name="name">The parameter name.</param>
    /// <returns>The references, or why one is invalid.</returns>
    public static Outcome<IReadOnlyList<NodeRef>> Nodes(HttpRequest request, string name) =>
        RequestParsing.Nodes([.. request.Query[name]], name);

    /// <summary>Reads the <c>cursor</c> and <c>limit</c> parameters.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The page request, or why a parameter is invalid.</returns>
    public static Outcome<PageRequest> Page(HttpRequest request)
    {
        StringValues cursor = request.Query["cursor"];
        Outcome<int?> limit = Limit(request, ContractLimits.MaxNodesPerPage);
        if (cursor.Count > 1 || !limit.IsSuccess)
        {
            return Outcome.Failure<PageRequest>(limit.Error ?? new PluginError(ErrorCode.InvalidArgument, "'cursor' is not valid."));
        }

        return RequestParsing.Page(cursor.Count == 1 ? cursor[0] : null, limit.Value);
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
}

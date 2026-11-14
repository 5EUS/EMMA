using System.Globalization;
using System.Net.Http.Headers;
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
    private const int MaxAssetIndex = 1023;

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

    /// <summary>Reads an optional query parameter that may appear at most once.</summary>
    /// <param name="request">The request.</param>
    /// <param name="name">The parameter name.</param>
    /// <returns>The value, <see langword="null"/> when absent, or why it is repeated.</returns>
    public static Outcome<string?> Optional(HttpRequest request, string name)
    {
        StringValues values = request.Query[name];
        if (values.Count > 1)
        {
            return Outcome.Failure<string?>(ErrorCode.InvalidArgument, $"'{name}' may be given only once.");
        }

        return Outcome.Success<string?>(values.Count == 0 ? null : values[0]);
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

    /// <summary>Reads the optional <c>hint</c> parameter: a quality preference passed to the plugin.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The hint, <see langword="null"/> when absent, or why it is invalid.</returns>
    public static Outcome<string?> Hint(HttpRequest request)
    {
        Outcome<string?> hint = Optional(request, "hint");
        return hint.IsSuccess ? RequestParsing.Hint(hint.Value) : hint;
    }

    /// <summary>Reads the optional <c>index</c> parameter: which of the resolved assets to use.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The index, zero when absent, or why it is invalid.</returns>
    public static Outcome<int> Index(HttpRequest request)
    {
        StringValues values = request.Query["index"];
        if (values.Count == 0)
        {
            return Outcome.Success(0);
        }

        return values.Count == 1
            && int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            && index <= MaxAssetIndex
            ? Outcome.Success(index)
            : Outcome.Failure<int>(ErrorCode.InvalidArgument, $"'index' must be a whole number from 0 to {MaxAssetIndex}.");
    }

    /// <summary>Reads a single <c>bytes=start-</c> or <c>bytes=start-end</c> range; anything else is ignored and the whole asset is sent.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The range, or <see langword="null"/> when there is none this API honours.</returns>
    public static ByteRange? Range(HttpRequest request)
    {
        if (!RangeHeaderValue.TryParse(request.Headers.Range.ToString(), out RangeHeaderValue? header)
            || !string.Equals(header.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
            || header.Ranges.Count != 1)
        {
            return null;
        }

        RangeItemHeaderValue item = header.Ranges.Single();
        if (item.From is not { } start || (item.To is { } end && end < start))
        {
            return null;
        }

        return new ByteRange(start, item.To is { } last ? last - start + 1 : null);
    }
}

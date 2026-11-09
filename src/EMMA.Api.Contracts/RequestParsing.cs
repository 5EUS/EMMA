using System.Text;

using EMMA.Domain;

namespace EMMA.Api.Contracts;

/// <summary>
/// Validates the pieces of a request that every transport carries the same way: plugin ids, node references,
/// roles and paging. The HTTP API reads them from a query string and the native library from JSON; both end up
/// here, so a value is accepted or refused identically on either.
/// </summary>
public static class RequestParsing
{
    /// <summary>Reads a plugin id.</summary>
    /// <param name="text">The id text.</param>
    /// <returns>The id, or why it is invalid.</returns>
    public static Outcome<PluginId> Plugin(string? text) =>
        PluginId.TryCreate(text, out PluginId id)
            ? Outcome.Success(id)
            : Outcome.Failure<PluginId>(ErrorCode.InvalidArgument, "The plugin id is not valid.");

    /// <summary>Reads one node reference.</summary>
    /// <param name="text">The reference text, <c>plugin.id:local-id</c>.</param>
    /// <param name="name">The parameter name, for the error message.</param>
    /// <returns>The reference, or why it is invalid.</returns>
    public static Outcome<NodeRef> Node(string? text, string name) =>
        NodeRef.TryParse(text, out NodeRef reference)
            ? Outcome.Success(reference)
            : Outcome.Failure<NodeRef>(ErrorCode.InvalidArgument, $"'{name}' must be one node reference such as 'com.example.plugin:abc'.");

    /// <summary>Reads between one and <see cref="ContractLimits.MaxIdsPerGet"/> node references.</summary>
    /// <param name="texts">The reference texts.</param>
    /// <param name="name">The parameter name, for the error message.</param>
    /// <returns>The references, or why one is invalid.</returns>
    public static Outcome<IReadOnlyList<NodeRef>> Nodes(IReadOnlyList<string?>? texts, string name)
    {
        if (texts is null || texts.Count is 0 or > ContractLimits.MaxIdsPerGet)
        {
            return Outcome.Failure<IReadOnlyList<NodeRef>>(
                ErrorCode.InvalidArgument,
                $"Give between 1 and {ContractLimits.MaxIdsPerGet} '{name}' values.");
        }

        List<NodeRef> references = [];
        foreach (string? text in texts)
        {
            if (!NodeRef.TryParse(text, out NodeRef reference))
            {
                return Outcome.Failure<IReadOnlyList<NodeRef>>(ErrorCode.InvalidArgument, $"'{name}' holds an invalid node reference.");
            }

            references.Add(reference);
        }

        return Outcome.Success<IReadOnlyList<NodeRef>>(references);
    }

    /// <summary>Reads an asset role.</summary>
    /// <param name="text">The role text.</param>
    /// <returns>The role, or why it is invalid.</returns>
    public static Outcome<AssetRole> Role(string? text) =>
        AssetRole.TryCreate(text, out AssetRole role)
            ? Outcome.Success(role)
            : Outcome.Failure<AssetRole>(ErrorCode.InvalidArgument, "'role' is not a valid asset role.");

    /// <summary>Reads an optional asset hint, such as a quality.</summary>
    /// <param name="text">The hint text, or <see langword="null"/> for none.</param>
    /// <returns>The hint, or why it is invalid.</returns>
    public static Outcome<string?> Hint(string? text) =>
        AssetHint.IsValid(text)
            ? Outcome.Success(text)
            : Outcome.Failure<string?>(ErrorCode.InvalidArgument, $"'hint' must be 1 to {AssetHint.MaxLength} characters without control characters.");

    /// <summary>Checks a page-size hint.</summary>
    /// <param name="limit">The hint, if any.</param>
    /// <param name="max">The largest accepted value.</param>
    /// <returns>The hint, or why it is out of range.</returns>
    public static Outcome<int?> Limit(int? limit, int max) =>
        limit is null || (limit >= 1 && limit <= max)
            ? Outcome.Success(limit)
            : Outcome.Failure<int?>(ErrorCode.InvalidArgument, $"'limit' must be a whole number from 1 to {max}.");

    /// <summary>Reads a cursor and page-size hint.</summary>
    /// <param name="cursor">The cursor from a previous page, or <see langword="null"/> or empty for the first.</param>
    /// <param name="limit">The page-size hint, if any.</param>
    /// <returns>The page request, or why a value is invalid.</returns>
    public static Outcome<PageRequest> Page(string? cursor, int? limit)
    {
        if (cursor is not null && Encoding.UTF8.GetByteCount(cursor) > ContractLimits.MaxCursorBytes)
        {
            return Outcome.Failure<PageRequest>(ErrorCode.InvalidArgument, "'cursor' is not valid.");
        }

        Outcome<int?> checkedLimit = Limit(limit, ContractLimits.MaxNodesPerPage);
        if (!checkedLimit.IsSuccess)
        {
            return Outcome.Failure<PageRequest>(checkedLimit.Error);
        }

        string? first = string.IsNullOrEmpty(cursor) ? null : cursor;
        return Outcome.Success(new PageRequest(first, checkedLimit.Value));
    }
}

using EMMA.Api.Contracts;
using EMMA.Api.Mapping;
using EMMA.Domain;
using EMMA.Native.Requests;

namespace EMMA.Native.Operations;

/// <summary>The handle's user's libraries and the nodes saved in them. A handle belongs to one user, so there is no way to name another.</summary>
internal static class LibraryOperations
{
    private const int DefaultSearch = 50;
    private const int MaxSearch = 200;

    /// <summary>Lists the user's saved nodes, in one library or all.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> ListAsync(OperationContext context)
    {
        Outcome<LibraryListCall> call = context.Read(NativeJsonContext.Default.LibraryListCall);
        if (!call.IsSuccess)
        {
            return Envelope.Failure(call.Error);
        }

        return Entries(await context.Runtime.Library.ListAsync(context.User, call.Value.Library, context.CancellationToken).ConfigureAwait(false));
    }

    /// <summary>Finds saved nodes by title without asking any plugin.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> SearchAsync(OperationContext context)
    {
        Outcome<LibrarySearchCall> call = context.Read(NativeJsonContext.Default.LibrarySearchCall);
        Outcome<int?> limit = call.IsSuccess ? RequestParsing.Limit(call.Value.Limit, MaxSearch) : Outcome.Failure<int?>(call.Error);
        if (!limit.IsSuccess)
        {
            return Envelope.Failure(limit.Error);
        }

        return Entries(await context.Runtime.Library
            .SearchAsync(context.User, call.Value.Text, call.Value.Library, limit.Value ?? DefaultSearch, context.CancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>Saves a node the host has already served.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> AddAsync(OperationContext context)
    {
        Outcome<LibraryRefCall> call = context.Read(NativeJsonContext.Default.LibraryRefCall);
        Outcome<NodeRef> node = call.IsSuccess ? RequestParsing.Node(call.Value.Ref, "ref") : Outcome.Failure<NodeRef>(call.Error);
        if (!node.IsSuccess)
        {
            return Envelope.Failure(node.Error);
        }

        Outcome<bool> added = await context.Runtime.Library.AddAsync(context.User, node.Value, call.Value.Library, context.CancellationToken).ConfigureAwait(false);
        return added.IsSuccess ? Envelope.Done : Envelope.Failure(added.Error);
    }

    /// <summary>Removes a saved node from whichever library holds it.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> RemoveAsync(OperationContext context)
    {
        Outcome<RefCall> call = context.Read(NativeJsonContext.Default.RefCall);
        Outcome<NodeRef> node = call.IsSuccess ? RequestParsing.Node(call.Value.Ref, "ref") : Outcome.Failure<NodeRef>(call.Error);
        if (!node.IsSuccess)
        {
            return Envelope.Failure(node.Error);
        }

        return await context.Runtime.Library.RemoveAsync(context.User, node.Value, context.CancellationToken).ConfigureAwait(false)
            ? Envelope.Done
            : Envelope.Failure(ErrorCode.NotFound, "That node is not in the library.");
    }

    /// <summary>Moves a saved node to another library.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> MoveAsync(OperationContext context)
    {
        Outcome<LibraryRefCall> call = context.Read(NativeJsonContext.Default.LibraryRefCall);
        Outcome<NodeRef> node = call.IsSuccess ? RequestParsing.Node(call.Value.Ref, "ref") : Outcome.Failure<NodeRef>(call.Error);
        if (!node.IsSuccess)
        {
            return Envelope.Failure(node.Error);
        }

        if (call.Value.Library is null)
        {
            return Envelope.Failure(ErrorCode.InvalidArgument, "'library' is required.");
        }

        Outcome<bool> moved = await context.Runtime.Library.MoveAsync(context.User, node.Value, call.Value.Library, context.CancellationToken).ConfigureAwait(false);
        return moved.IsSuccess ? Envelope.Done : Envelope.Failure(moved.Error);
    }

    /// <summary>Lists the children of every saved node again and reports the new ones.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> RefreshAsync(OperationContext context)
    {
        Outcome<LibraryListCall> call = context.Read(NativeJsonContext.Default.LibraryListCall);
        if (!call.IsSuccess)
        {
            return Envelope.Failure(call.Error);
        }

        Outcome<RefreshReport> report = await context.Runtime.Refresh.RefreshLibraryAsync(context.User, call.Value.Library, context.CancellationToken).ConfigureAwait(false);
        return report.IsSuccess
            ? Envelope.Ok(NodeMapper.ToDto(report.Value), ApiJsonContext.Default.RefreshResponse)
            : Envelope.Failure(report.Error);
    }

    /// <summary>Lists the user's libraries.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> LibrariesAsync(OperationContext context)
    {
        IReadOnlyList<LibrarySummary> libraries = await context.Runtime.Library.LibrariesAsync(context.User, context.CancellationToken).ConfigureAwait(false);
        return Envelope.Ok(new LibrariesResponse([.. libraries.Select(UserMapper.ToDto)]), ApiJsonContext.Default.LibrariesResponse);
    }

    /// <summary>Creates a library.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> CreateAsync(OperationContext context)
    {
        Outcome<LibraryNameCall> call = context.Read(NativeJsonContext.Default.LibraryNameCall);
        if (!call.IsSuccess)
        {
            return Envelope.Failure(call.Error);
        }

        Outcome<LibrarySummary> created = await context.Runtime.Library.CreateAsync(context.User, call.Value.Name, context.CancellationToken).ConfigureAwait(false);
        return created.IsSuccess
            ? Envelope.Ok(UserMapper.ToDto(created.Value), ApiJsonContext.Default.LibrarySummaryDto)
            : Envelope.Failure(created.Error);
    }

    /// <summary>Deletes a library and the entries in it.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> DeleteAsync(OperationContext context)
    {
        Outcome<LibraryNameCall> call = context.Read(NativeJsonContext.Default.LibraryNameCall);
        if (!call.IsSuccess)
        {
            return Envelope.Failure(call.Error);
        }

        Outcome<bool> deleted = await context.Runtime.Library.DeleteAsync(context.User, call.Value.Name, context.CancellationToken).ConfigureAwait(false);
        return deleted.IsSuccess ? Envelope.Done : Envelope.Failure(deleted.Error);
    }

    private static NativeResponse Entries(Outcome<IReadOnlyList<LibraryEntry>> entries) => entries.IsSuccess
        ? Envelope.Ok(new LibraryResponse([.. entries.Value.Select(UserMapper.ToDto)]), ApiJsonContext.Default.LibraryResponse)
        : Envelope.Failure(entries.Error);
}

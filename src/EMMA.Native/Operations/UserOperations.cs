using EMMA.Api.Contracts;
using EMMA.Api.Mapping;
using EMMA.Domain;
using EMMA.Native.Requests;

namespace EMMA.Native.Operations;

/// <summary>The handle's user's own data: progress and library. A handle belongs to one user, so there is no way to name another.</summary>
internal static class UserOperations
{
    private const int DefaultRecent = 50;
    private const int MaxRecent = 200;

    /// <summary>Records where the user is within a node.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> SetProgressAsync(OperationContext context)
    {
        Outcome<ProgressCall> call = context.Read(NativeJsonContext.Default.ProgressCall);
        Outcome<NodeRef> node = call.IsSuccess ? RequestParsing.Node(call.Value.Ref, "ref") : Outcome.Failure<NodeRef>(call.Error);
        if (!node.IsSuccess)
        {
            return Envelope.Failure(node.Error);
        }

        if (call.Value.Unit is null || call.Value.Value is null)
        {
            return Envelope.Failure(ErrorCode.InvalidArgument, "'unit' and 'value' are required.");
        }

        ProgressLocator locator = new(call.Value.Unit, call.Value.Value.Value, call.Value.Total);
        Outcome<Progress> stored = await context.Runtime.Progress
            .SetAsync(context.User, node.Value, locator, call.Value.Completed ?? false, context.CancellationToken)
            .ConfigureAwait(false);
        return stored.IsSuccess
            ? Envelope.Ok(UserMapper.ToDto(stored.Value), ApiJsonContext.Default.ProgressDto)
            : Envelope.Failure(stored.Error);
    }

    /// <summary>Gets the user's progress in a node.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> GetProgressAsync(OperationContext context)
    {
        Outcome<NodeRef> node = ReadRef(context);
        return node.IsSuccess
            ? Found(await context.Runtime.Progress.GetAsync(context.User, node.Value, context.CancellationToken).ConfigureAwait(false))
            : Envelope.Failure(node.Error);
    }

    /// <summary>Gets the latest progress anywhere beneath a container.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> ContinueAsync(OperationContext context)
    {
        Outcome<NodeRef> node = ReadRef(context);
        return node.IsSuccess
            ? Found(await context.Runtime.Progress.ContinueAsync(context.User, node.Value, context.CancellationToken).ConfigureAwait(false))
            : Envelope.Failure(node.Error);
    }

    /// <summary>Lists the user's recent progress.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> RecentAsync(OperationContext context)
    {
        Outcome<RecentCall> call = context.Read(NativeJsonContext.Default.RecentCall);
        Outcome<int?> limit = call.IsSuccess ? RequestParsing.Limit(call.Value.Limit, MaxRecent) : Outcome.Failure<int?>(call.Error);
        if (!limit.IsSuccess)
        {
            return Envelope.Failure(limit.Error);
        }

        IReadOnlyList<Progress> recent = await context.Runtime.Progress
            .RecentAsync(context.User, limit.Value ?? DefaultRecent, context.CancellationToken)
            .ConfigureAwait(false);
        return Envelope.Ok(new ProgressListResponse([.. recent.Select(UserMapper.ToDto)]), ApiJsonContext.Default.ProgressListResponse);
    }

    /// <summary>Lists the user's saved nodes.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> LibraryListAsync(OperationContext context)
    {
        IReadOnlyList<LibraryEntry> entries = await context.Runtime.Library.ListAsync(context.User, context.CancellationToken).ConfigureAwait(false);
        return Envelope.Ok(new LibraryResponse([.. entries.Select(UserMapper.ToDto)]), ApiJsonContext.Default.LibraryResponse);
    }

    /// <summary>Saves a node the host has already served.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> LibraryAddAsync(OperationContext context)
    {
        Outcome<NodeRef> node = ReadRef(context);
        if (!node.IsSuccess)
        {
            return Envelope.Failure(node.Error);
        }

        Outcome<bool> added = await context.Runtime.AddToLibraryAsync(context.User, node.Value, context.CancellationToken).ConfigureAwait(false);
        return added.IsSuccess ? Envelope.Done : Envelope.Failure(added.Error);
    }

    /// <summary>Removes a saved node.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> LibraryRemoveAsync(OperationContext context)
    {
        Outcome<NodeRef> node = ReadRef(context);
        if (!node.IsSuccess)
        {
            return Envelope.Failure(node.Error);
        }

        return await context.Runtime.Library.RemoveAsync(context.User, node.Value, context.CancellationToken).ConfigureAwait(false)
            ? Envelope.Done
            : Envelope.Failure(ErrorCode.NotFound, "That node is not in the library.");
    }

    private static Outcome<NodeRef> ReadRef(OperationContext context)
    {
        Outcome<RefCall> call = context.Read(NativeJsonContext.Default.RefCall);
        return call.IsSuccess ? RequestParsing.Node(call.Value.Ref, "ref") : Outcome.Failure<NodeRef>(call.Error);
    }

    private static NativeResponse Found(Progress? progress) => progress is null
        ? Envelope.Failure(ErrorCode.NotFound, "There is no progress recorded there.")
        : Envelope.Ok(UserMapper.ToDto(progress), ApiJsonContext.Default.ProgressDto);
}

using EMMA.Api.Contracts;
using EMMA.Api.Mapping;
using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.Native.Requests;

namespace EMMA.Native.Operations;

/// <summary>The handle's user's own progress. A handle belongs to one user, so there is no way to name another.</summary>
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

    /// <summary>Lists the user's recent activity, one entry per series.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> HistoryAsync(OperationContext context)
    {
        Outcome<RecentCall> call = context.Read(NativeJsonContext.Default.RecentCall);
        Outcome<int?> limit = call.IsSuccess ? RequestParsing.Limit(call.Value.Limit, MaxRecent) : Outcome.Failure<int?>(call.Error);
        if (!limit.IsSuccess)
        {
            return Envelope.Failure(limit.Error);
        }

        IReadOnlyList<ProgressGroup> groups = await context.Runtime.Progress
            .HistoryAsync(context.User, limit.Value ?? DefaultRecent, context.CancellationToken)
            .ConfigureAwait(false);
        return Envelope.Ok(new ProgressHistoryResponse([.. groups.Select(UserMapper.ToDto)]), ApiJsonContext.Default.ProgressHistoryResponse);
    }

    /// <summary>Gets what the user did beneath one container.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> SeriesAsync(OperationContext context)
    {
        Outcome<NodeRef> node = ReadRef(context);
        if (!node.IsSuccess)
        {
            return Envelope.Failure(node.Error);
        }

        SeriesProgress series = await context.Runtime.Progress.SeriesAsync(context.User, node.Value, context.CancellationToken).ConfigureAwait(false);
        return Envelope.Ok(UserMapper.ToDto(series), ApiJsonContext.Default.SeriesProgressDto);
    }

    /// <summary>Deletes the progress of nodes and of everything beneath them.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> DeleteAsync(OperationContext context)
    {
        Outcome<IReadOnlyList<NodeRef>> nodes = ReadRefs(context);
        if (!nodes.IsSuccess)
        {
            return Envelope.Failure(nodes.Error);
        }

        int removed = await context.Runtime.Progress.DeleteAsync(context.User, nodes.Value, context.CancellationToken).ConfigureAwait(false);
        return Envelope.Ok(new RemovedResponse(removed), ApiJsonContext.Default.RemovedResponse);
    }

    /// <summary>Deletes all of the user's progress.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> ClearAsync(OperationContext context)
    {
        int removed = await context.Runtime.Progress.ClearAsync(context.User, context.CancellationToken).ConfigureAwait(false);
        return Envelope.Ok(new RemovedResponse(removed), ApiJsonContext.Default.RemovedResponse);
    }

    /// <summary>Marks nodes finished.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> CompleteAsync(OperationContext context)
    {
        Outcome<IReadOnlyList<NodeRef>> nodes = ReadRefs(context);
        if (!nodes.IsSuccess)
        {
            return Envelope.Failure(nodes.Error);
        }

        IReadOnlyList<Progress> stored = await context.Runtime.Progress.CompleteAsync(context.User, nodes.Value, context.CancellationToken).ConfigureAwait(false);
        return Envelope.Ok(new ProgressListResponse([.. stored.Select(UserMapper.ToDto)]), ApiJsonContext.Default.ProgressListResponse);
    }

    /// <summary>Gets how long the user's history is kept.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> RetentionGetAsync(OperationContext context)
    {
        int days = await context.Runtime.Progress.GetRetentionAsync(context.User, context.CancellationToken).ConfigureAwait(false);
        return Envelope.Ok(new RetentionResponse(days, null), ApiJsonContext.Default.RetentionResponse);
    }

    /// <summary>Sets how long the user's history is kept.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> RetentionSetAsync(OperationContext context)
    {
        Outcome<RetentionCall> call = context.Read(NativeJsonContext.Default.RetentionCall);
        if (!call.IsSuccess)
        {
            return Envelope.Failure(call.Error);
        }

        if (call.Value.Days is not { } days)
        {
            return Envelope.Failure(ErrorCode.InvalidArgument, "'days' is required.");
        }

        Outcome<HistoryRetention> set = await context.Runtime.Progress.SetRetentionAsync(context.User, days, context.CancellationToken).ConfigureAwait(false);
        return set.IsSuccess
            ? Envelope.Ok(new RetentionResponse(set.Value.Days, set.Value.Removed), ApiJsonContext.Default.RetentionResponse)
            : Envelope.Failure(set.Error);
    }

    private static Outcome<IReadOnlyList<NodeRef>> ReadRefs(OperationContext context)
    {
        Outcome<RefsCall> call = context.Read(NativeJsonContext.Default.RefsCall);
        return call.IsSuccess
            ? RequestParsing.Nodes(call.Value.Refs, "refs")
            : Outcome.Failure<IReadOnlyList<NodeRef>>(call.Error);
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

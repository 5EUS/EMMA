using EMMA.Api.Contracts;
using EMMA.Api.Mapping;
using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.Native.Requests;

namespace EMMA.Native.Operations;

/// <summary>
/// The download queue. Downloads belong to the host, not to the handle's user: everything the handle's runtime
/// has queued is visible and controllable, and the stored bytes are what <c>assets.content</c> serves first.
/// </summary>
internal static class DownloadOperations
{
    private const int DefaultList = 200;
    private const int MaxList = 1000;

    /// <summary>Queues a download, or returns the one already there for that node and role.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> EnqueueAsync(OperationContext context)
    {
        Outcome<DownloadEnqueueCall> call = context.Read(NativeJsonContext.Default.DownloadEnqueueCall);
        if (!call.IsSuccess)
        {
            return Envelope.Failure(call.Error);
        }

        Outcome<NodeRef> node = RequestParsing.Node(call.Value.Ref, "ref");
        Outcome<AssetRole> role = RequestParsing.Role(call.Value.Role);
        Outcome<string?> hint = RequestParsing.Hint(call.Value.Hint);
        if (!node.IsSuccess || !role.IsSuccess || !hint.IsSuccess)
        {
            return Envelope.Failure((node.Error ?? role.Error ?? hint.Error)!);
        }

        return Job(await context.Runtime.Downloads.EnqueueAsync(node.Value, role.Value, hint.Value, context.CancellationToken).ConfigureAwait(false));
    }

    /// <summary>Lists downloads, the most recently changed first.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> ListAsync(OperationContext context)
    {
        Outcome<DownloadListCall> call = context.Read(NativeJsonContext.Default.DownloadListCall);
        Outcome<IReadOnlyCollection<DownloadState>?> states = call.IsSuccess
            ? DownloadMapper.ParseStates(call.Value.States)
            : Outcome.Failure<IReadOnlyCollection<DownloadState>?>(call.Error);
        Outcome<int?> limit = call.IsSuccess ? RequestParsing.Limit(call.Value.Limit, MaxList) : Outcome.Failure<int?>(call.Error);
        if (!states.IsSuccess || !limit.IsSuccess)
        {
            return Envelope.Failure((states.Error ?? limit.Error)!);
        }

        IReadOnlyList<DownloadJob> jobs = await context.Runtime.Downloads.ListAsync(states.Value, limit.Value ?? DefaultList, context.CancellationToken).ConfigureAwait(false);
        return Envelope.Ok(new DownloadListResponse([.. jobs.Select(DownloadMapper.ToDto)]), ApiJsonContext.Default.DownloadListResponse);
    }

    /// <summary>Gets a download and its leaves.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> GetAsync(OperationContext context)
    {
        Outcome<Guid> id = ReadId(context);
        if (!id.IsSuccess)
        {
            return Envelope.Failure(id.Error);
        }

        Outcome<DownloadDetail> detail = await context.Runtime.Downloads.GetAsync(id.Value, context.CancellationToken).ConfigureAwait(false);
        return detail.IsSuccess
            ? Envelope.Ok(
                new DownloadDetailResponse(DownloadMapper.ToDto(detail.Value.Job), [.. detail.Value.Items.Select(DownloadMapper.ToDto)]),
                ApiJsonContext.Default.DownloadDetailResponse)
            : Envelope.Failure(detail.Error);
    }

    /// <summary>Pauses a queued or running download.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static Task<NativeResponse> PauseAsync(OperationContext context) =>
        ChangeAsync(context, (downloads, id, token) => downloads.PauseAsync(id, token));

    /// <summary>Queues a paused download again.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static Task<NativeResponse> ResumeAsync(OperationContext context) =>
        ChangeAsync(context, (downloads, id, token) => downloads.ResumeAsync(id, token));

    /// <summary>Queues a failed or cancelled download again.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static Task<NativeResponse> RetryAsync(OperationContext context) =>
        ChangeAsync(context, (downloads, id, token) => downloads.RetryAsync(id, token));

    /// <summary>Cancels a queued, running or paused download.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static Task<NativeResponse> CancelAsync(OperationContext context) =>
        ChangeAsync(context, (downloads, id, token) => downloads.CancelAsync(id, token));

    /// <summary>Deletes a download and the files nothing else uses.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> DeleteAsync(OperationContext context)
    {
        Outcome<Guid> id = ReadId(context);
        if (!id.IsSuccess)
        {
            return Envelope.Failure(id.Error);
        }

        return await context.Runtime.Downloads.DeleteAsync(id.Value, context.CancellationToken).ConfigureAwait(false)
            ? Envelope.Done
            : Envelope.Failure(ErrorCode.NotFound, "That download does not exist.");
    }

    /// <summary>Deletes every download in the given states.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> ClearAsync(OperationContext context)
    {
        Outcome<DownloadStatesCall> call = context.Read(NativeJsonContext.Default.DownloadStatesCall);
        Outcome<IReadOnlyCollection<DownloadState>?> states = call.IsSuccess
            ? DownloadMapper.ParseStates(call.Value.States)
            : Outcome.Failure<IReadOnlyCollection<DownloadState>?>(call.Error);
        if (!states.IsSuccess)
        {
            return Envelope.Failure(states.Error);
        }

        Outcome<int> cleared = await context.Runtime.Downloads.ClearAsync(states.Value ?? [], context.CancellationToken).ConfigureAwait(false);
        return cleared.IsSuccess
            ? Envelope.Ok(new DownloadCountResponse(cleared.Value), ApiJsonContext.Default.DownloadCountResponse)
            : Envelope.Failure(cleared.Error);
    }

    /// <summary>Queues every failed download again.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> RetryFailedAsync(OperationContext context) =>
        Envelope.Ok(
            new DownloadCountResponse(await context.Runtime.Downloads.RetryFailedAsync(context.CancellationToken).ConfigureAwait(false)),
            ApiJsonContext.Default.DownloadCountResponse);

    /// <summary>Gets how the download executor is set up.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> SettingsGetAsync(OperationContext context) =>
        Envelope.Ok(
            new DownloadSettingsResponse(await context.Runtime.Downloads.GetParallelAsync(context.CancellationToken).ConfigureAwait(false)),
            ApiJsonContext.Default.DownloadSettingsResponse);

    /// <summary>Sets how many downloads run at once.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> SettingsSetAsync(OperationContext context)
    {
        Outcome<DownloadSettingsCall> call = context.Read(NativeJsonContext.Default.DownloadSettingsCall);
        if (!call.IsSuccess)
        {
            return Envelope.Failure(call.Error);
        }

        if (call.Value.Parallel is not { } parallel)
        {
            return Envelope.Failure(ErrorCode.InvalidArgument, "'parallel' is required.");
        }

        Outcome<int> set = await context.Runtime.Downloads.SetParallelAsync(parallel, context.CancellationToken).ConfigureAwait(false);
        return set.IsSuccess
            ? Envelope.Ok(new DownloadSettingsResponse(set.Value), ApiJsonContext.Default.DownloadSettingsResponse)
            : Envelope.Failure(set.Error);
    }

    private static Outcome<Guid> ReadId(OperationContext context)
    {
        Outcome<DownloadIdCall> call = context.Read(NativeJsonContext.Default.DownloadIdCall);
        return call.IsSuccess ? DownloadMapper.ParseId(call.Value.Id, "id") : Outcome.Failure<Guid>(call.Error);
    }

    private static async Task<NativeResponse> ChangeAsync(
        OperationContext context,
        Func<DownloadPipeline, Guid, CancellationToken, Task<Outcome<DownloadJob>>> change)
    {
        Outcome<Guid> id = ReadId(context);
        if (!id.IsSuccess)
        {
            return Envelope.Failure(id.Error);
        }

        return Job(await change(context.Runtime.Downloads, id.Value, context.CancellationToken).ConfigureAwait(false));
    }

    private static NativeResponse Job(Outcome<DownloadJob> job) => job.IsSuccess
        ? Envelope.Ok(DownloadMapper.ToDto(job.Value), ApiJsonContext.Default.DownloadDto)
        : Envelope.Failure(job.Error);
}

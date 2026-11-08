using EMMA.Api.Contracts;
using EMMA.Api.Http;
using EMMA.Api.Mapping;
using EMMA.Api.Security;
using EMMA.Application.Pipelines;
using EMMA.Domain;

namespace EMMA.Api.Endpoints;

/// <summary>
/// Reads and records the caller's progress. Progress is always the caller's own: it is filed under the
/// authenticated client, and there is no way to name another user.
/// </summary>
internal static class ProgressEndpoints
{
    private const int DefaultRecent = 50;
    private const int MaxRecent = 200;

    /// <summary>Maps the endpoints.</summary>
    /// <param name="routes">The route builder, already under <c>/v1</c>.</param>
    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPut("/progress", SetAsync);
        routes.MapGet("/progress", GetAsync);
        routes.MapGet("/progress/continue", ContinueAsync);
        routes.MapGet("/progress/recent", RecentAsync);
        routes.MapGet("/progress/history", HistoryAsync);
        routes.MapGet("/progress/series", SeriesAsync);
        routes.MapDelete("/progress", DeleteAsync);
        routes.MapPost("/progress/clear", ClearAsync);
        routes.MapPost("/progress/complete", CompleteAsync);
        routes.MapGet("/progress/retention", GetRetentionAsync);
        routes.MapPut("/progress/retention", SetRetentionAsync);
    }

    private static async Task<IResult> SetAsync(HttpContext context, ProgressPipeline progress, CancellationToken cancellationToken)
    {
        Outcome<NodeRef> node = RequestReader.Node(context.Request, "ref");
        if (!node.IsSuccess)
        {
            return ApiResults.Failure(node.Error);
        }

        Outcome<SetProgressRequest> body = await RequestReader.BodyAsync(context.Request, ApiJsonContext.Default.SetProgressRequest, cancellationToken).ConfigureAwait(false);
        if (!body.IsSuccess)
        {
            return ApiResults.Failure(body.Error);
        }

        if (body.Value.Unit is null || body.Value.Value is null)
        {
            return ApiResults.Failure(ErrorCode.InvalidArgument, "'unit' and 'value' are required.");
        }

        ProgressLocator locator = new(body.Value.Unit, body.Value.Value.Value, body.Value.Total);
        Outcome<Progress> stored = await progress
            .SetAsync(context.RequireClient().User, node.Value, locator, body.Value.Completed ?? false, cancellationToken)
            .ConfigureAwait(false);
        return stored.IsSuccess
            ? ApiResults.Ok(UserMapper.ToDto(stored.Value), ApiJsonContext.Default.ProgressDto)
            : ApiResults.Failure(stored.Error);
    }

    private static async Task<IResult> GetAsync(HttpContext context, ProgressPipeline progress, CancellationToken cancellationToken)
    {
        Outcome<NodeRef> node = RequestReader.Node(context.Request, "ref");
        if (!node.IsSuccess)
        {
            return ApiResults.Failure(node.Error);
        }

        Progress? found = await progress.GetAsync(context.RequireClient().User, node.Value, cancellationToken).ConfigureAwait(false);
        return Found(found);
    }

    private static async Task<IResult> ContinueAsync(HttpContext context, ProgressPipeline progress, CancellationToken cancellationToken)
    {
        Outcome<NodeRef> container = RequestReader.Node(context.Request, "ref");
        if (!container.IsSuccess)
        {
            return ApiResults.Failure(container.Error);
        }

        Progress? found = await progress.ContinueAsync(context.RequireClient().User, container.Value, cancellationToken).ConfigureAwait(false);
        return Found(found);
    }

    private static async Task<IResult> RecentAsync(HttpContext context, ProgressPipeline progress, CancellationToken cancellationToken)
    {
        Outcome<int?> limit = RequestReader.Limit(context.Request, MaxRecent);
        if (!limit.IsSuccess)
        {
            return ApiResults.Failure(limit.Error);
        }

        IReadOnlyList<Progress> recent = await progress
            .RecentAsync(context.RequireClient().User, limit.Value ?? DefaultRecent, cancellationToken)
            .ConfigureAwait(false);
        return ApiResults.Ok(new ProgressListResponse([.. recent.Select(UserMapper.ToDto)]), ApiJsonContext.Default.ProgressListResponse);
    }

    private static async Task<IResult> HistoryAsync(HttpContext context, ProgressPipeline progress, CancellationToken cancellationToken)
    {
        Outcome<int?> limit = RequestReader.Limit(context.Request, MaxRecent);
        if (!limit.IsSuccess)
        {
            return ApiResults.Failure(limit.Error);
        }

        IReadOnlyList<ProgressGroup> groups = await progress
            .HistoryAsync(context.RequireClient().User, limit.Value ?? DefaultRecent, cancellationToken)
            .ConfigureAwait(false);
        return ApiResults.Ok(new ProgressHistoryResponse([.. groups.Select(UserMapper.ToDto)]), ApiJsonContext.Default.ProgressHistoryResponse);
    }

    private static async Task<IResult> SeriesAsync(HttpContext context, ProgressPipeline progress, CancellationToken cancellationToken)
    {
        Outcome<NodeRef> container = RequestReader.Node(context.Request, "ref");
        if (!container.IsSuccess)
        {
            return ApiResults.Failure(container.Error);
        }

        SeriesProgress series = await progress.SeriesAsync(context.RequireClient().User, container.Value, cancellationToken).ConfigureAwait(false);
        return ApiResults.Ok(UserMapper.ToDto(series), ApiJsonContext.Default.SeriesProgressDto);
    }

    private static async Task<IResult> DeleteAsync(HttpContext context, ProgressPipeline progress, CancellationToken cancellationToken)
    {
        Outcome<IReadOnlyList<NodeRef>> nodes = RequestReader.Nodes(context.Request, "ref");
        if (!nodes.IsSuccess)
        {
            return ApiResults.Failure(nodes.Error);
        }

        int removed = await progress.DeleteAsync(context.RequireClient().User, nodes.Value, cancellationToken).ConfigureAwait(false);
        return ApiResults.Ok(new RemovedResponse(removed), ApiJsonContext.Default.RemovedResponse);
    }

    private static async Task<IResult> ClearAsync(HttpContext context, ProgressPipeline progress, CancellationToken cancellationToken)
    {
        int removed = await progress.ClearAsync(context.RequireClient().User, cancellationToken).ConfigureAwait(false);
        return ApiResults.Ok(new RemovedResponse(removed), ApiJsonContext.Default.RemovedResponse);
    }

    private static async Task<IResult> CompleteAsync(HttpContext context, ProgressPipeline progress, CancellationToken cancellationToken)
    {
        Outcome<IReadOnlyList<NodeRef>> nodes = RequestReader.Nodes(context.Request, "ref");
        if (!nodes.IsSuccess)
        {
            return ApiResults.Failure(nodes.Error);
        }

        IReadOnlyList<Progress> stored = await progress.CompleteAsync(context.RequireClient().User, nodes.Value, cancellationToken).ConfigureAwait(false);
        return ApiResults.Ok(new ProgressListResponse([.. stored.Select(UserMapper.ToDto)]), ApiJsonContext.Default.ProgressListResponse);
    }

    private static async Task<IResult> GetRetentionAsync(HttpContext context, ProgressPipeline progress, CancellationToken cancellationToken)
    {
        int days = await progress.GetRetentionAsync(context.RequireClient().User, cancellationToken).ConfigureAwait(false);
        return ApiResults.Ok(new RetentionResponse(days, null), ApiJsonContext.Default.RetentionResponse);
    }

    private static async Task<IResult> SetRetentionAsync(HttpContext context, ProgressPipeline progress, CancellationToken cancellationToken)
    {
        Outcome<SetRetentionRequest> body = await RequestReader.BodyAsync(context.Request, ApiJsonContext.Default.SetRetentionRequest, cancellationToken).ConfigureAwait(false);
        if (!body.IsSuccess)
        {
            return ApiResults.Failure(body.Error);
        }

        if (body.Value.Days is not { } days)
        {
            return ApiResults.Failure(ErrorCode.InvalidArgument, "'days' is required.");
        }

        Outcome<HistoryRetention> set = await progress.SetRetentionAsync(context.RequireClient().User, days, cancellationToken).ConfigureAwait(false);
        return set.IsSuccess
            ? ApiResults.Ok(new RetentionResponse(set.Value.Days, set.Value.Removed), ApiJsonContext.Default.RetentionResponse)
            : ApiResults.Failure(set.Error);
    }

    private static IResult Found(Progress? progress) => progress is null
        ? ApiResults.Failure(ErrorCode.NotFound, "There is no progress recorded there.")
        : ApiResults.Ok(UserMapper.ToDto(progress), ApiJsonContext.Default.ProgressDto);
}

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

    private static IResult Found(Progress? progress) => progress is null
        ? ApiResults.Failure(ErrorCode.NotFound, "There is no progress recorded there.")
        : ApiResults.Ok(UserMapper.ToDto(progress), ApiJsonContext.Default.ProgressDto);
}

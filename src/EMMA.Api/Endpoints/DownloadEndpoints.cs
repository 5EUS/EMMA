using EMMA.Api.Contracts;
using EMMA.Api.Http;
using EMMA.Api.Mapping;
using EMMA.Application.Pipelines;
using EMMA.Domain;

using Microsoft.Extensions.Primitives;

namespace EMMA.Api.Endpoints;

/// <summary>
/// The download queue. Downloads belong to the host rather than to a client: every client sees and controls the
/// same jobs, and the stored bytes are what <c>GET /v1/assets/content</c> serves before it asks the plugin.
/// </summary>
internal static class DownloadEndpoints
{
    private const int DefaultList = 200;
    private const int MaxList = 1000;

    /// <summary>Maps the endpoints.</summary>
    /// <param name="routes">The route builder, already under <c>/v1</c>.</param>
    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/downloads", ListAsync);
        routes.MapPost("/downloads", EnqueueAsync);
        routes.MapDelete("/downloads", ClearAsync);
        routes.MapPost("/downloads/retry-failed", RetryFailedAsync);
        routes.MapGet("/downloads/settings", GetSettingsAsync);
        routes.MapPut("/downloads/settings", SetSettingsAsync);
        routes.MapGet("/downloads/{id:guid}", GetAsync);
        routes.MapDelete("/downloads/{id:guid}", DeleteAsync);
        routes.MapPost("/downloads/{id:guid}/pause", (Guid id, DownloadPipeline downloads, CancellationToken token) => ChangeAsync(downloads.PauseAsync(id, token)));
        routes.MapPost("/downloads/{id:guid}/resume", (Guid id, DownloadPipeline downloads, CancellationToken token) => ChangeAsync(downloads.ResumeAsync(id, token)));
        routes.MapPost("/downloads/{id:guid}/retry", (Guid id, DownloadPipeline downloads, CancellationToken token) => ChangeAsync(downloads.RetryAsync(id, token)));
        routes.MapPost("/downloads/{id:guid}/cancel", (Guid id, DownloadPipeline downloads, CancellationToken token) => ChangeAsync(downloads.CancelAsync(id, token)));
    }

    private static async Task<IResult> ListAsync(HttpContext context, DownloadPipeline downloads, CancellationToken cancellationToken)
    {
        Outcome<IReadOnlyCollection<DownloadState>?> states = ReadStates(context.Request);
        Outcome<int?> limit = RequestReader.Limit(context.Request, MaxList);
        if (!states.IsSuccess || !limit.IsSuccess)
        {
            return ApiResults.Failure((states.Error ?? limit.Error)!);
        }

        IReadOnlyList<DownloadJob> jobs = await downloads.ListAsync(states.Value, limit.Value ?? DefaultList, cancellationToken).ConfigureAwait(false);
        return ApiResults.Ok(new DownloadListResponse([.. jobs.Select(DownloadMapper.ToDto)]), ApiJsonContext.Default.DownloadListResponse);
    }

    private static async Task<IResult> EnqueueAsync(HttpContext context, DownloadPipeline downloads, CancellationToken cancellationToken)
    {
        Outcome<NodeRef> node = RequestReader.Node(context.Request, "ref");
        Outcome<string> role = RequestReader.Required(context.Request, "role");
        Outcome<string?> hint = RequestReader.Optional(context.Request, "hint");
        if (!node.IsSuccess || !role.IsSuccess || !hint.IsSuccess)
        {
            return ApiResults.Failure((node.Error ?? role.Error ?? hint.Error)!);
        }

        Outcome<AssetRole> parsedRole = RequestParsing.Role(role.Value);
        Outcome<string?> parsedHint = RequestParsing.Hint(hint.Value);
        if (!parsedRole.IsSuccess || !parsedHint.IsSuccess)
        {
            return ApiResults.Failure((parsedRole.Error ?? parsedHint.Error)!);
        }

        return Job(await downloads.EnqueueAsync(node.Value, parsedRole.Value, parsedHint.Value, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> GetAsync(Guid id, DownloadPipeline downloads, CancellationToken cancellationToken)
    {
        Outcome<DownloadDetail> detail = await downloads.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return detail.IsSuccess
            ? ApiResults.Ok(
                new DownloadDetailResponse(DownloadMapper.ToDto(detail.Value.Job), [.. detail.Value.Items.Select(DownloadMapper.ToDto)]),
                ApiJsonContext.Default.DownloadDetailResponse)
            : ApiResults.Failure(detail.Error);
    }

    private static async Task<IResult> DeleteAsync(Guid id, DownloadPipeline downloads, CancellationToken cancellationToken) =>
        await downloads.DeleteAsync(id, cancellationToken).ConfigureAwait(false)
            ? Results.NoContent()
            : ApiResults.Failure(ErrorCode.NotFound, "That download does not exist.");

    private static async Task<IResult> ClearAsync(HttpContext context, DownloadPipeline downloads, CancellationToken cancellationToken)
    {
        Outcome<IReadOnlyCollection<DownloadState>?> states = ReadStates(context.Request);
        if (!states.IsSuccess)
        {
            return ApiResults.Failure(states.Error);
        }

        // Clearing everything by leaving out 'state' would be too easy to do by accident.
        Outcome<int> cleared = await downloads.ClearAsync(states.Value ?? [], cancellationToken).ConfigureAwait(false);
        return cleared.IsSuccess
            ? ApiResults.Ok(new DownloadCountResponse(cleared.Value), ApiJsonContext.Default.DownloadCountResponse)
            : ApiResults.Failure(cleared.Error);
    }

    private static async Task<IResult> RetryFailedAsync(DownloadPipeline downloads, CancellationToken cancellationToken) =>
        ApiResults.Ok(new DownloadCountResponse(await downloads.RetryFailedAsync(cancellationToken).ConfigureAwait(false)), ApiJsonContext.Default.DownloadCountResponse);

    private static async Task<IResult> GetSettingsAsync(DownloadPipeline downloads, CancellationToken cancellationToken) =>
        ApiResults.Ok(new DownloadSettingsResponse(await downloads.GetParallelAsync(cancellationToken).ConfigureAwait(false)), ApiJsonContext.Default.DownloadSettingsResponse);

    private static async Task<IResult> SetSettingsAsync(HttpContext context, DownloadPipeline downloads, CancellationToken cancellationToken)
    {
        Outcome<SetDownloadSettingsRequest> body = await RequestReader.BodyAsync(context.Request, ApiJsonContext.Default.SetDownloadSettingsRequest, cancellationToken).ConfigureAwait(false);
        if (!body.IsSuccess)
        {
            return ApiResults.Failure(body.Error);
        }

        if (body.Value.Parallel is not { } parallel)
        {
            return ApiResults.Failure(ErrorCode.InvalidArgument, "'parallel' is required.");
        }

        Outcome<int> set = await downloads.SetParallelAsync(parallel, cancellationToken).ConfigureAwait(false);
        return set.IsSuccess
            ? ApiResults.Ok(new DownloadSettingsResponse(set.Value), ApiJsonContext.Default.DownloadSettingsResponse)
            : ApiResults.Failure(set.Error);
    }

    private static async Task<IResult> ChangeAsync(Task<Outcome<DownloadJob>> change) =>
        Job(await change.ConfigureAwait(false));

    private static IResult Job(Outcome<DownloadJob> job) => job.IsSuccess
        ? ApiResults.Ok(DownloadMapper.ToDto(job.Value), ApiJsonContext.Default.DownloadDto)
        : ApiResults.Failure(job.Error);

    private static Outcome<IReadOnlyCollection<DownloadState>?> ReadStates(HttpRequest request)
    {
        StringValues values = request.Query["state"];
        return DownloadMapper.ParseStates([.. values]);
    }
}

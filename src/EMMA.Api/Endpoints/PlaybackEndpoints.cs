using EMMA.Api.Contracts;
using EMMA.Api.Http;
using EMMA.Api.Mapping;
using EMMA.Application.Pipelines;
using EMMA.Application.Streaming;
using EMMA.Domain;

namespace EMMA.Api.Endpoints;

/// <summary>
/// What a player needs: the plan for a node (its streams, subtitles, audio tracks and chapters), a way to open one
/// of them, and the opened stream itself. The API never reveals where media lives: a player is given a path on the
/// API and the host fetches the media, with the headers and under the network policy of the plugin.
/// </summary>
internal static class PlaybackEndpoints
{
    /// <summary>The path streams are served under, which the rate limiter treats differently from the rest of the API.</summary>
    public const string StreamPath = "/v1/stream";

    /// <summary>Maps the endpoints.</summary>
    /// <param name="routes">The route builder, already under <c>/v1</c>.</param>
    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/playback", PlanAsync);
        routes.MapPost("/playback/open", OpenAsync);
        routes.MapMethods("/stream/{session}/{part}/{**rest}", ["GET", "HEAD"], ServeAsync);
    }

    private static async Task<IResult> PlanAsync(HttpContext context, PlaybackPipeline playback, CancellationToken cancellationToken)
    {
        Outcome<NodeRef> node = RequestReader.Node(context.Request, "ref");
        Outcome<string?> hint = RequestReader.Hint(context.Request);
        if (!node.IsSuccess || !hint.IsSuccess)
        {
            return ApiResults.Failure((node.Error ?? hint.Error)!);
        }

        Outcome<PlaybackPlan> plan = await playback.PlanAsync(node.Value, hint.Value, cancellationToken).ConfigureAwait(false);
        return plan.IsSuccess
            ? ApiResults.Ok(PlaybackMapper.ToDto(plan.Value), ApiJsonContext.Default.PlaybackPlanResponse)
            : ApiResults.Failure(plan.Error);
    }

    private static async Task<IResult> OpenAsync(HttpContext context, StreamProxy streams, CancellationToken cancellationToken)
    {
        Outcome<NodeRef> node = RequestReader.Node(context.Request, "ref");
        Outcome<string> role = RequestReader.Required(context.Request, "role");
        Outcome<int> index = RequestReader.Index(context.Request);
        Outcome<string?> hint = RequestReader.Hint(context.Request);
        if (!node.IsSuccess || !role.IsSuccess || !index.IsSuccess || !hint.IsSuccess)
        {
            return ApiResults.Failure((node.Error ?? role.Error ?? index.Error ?? hint.Error)!);
        }

        if (!AssetRole.TryCreate(role.Value, out AssetRole parsed))
        {
            return ApiResults.Failure(new PluginError(ErrorCode.InvalidArgument, "'role' is not a valid asset role."));
        }

        Outcome<StreamLink> link = await streams.OpenAsync(node.Value, parsed, index.Value, hint.Value, cancellationToken).ConfigureAwait(false);
        return link.IsSuccess
            ? ApiResults.Ok(new StreamOpenResponse(new Uri($"{StreamPath}/{link.Value.Path}", UriKind.Relative), link.Value.Mime), ApiJsonContext.Default.StreamOpenResponse)
            : ApiResults.Failure(link.Error);
    }

    private static async Task<IResult> ServeAsync(HttpContext context, StreamProxy streams, string session, string part, string? rest, CancellationToken cancellationToken)
    {
        string path = string.IsNullOrEmpty(rest) ? $"{session}/{part}" : $"{session}/{part}/{rest}";
        string? query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value![1..] : null;
        Outcome<StreamResponse> response = await streams
            .ServeAsync(new StreamRequest(path, RequestReader.Range(context.Request), query, $"{context.Request.Scheme}://{context.Request.Host}{StreamPath}/"), cancellationToken)
            .ConfigureAwait(false);
        return response.IsSuccess
            ? new StreamResult(response.Value, string.Equals(context.Request.Method, HttpMethods.Head, StringComparison.Ordinal))
            : ApiResults.Failure(response.Error);
    }
}

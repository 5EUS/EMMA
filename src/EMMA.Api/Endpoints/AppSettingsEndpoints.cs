using EMMA.Api.Contracts;
using EMMA.Api.Http;
using EMMA.Api.Mapping;
using EMMA.Api.Security;
using EMMA.Application.Pipelines;
using EMMA.Domain;

namespace EMMA.Api.Endpoints;

/// <summary>What the app remembers for the caller: small text choices under a scope. Like progress, they are the caller's own.</summary>
internal static class AppSettingsEndpoints
{
    /// <summary>Maps the endpoints.</summary>
    /// <param name="routes">The route builder, already under <c>/v1</c>.</param>
    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/app-settings", GetAsync);
        routes.MapPut("/app-settings", SetAsync);
    }

    private static async Task<IResult> GetAsync(HttpContext context, AppSettingsPipeline settings, CancellationToken cancellationToken)
    {
        Outcome<string> scope = RequestReader.Required(context.Request, "scope");
        if (!scope.IsSuccess)
        {
            return ApiResults.Failure(scope.Error);
        }

        return Values(scope.Value, await settings.GetAsync(context.RequireClient().User, scope.Value, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> SetAsync(HttpContext context, AppSettingsPipeline settings, CancellationToken cancellationToken)
    {
        Outcome<string> scope = RequestReader.Required(context.Request, "scope");
        if (!scope.IsSuccess)
        {
            return ApiResults.Failure(scope.Error);
        }

        Outcome<SetAppSettingsRequest> body = await RequestReader.BodyAsync(context.Request, ApiJsonContext.Default.SetAppSettingsRequest, cancellationToken).ConfigureAwait(false);
        Outcome<IReadOnlyList<AppSettingChange>> changes = body.IsSuccess
            ? UserMapper.ToChanges(body.Value.Changes)
            : Outcome.Failure<IReadOnlyList<AppSettingChange>>(body.Error);
        if (!changes.IsSuccess)
        {
            return ApiResults.Failure(changes.Error);
        }

        return Values(scope.Value, await settings.SetAsync(context.RequireClient().User, scope.Value, changes.Value, cancellationToken).ConfigureAwait(false));
    }

    private static IResult Values(string scope, Outcome<IReadOnlyDictionary<string, string>> values) => values.IsSuccess
        ? ApiResults.Ok(new AppSettingsResponse(scope, values.Value), ApiJsonContext.Default.AppSettingsResponse)
        : ApiResults.Failure(values.Error);
}

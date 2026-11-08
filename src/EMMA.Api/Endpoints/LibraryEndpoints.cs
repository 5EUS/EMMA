using EMMA.Api.Contracts;
using EMMA.Api.Http;
using EMMA.Api.Mapping;
using EMMA.Api.Security;
using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.Runtime;

namespace EMMA.Api.Endpoints;

/// <summary>The caller's saved nodes. Like progress, a library is the caller's own.</summary>
internal static class LibraryEndpoints
{
    /// <summary>Maps the endpoints.</summary>
    /// <param name="routes">The route builder, already under <c>/v1</c>.</param>
    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/library", ListAsync);
        routes.MapPut("/library", AddAsync);
        routes.MapDelete("/library", RemoveAsync);
    }

    private static async Task<IResult> ListAsync(HttpContext context, ILibraryPort library, CancellationToken cancellationToken)
    {
        IReadOnlyList<LibraryEntry> entries = await library.ListAsync(context.RequireClient().User, cancellationToken).ConfigureAwait(false);
        return ApiResults.Ok(new LibraryResponse([.. entries.Select(UserMapper.ToDto)]), ApiJsonContext.Default.LibraryResponse);
    }

    private static async Task<IResult> AddAsync(HttpContext context, EmmaRuntime runtime, CancellationToken cancellationToken)
    {
        Outcome<NodeRef> node = RequestReader.Node(context.Request, "ref");
        if (!node.IsSuccess)
        {
            return ApiResults.Failure(node.Error);
        }

        Outcome<bool> added = await runtime.AddToLibraryAsync(context.RequireClient().User, node.Value, cancellationToken).ConfigureAwait(false);
        return added.IsSuccess ? Results.NoContent() : ApiResults.Failure(added.Error);
    }

    private static async Task<IResult> RemoveAsync(HttpContext context, ILibraryPort library, CancellationToken cancellationToken)
    {
        Outcome<NodeRef> node = RequestReader.Node(context.Request, "ref");
        if (!node.IsSuccess)
        {
            return ApiResults.Failure(node.Error);
        }

        bool removed = await library.RemoveAsync(context.RequireClient().User, node.Value, cancellationToken).ConfigureAwait(false);
        return removed
            ? Results.NoContent()
            : ApiResults.Failure(ErrorCode.NotFound, "That node is not in the library.");
    }
}

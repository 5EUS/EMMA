using EMMA.Api.Contracts;
using EMMA.Api.Http;
using EMMA.Api.Mapping;
using EMMA.Api.Security;
using EMMA.Application.Ports;
using EMMA.Domain;

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

    private static async Task<IResult> AddAsync(
        HttpContext context,
        ILibraryPort library,
        INodeStorePort nodes,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        Outcome<NodeRef> node = RequestReader.Node(context.Request, "ref");
        if (!node.IsSuccess)
        {
            return ApiResults.Failure(node.Error);
        }

        // Only nodes the host has served can be saved, so a library never holds a reference nothing can resolve.
        if (await nodes.GetAsync(node.Value, cancellationToken).ConfigureAwait(false) is null)
        {
            return ApiResults.Failure(ErrorCode.NotFound, "That node has not been seen; browse or search to it first.");
        }

        await library.AddAsync(new LibraryEntry(context.RequireClient().User, node.Value, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
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

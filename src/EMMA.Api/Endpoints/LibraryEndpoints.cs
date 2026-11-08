using EMMA.Api.Contracts;
using EMMA.Api.Http;
using EMMA.Api.Mapping;
using EMMA.Api.Security;
using EMMA.Application.Pipelines;
using EMMA.Domain;

namespace EMMA.Api.Endpoints;

/// <summary>The caller's libraries and the nodes saved in them. Like progress, a library is the caller's own.</summary>
internal static class LibraryEndpoints
{
    private const int DefaultSearch = 50;
    private const int MaxSearch = 200;

    /// <summary>Maps the endpoints.</summary>
    /// <param name="routes">The route builder, already under <c>/v1</c>.</param>
    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/library", ListAsync);
        routes.MapGet("/library/search", SearchAsync);
        routes.MapPut("/library", AddAsync);
        routes.MapDelete("/library", RemoveAsync);
        routes.MapPost("/library/move", MoveAsync);
        routes.MapGet("/libraries", LibrariesAsync);
        routes.MapPost("/libraries", CreateAsync);
        routes.MapDelete("/libraries", DeleteAsync);
    }

    private static async Task<IResult> ListAsync(HttpContext context, LibraryPipeline library, CancellationToken cancellationToken)
    {
        Outcome<string?> name = RequestReader.Optional(context.Request, "library");
        if (!name.IsSuccess)
        {
            return ApiResults.Failure(name.Error);
        }

        Outcome<IReadOnlyList<LibraryEntry>> entries = await library.ListAsync(context.RequireClient().User, name.Value, cancellationToken).ConfigureAwait(false);
        return Entries(entries);
    }

    private static async Task<IResult> SearchAsync(HttpContext context, LibraryPipeline library, CancellationToken cancellationToken)
    {
        Outcome<string> text = RequestReader.Required(context.Request, "text");
        Outcome<string?> name = RequestReader.Optional(context.Request, "library");
        Outcome<int?> limit = RequestReader.Limit(context.Request, MaxSearch);
        if (!text.IsSuccess || !name.IsSuccess || !limit.IsSuccess)
        {
            return ApiResults.Failure((text.Error ?? name.Error ?? limit.Error)!);
        }

        Outcome<IReadOnlyList<LibraryEntry>> found = await library
            .SearchAsync(context.RequireClient().User, text.Value, name.Value, limit.Value ?? DefaultSearch, cancellationToken)
            .ConfigureAwait(false);
        return Entries(found);
    }

    private static async Task<IResult> AddAsync(HttpContext context, LibraryPipeline library, CancellationToken cancellationToken)
    {
        Outcome<NodeRef> node = RequestReader.Node(context.Request, "ref");
        Outcome<string?> name = RequestReader.Optional(context.Request, "library");
        if (!node.IsSuccess || !name.IsSuccess)
        {
            return ApiResults.Failure((node.Error ?? name.Error)!);
        }

        Outcome<bool> added = await library.AddAsync(context.RequireClient().User, node.Value, name.Value, cancellationToken).ConfigureAwait(false);
        return added.IsSuccess ? Results.NoContent() : ApiResults.Failure(added.Error);
    }

    private static async Task<IResult> RemoveAsync(HttpContext context, LibraryPipeline library, CancellationToken cancellationToken)
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

    private static async Task<IResult> MoveAsync(HttpContext context, LibraryPipeline library, CancellationToken cancellationToken)
    {
        Outcome<NodeRef> node = RequestReader.Node(context.Request, "ref");
        Outcome<string> name = RequestReader.Required(context.Request, "library");
        if (!node.IsSuccess || !name.IsSuccess)
        {
            return ApiResults.Failure((node.Error ?? name.Error)!);
        }

        Outcome<bool> moved = await library.MoveAsync(context.RequireClient().User, node.Value, name.Value, cancellationToken).ConfigureAwait(false);
        return moved.IsSuccess ? Results.NoContent() : ApiResults.Failure(moved.Error);
    }

    private static async Task<IResult> LibrariesAsync(HttpContext context, LibraryPipeline library, CancellationToken cancellationToken)
    {
        IReadOnlyList<LibrarySummary> libraries = await library.LibrariesAsync(context.RequireClient().User, cancellationToken).ConfigureAwait(false);
        return ApiResults.Ok(new LibrariesResponse([.. libraries.Select(UserMapper.ToDto)]), ApiJsonContext.Default.LibrariesResponse);
    }

    private static async Task<IResult> CreateAsync(HttpContext context, LibraryPipeline library, CancellationToken cancellationToken)
    {
        Outcome<CreateLibraryRequest> body = await RequestReader.BodyAsync(context.Request, ApiJsonContext.Default.CreateLibraryRequest, cancellationToken).ConfigureAwait(false);
        if (!body.IsSuccess)
        {
            return ApiResults.Failure(body.Error);
        }

        Outcome<LibrarySummary> created = await library.CreateAsync(context.RequireClient().User, body.Value.Name, cancellationToken).ConfigureAwait(false);
        return created.IsSuccess
            ? ApiResults.Ok(UserMapper.ToDto(created.Value), ApiJsonContext.Default.LibrarySummaryDto)
            : ApiResults.Failure(created.Error);
    }

    private static async Task<IResult> DeleteAsync(HttpContext context, LibraryPipeline library, CancellationToken cancellationToken)
    {
        Outcome<string> name = RequestReader.Required(context.Request, "name");
        if (!name.IsSuccess)
        {
            return ApiResults.Failure(name.Error);
        }

        Outcome<bool> deleted = await library.DeleteAsync(context.RequireClient().User, name.Value, cancellationToken).ConfigureAwait(false);
        return deleted.IsSuccess ? Results.NoContent() : ApiResults.Failure(deleted.Error);
    }

    private static IResult Entries(Outcome<IReadOnlyList<LibraryEntry>> entries) => entries.IsSuccess
        ? ApiResults.Ok(new LibraryResponse([.. entries.Value.Select(UserMapper.ToDto)]), ApiJsonContext.Default.LibraryResponse)
        : ApiResults.Failure(entries.Error);
}

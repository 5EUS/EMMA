using EMMA.Api;
using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.Infrastructure.InMemory;
using EMMA.Infrastructure.Policy;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<InMemoryMediaStore>();
builder.Services.AddSingleton<IMediaSearchPort, InMemorySearchPort>();
builder.Services.AddSingleton<IPageProviderPort, InMemoryPageProvider>();
builder.Services.AddSingleton<IPolicyEvaluator, AllowAllPolicyEvaluator>();
builder.Services.AddSingleton<ICachePort, InMemoryCachePort>();

builder.Services.AddSingleton(sp =>
{
    var store = sp.GetRequiredService<InMemoryMediaStore>();
    SeedDemoData(store);

    return EmbeddedRuntimeFactory.Create(
        sp.GetRequiredService<IMediaSearchPort>(),
        sp.GetRequiredService<IPageProviderPort>(),
        sp.GetRequiredService<IPolicyEvaluator>(),
        metadataCache: sp.GetRequiredService<ICachePort>());
});

var app = builder.Build();

app.MapGet("/", () => "EMMA API host is running.");

app.MapGet("/api/paged/search", async (
    string? query,
    EmbeddedRuntime runtime,
    CancellationToken cancellationToken) =>
{
    var results = await runtime.Pipeline.SearchAsync(query ?? string.Empty, cancellationToken);
    return Results.Ok(results.Select(result => new
    {
        Id = result.Id.ToString(),
        Source = result.SourceId,
        result.Title,
        MediaType = result.MediaType.ToString().ToLowerInvariant()
    }));
});

app.MapGet("/api/paged/chapters", async (
    string? mediaId,
    EmbeddedRuntime runtime,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(mediaId))
    {
        return Results.BadRequest(new { message = "mediaId is required." });
    }

    var chapters = await runtime.Pipeline.GetChaptersAsync(MediaId.Create(mediaId), cancellationToken);
    return Results.Ok(chapters.Select(chapter => new
    {
        Id = chapter.ChapterId,
        chapter.Number,
        chapter.Title
    }));
});

app.MapGet("/api/paged/page", async (
    string? mediaId,
    string? chapterId,
    int? index,
    EmbeddedRuntime runtime,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId))
    {
        return Results.BadRequest(new { message = "mediaId and chapterId are required." });
    }

    var page = await runtime.Pipeline.GetPageAsync(
        MediaId.Create(mediaId),
        chapterId,
        index ?? 0,
        cancellationToken);

    return Results.Ok(new
    {
        Id = page.PageId,
        page.Index,
        ContentUri = page.ContentUri.ToString()
    });
});

app.Run();

static void SeedDemoData(InMemoryMediaStore store)
{
    if (store.Media.Count > 0)
    {
        return;
    }

    var mediaId = MediaId.Create("demo-1");
    var summary = new MediaSummary(mediaId, "local", "Embedded Demo", MediaType.Paged);
    store.AddMedia(summary);

    var chapter = new MediaChapter("ch-1", 1, "Chapter One");
    store.AddChapter(mediaId, chapter);

    store.AddPage(
        mediaId,
        chapter.ChapterId,
        new MediaPage("page-1", 0, new Uri("https://example.invalid/page-1.jpg")));
}

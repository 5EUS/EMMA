using EMMA.Api;
using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.Infrastructure.Http;
using EMMA.Infrastructure.InMemory;
using EMMA.Infrastructure.Policy;
using EMMA.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<PluginHostClientOptions>(builder.Configuration.GetSection("PluginHost"));
builder.Services.AddHttpClient<PluginHostPagedMediaPort>((sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PluginHostClientOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
});
builder.Services.AddSingleton<IMediaSearchPort>(sp => sp.GetRequiredService<PluginHostPagedMediaPort>());
builder.Services.AddSingleton<IPageProviderPort>(sp => sp.GetRequiredService<PluginHostPagedMediaPort>());
builder.Services.AddSingleton<IPolicyEvaluator>(_ => new HostPolicyEvaluator());
builder.Services.AddSingleton<ICachePort, InMemoryCachePort>();
builder.Services.AddSingleton(StorageOptions.Default);
builder.Services.AddSingleton<StorageInitializer>();

builder.Services.AddSingleton(sp =>
{
    return EmbeddedRuntimeFactory.Create(
        sp.GetRequiredService<IMediaSearchPort>(),
        sp.GetRequiredService<IPageProviderPort>(),
        sp.GetRequiredService<IPolicyEvaluator>(),
        metadataCache: sp.GetRequiredService<ICachePort>());
});

var app = builder.Build();

var storageInitializer = app.Services.GetRequiredService<StorageInitializer>();
await storageInitializer.InitializeAsync(CancellationToken.None);

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

app.MapGet("/api/paged/page-asset", async (
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

    var asset = await runtime.Pipeline.GetPageAssetAsync(page, cancellationToken);
    return Results.File(asset.Payload, asset.ContentType);
});

app.Run();

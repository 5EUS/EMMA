using System.Diagnostics;
using System.Threading.RateLimiting;
using EMMA.Api;
using EMMA.Api.Configuration;
using EMMA.Api.Services;
using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.Infrastructure.Http;
using EMMA.Infrastructure.InMemory;
using EMMA.Infrastructure.Policy;
using EMMA.Storage;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ApiAuthOptions>(builder.Configuration.GetSection("ApiAuth"));
builder.Services.Configure<ApiRateLimitOptions>(builder.Configuration.GetSection("ApiRateLimit"));
builder.Services.AddSingleton<IApiKeyValidator, ApiKeyValidator>();
builder.Services.AddSingleton<IClientIdentityAccessor, ClientIdentityAccessor>();
builder.Services.AddGrpc(options =>
{
    options.Interceptors.Add<ApiKeyGrpcInterceptor>();
});
builder.Services.AddRateLimiter(options =>
{
    static ApiRateLimitOptions GetRateLimitOptions(HttpContext context)
        => context.RequestServices.GetRequiredService<IOptions<ApiRateLimitOptions>>().Value;

    static ApiAuthOptions GetAuthOptions(HttpContext context)
        => context.RequestServices.GetRequiredService<IOptions<ApiAuthOptions>>().Value;

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var settings = GetRateLimitOptions(context);
        return RateLimitPartition.GetConcurrencyLimiter(
            "global",
            _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = Math.Max(1, settings.GlobalConcurrencyLimit),
                QueueLimit = Math.Max(0, settings.GlobalQueueLimit),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
    });
    options.AddPolicy("per-client", context =>
    {
        var settings = GetRateLimitOptions(context);
        var auth = GetAuthOptions(context);
        var key = ApiAuthHeader.GetClientKey(context, auth.HeaderName);
        return RateLimitPartition.GetFixedWindowLimiter(
            key,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, settings.PerClientPermitLimit),
                Window = TimeSpan.FromSeconds(Math.Max(1, settings.PerClientWindowSeconds)),
                QueueLimit = Math.Max(0, settings.PerClientQueueLimit),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
    });
});
builder.Services.Configure<PluginHostClientOptions>(builder.Configuration.GetSection("PluginHost"));
builder.Services.AddHttpClient<PluginHostPagedMediaPort>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<PluginHostClientOptions>>().Value;
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

app.UseRateLimiter();
app.UseMiddleware<ApiKeyAuthMiddleware>();

var storageInitializer = app.Services.GetRequiredService<StorageInitializer>();
await storageInitializer.InitializeAsync(CancellationToken.None);

app.MapGet("/", () => "EMMA API host is running.");

app.MapGrpcService<PagedMediaApiService>()
    .RequireRateLimiting("per-client");

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
}).RequireRateLimiting("per-client");

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
}).RequireRateLimiting("per-client");

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
}).RequireRateLimiting("per-client");

app.MapGet("/api/paged/page-asset", async (
    string? mediaId,
    string? chapterId,
    int? index,
    PluginHostPagedMediaPort pluginHost,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId))
    {
        return Results.BadRequest(new { message = "mediaId and chapterId are required." });
    }

    var correlationId = Guid.NewGuid().ToString("n");
    var stopwatch = Stopwatch.StartNew();

    try
    {
        var asset = await pluginHost.GetPageAssetAsync(
            MediaId.Create(mediaId),
            chapterId,
            index ?? 0,
            cancellationToken);

        logger.LogInformation(
            "Page asset proxy {CorrelationId} mediaId={MediaId} chapterId={ChapterId} index={Index} size={Size} elapsedMs={ElapsedMs}",
            correlationId,
            mediaId,
            chapterId,
            index ?? 0,
            asset.Payload.Length,
            stopwatch.ElapsedMilliseconds);

        return Results.File(asset.Payload, asset.ContentType);
    }
    catch (Exception ex)
    {
        logger.LogError(
            ex,
            "Page asset proxy failed {CorrelationId} mediaId={MediaId} chapterId={ChapterId} index={Index} elapsedMs={ElapsedMs}",
            correlationId,
            mediaId,
            chapterId,
            index ?? 0,
            stopwatch.ElapsedMilliseconds);
        throw;
    }
}).RequireRateLimiting("per-client");

app.Run();

public partial class Program
{
}

using ConsoleAppFramework;
using EMMA.Api;
using EMMA.Api.Embedded;
using EMMA.Domain;
using EMMA.Contracts.Api.V1;
using EMMA.Infrastructure.Http;
using EMMA.Infrastructure.InMemory;
using EMMA.Infrastructure.Policy;
using Microsoft.Extensions.Options;

namespace EMMA.Cli;

public class MyCommands
{
    private readonly EmbeddedRuntime _runtime;
    private readonly EmbeddedPagedMediaApi _api;

    public MyCommands()
    {
        var baseUrl = Environment.GetEnvironmentVariable("EMMA_PLUGIN_HOST_URL")?.Trim().TrimEnd('/')
            ?? "http://localhost:5000";

        var pluginId = Environment.GetEnvironmentVariable("EMMA_PLUGIN_ID")?.Trim() ?? "emma.plugin.test";

        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl, UriKind.Absolute) };
        var pluginPort = new PluginHostPagedMediaPort(
            httpClient,
            Options.Create(new PluginHostClientOptions
            {
                BaseUrl = baseUrl,
                PluginId = pluginId
            }));

        _runtime = EmbeddedRuntimeFactory.Create(
            pluginPort,
            pluginPort,
            new HostPolicyEvaluator(),
            metadataCache: new InMemoryCachePort());

        _api = new EmbeddedPagedMediaApi(_runtime);
    }

    /// <summary>
    /// Search command test.
    /// </summary>
    /// <param name="query">-q, Search query.</param>
    [Command("search")]
    public async Task SearchAsync(string query)
    {

        var response = await _api.SearchAsync(new SearchRequest
        {
            Query = query,
            Context = new ApiRequestContext
            {
                CorrelationId = Guid.NewGuid().ToString("n"),
                DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O"),
                ClientId = "cli"
            }
        }, CancellationToken.None);

        if (response.OutcomeCase == SearchResponse.OutcomeOneofCase.Error)
        {
            Console.WriteLine($"Search failed: {response.Error.Code} {response.Error.Message}");
            return;
        }

        foreach (var item in response.Result.Items)
        {

            CommandContextHolder.Context.AddResult(new SimpleResult(
                $"{item.Title} ({item.Id})",
                [
                    new ResultAction
                {
                    Name = "Open",
                    Execute = async () =>
                    {
                        Console.WriteLine($"Fetching media chapters (id: {item.Id})...");
                        await ChaptersAsync(item.Id);
                    }
                }
                ]));
        }
    }

    /// <summary>
    /// Chapters command test.
    /// </summary>
    /// <param name="id">-i, Media ID.</param>
    [Command("chapters")]
    public async Task ChaptersAsync(string id)
    {
        var response = await _api.GetChaptersAsync(new ChaptersRequest
        {
            MediaId = id,
            Context = new ApiRequestContext
            {
                CorrelationId = Guid.NewGuid().ToString("n"),
                DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O"),
                ClientId = "cli"
            }
        }, CancellationToken.None);

        if (response.OutcomeCase == ChaptersResponse.OutcomeOneofCase.Error)
        {
            Console.WriteLine($"Chapters failed: {response.Error.Code} {response.Error.Message}");
            return;
        }
        foreach (var item in response.Result.Items)
        {
            CommandContextHolder.Context.AddResult(new SimpleResult(
                $"{item.Title} ({item.Id})",
                [
                    new ResultAction
                    {
                        Name = "Open",
                        Execute = async () =>
                        {
                            Console.WriteLine($"Fetching chapter pages (media id: {id}, chapter id: {item.Id})...");
                            PageAsync(id, item.Id, 0).Wait();
                        }
                    },
                    new ResultAction
                    {
                        Name = "Pages",
                        Execute = async () =>
                        {
                            Console.WriteLine($"Fetching pages list (media id: {id}, chapter id: {item.Id})...");
                            try
                            {
                                // Fetch a large batch of pages; pipeline will return reachable pages and indicate end.
                                var startIndex = 0;
                                var pagesResult = await _runtime.Pipeline.GetPagesAsync(MediaId.Create(id), item.Id, startIndex, 10000, CancellationToken.None);
                                for (int p = 0; p < pagesResult.Pages.Count; p++)
                                {
                                    var page = pagesResult.Pages[p];
                                    var pageIndex = startIndex + p; // preserve absolute index
                                    CommandContextHolder.Context.AddResult(new SimpleResult(
                                        $"Page {pageIndex}",
                                        [
                                            new ResultAction
                                            {
                                                Name = "Open",
                                                Execute = async () =>
                                                {
                                                    await PageAsync(id, item.Id, pageIndex);
                                                }
                                            }
                                        ]));
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Failed to fetch pages list: {ex.Message}");
                            }
                        }
                    }
                ]));
        }
    }

    /// <summary>
    /// Page asset command test.
    /// </summary>
    /// <param name="mid">-mi, Media ID.</param>
    /// <param name="cid">-ci, Asset ID.</param>
    [Command("page-asset")]
    public async Task PageAssetAsync(string mid, string cid)
    {
        var response = await _api.GetPageAssetAsync(new PageAssetRequest
        {
            MediaId = mid,
            ChapterId = cid,
            Context = new ApiRequestContext
            {
                CorrelationId = Guid.NewGuid().ToString("n"),
                DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O"),
                ClientId = "cli"
            }
        }, CancellationToken.None);

        if (response.OutcomeCase == PageAssetResponse.OutcomeOneofCase.Error)
        {
            Console.WriteLine($"Page asset failed: {response.Error.Code} {response.Error.Message}");
            return;
        }
        CommandContextHolder.Context.AddResult(new SimpleResult(
            $"Page Asset (media: {mid}, chapter: {cid})",
            [
                new ResultAction
                {
                    Name = "Show Base64",
                    Execute = async () =>
                    {
                        Console.WriteLine($"Content Base64: {Convert.ToBase64String(response.Asset.Payload.ToArray())}");
                    }
                }
            ]));
    }

    /// <summary>
    /// Page command test.
    /// </summary>
    /// <param name="mid">-mi, Media ID.</param>
    /// <param name="cid">-ci, Asset ID.</param>
    /// <param name="index">-i, Page index.</param>
    [Command("page")]
    public async Task PageAsync(string mid, string cid, int index)
    {
        var response = await _api.GetPageAsync(new PageRequest
        {
            MediaId = mid,
            ChapterId = cid,
            Index = index,
            Context = new ApiRequestContext
            {
                CorrelationId = Guid.NewGuid().ToString("n"),
                DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O"),
                ClientId = "cli"
            }
        }, CancellationToken.None);

        if (response.OutcomeCase == PageResponse.OutcomeOneofCase.Error)
        {
            Console.WriteLine($"Page failed: {response.Error.Code} {response.Error.Message}");
            return;
        }

        Console.WriteLine($"Content URI: {response.Page.ContentUri}");
        Console.WriteLine($"Index: {response.Page.Index}");

    }

    /// <summary>
    /// Shows a list of commands and how to use them.
    /// </summary>
    /// <returns></returns>
    [Command("help")]
    public async Task Help()
    {
        Console.WriteLine("Available commands:");
        Console.WriteLine("  search -q <query>                              Search for media");
        Console.WriteLine("  chapters -i <mediaId>                          List chapters of a media");
        Console.WriteLine("  page-asset -mi <mediaId> -ci <chapterId>       Get a chapter page asset");
        Console.WriteLine("  page -mi <mediaId> -ci <chapterId> -i <index>  Get a chapter page");
        Console.WriteLine("  help                                           Show this help message");
    }
}
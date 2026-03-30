using EMMA.Api;
using EMMA.Api.Embedded;
using EMMA.Contracts.Api.V1;
using EMMA.Infrastructure.Http;
using EMMA.Infrastructure.InMemory;
using EMMA.Infrastructure.Policy;
using Microsoft.Extensions.Options;

var baseUrl = Environment.GetEnvironmentVariable("EMMA_PLUGIN_HOST_URL")?.Trim().TrimEnd('/')
    ?? "http://localhost:5001";
var pluginId = Environment.GetEnvironmentVariable("EMMA_PLUGIN_ID")?.Trim()
    ?? "demo";
var query = args.FirstOrDefault()?.Trim();
if (string.IsNullOrWhiteSpace(query))
{
    query = "demo";
}

using var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl, UriKind.Absolute) };
var pluginPort = new PluginHostPagedMediaPort(
    httpClient,
    Options.Create(new PluginHostClientOptions
    {
        BaseUrl = baseUrl,
        PluginId = pluginId
    }));

var runtime = EmbeddedRuntimeFactory.Create(
    pluginPort,
    pluginPort,
    new HostPolicyEvaluator(),
    metadataCache: new InMemoryCachePort());

var api = new EmbeddedPagedMediaApi(runtime);

Console.WriteLine("EMMA CLI run");
Console.WriteLine($"Plugin host: {baseUrl} (pluginId={pluginId})");

var response = await api.SearchAsync(new SearchRequest
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

Console.WriteLine($"Search results: {response.Result.Items.Count}");
foreach (var item in response.Result.Items)
{
    Console.WriteLine($"- {item.Title} ({item.Id})");
}

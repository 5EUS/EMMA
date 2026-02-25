using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.Infrastructure.InMemory;
using EMMA.Infrastructure.Policy;

namespace EMMA.Bootstrap;

/// <summary>
/// In-memory runtime bundle for smoke testing and local validation.
/// </summary>
public sealed record InMemoryRuntime(PagedMediaPipeline Pipeline, InMemoryMediaStore Store);

/// <summary>
/// Composition root for minimal runtime wiring.
/// </summary>
public static class RuntimeBootstrap
{
    // TODO: Deprecate this in-memory bootstrap once the full runtime composition root exists.
    /// <summary>
    /// Creates an in-memory runtime with a seeded demo catalog.
    /// </summary>
    public static InMemoryRuntime CreateInMemory()
    {
        var store = new InMemoryMediaStore();
        SeedSampleData(store);

        var cache = new InMemoryCachePort();
        var policy = new HostPolicyEvaluator();
        var search = new InMemorySearchPort(store);
        var pages = new InMemoryPageProvider(store);

        var pipeline = new PagedMediaPipeline(search, pages, policy, cache);

        return new InMemoryRuntime(pipeline, store);
    }

    /// <summary>
    /// Seeds a tiny dataset for CLI smoke runs.
    /// </summary>
    private static void SeedSampleData(InMemoryMediaStore store)
    {
        var mediaId = MediaId.Create("demo-1");
        var summary = new MediaSummary(
            mediaId,
            "local",
            "Milestone One Demo",
            MediaType.Paged);

        store.AddMedia(summary);

        var chapter = new MediaChapter("ch-1", 1, "Chapter One");
        store.AddChapter(mediaId, chapter);

        store.AddPage(
            mediaId,
            chapter.ChapterId,
            new MediaPage("page-1", 0, new Uri("https://example.invalid/page-1.jpg")));
    }
}

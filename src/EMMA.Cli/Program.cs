using EMMA.Bootstrap;

var runtime = RuntimeBootstrap.CreateInMemory();
var pipeline = runtime.Pipeline;

Console.WriteLine("EMMA Milestone 1 smoke run");

var results = await pipeline.SearchAsync("demo", CancellationToken.None);
Console.WriteLine($"Search results: {results.Count}");

foreach (var item in results)
{
	Console.WriteLine($"- {item.Title} ({item.Id})");
}

if (results.Count > 0)
{
	var media = results[0];
	var chapters = await pipeline.GetChaptersAsync(media.Id, CancellationToken.None);
	Console.WriteLine($"Chapters: {chapters.Count}");

	if (chapters.Count > 0)
	{
		var firstChapter = chapters[0];
		var page = await pipeline.GetPageAsync(media.Id, firstChapter.ChapterId, 0, CancellationToken.None);
		Console.WriteLine($"Page 1 URI: {page.ContentUri}");
	}
}

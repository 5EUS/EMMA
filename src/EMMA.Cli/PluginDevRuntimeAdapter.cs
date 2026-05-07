using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using EMMA.Api;
using EMMA.Api.Embedded;
using EMMA.Contracts.Api.V1;
using EMMA.Domain;
using EMMA.Plugin.Common;

namespace EMMA.Cli;

public sealed record PluginDevBuildPlan(
    string Name,
    string WorkingDirectory,
    string Command,
    IReadOnlyList<string> Arguments,
    string? ArtifactPath,
    string Description);

public sealed record PluginDevPackResult(string PackagePath, string ManifestPath, string ArtifactPath);

public sealed record PluginDevScenarioResult(string Name, bool Succeeded, IReadOnlyList<string> Messages);

public interface IPluginDevRuntimeAdapter
{
    string Name { get; }
    bool SupportsReload { get; }
    bool SupportsPageAsset { get; }
    bool SupportsScenarios { get; }

    Task<IReadOnlyList<SearchItem>> SearchAsync(string query, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChapterItem>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken);
    Task<PageItem?> GetPageAsync(string mediaId, string chapterId, int index, CancellationToken cancellationToken);
    Task<IReadOnlyList<PageItem>> GetPagesAsync(string mediaId, string chapterId, int startIndex, int count, CancellationToken cancellationToken);
    Task<byte[]?> GetPageAssetAsync(string mediaId, string chapterId, CancellationToken cancellationToken);
    Task<string> ReloadAsync(CancellationToken cancellationToken);
}

public sealed class HostBridgeRuntimeAdapter(EmbeddedRuntime runtime, EmbeddedPagedMediaApi api) : IPluginDevRuntimeAdapter
{
    public string Name => "host-bridge";
    public bool SupportsReload => false;
    public bool SupportsPageAsset => true;
    public bool SupportsScenarios => true;

    public async Task<IReadOnlyList<SearchItem>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var response = await api.SearchAsync(new SearchRequest
        {
            Query = query,
            Context = PluginDevRequestContext.Create("cli")
        }, cancellationToken);

        if (response.OutcomeCase == SearchResponse.OutcomeOneofCase.Error)
        {
            throw new InvalidOperationException($"Search failed: {response.Error.Code} {response.Error.Message}");
        }

        return response.Result.Items.Select(static item => new SearchItem(
            item.Id,
            item.Source,
            item.Title,
            item.MediaType.ToString().ToLowerInvariant(),
            item.ThumbnailUrl,
            null,
            null)).ToArray();
    }

    public async Task<IReadOnlyList<ChapterItem>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken)
    {
        var response = await api.GetChaptersAsync(new ChaptersRequest
        {
            MediaId = mediaId,
            Context = PluginDevRequestContext.Create("cli")
        }, cancellationToken);

        if (response.OutcomeCase == ChaptersResponse.OutcomeOneofCase.Error)
        {
            throw new InvalidOperationException($"Chapters failed: {response.Error.Code} {response.Error.Message}");
        }

        return response.Result.Items.Select(static item => new ChapterItem(item.Id, item.Number, item.Title, item.UploaderGroups.ToArray())).ToArray();
    }

    public async Task<PageItem?> GetPageAsync(string mediaId, string chapterId, int index, CancellationToken cancellationToken)
    {
        var response = await api.GetPageAsync(new PageRequest
        {
            MediaId = mediaId,
            ChapterId = chapterId,
            Index = index,
            Context = PluginDevRequestContext.Create("cli")
        }, cancellationToken);

        if (response.OutcomeCase == PageResponse.OutcomeOneofCase.Error)
        {
            throw new InvalidOperationException($"Page failed: {response.Error.Code} {response.Error.Message}");
        }

        return new PageItem(chapterId, response.Page.Index, response.Page.ContentUri);
    }

    public async Task<IReadOnlyList<PageItem>> GetPagesAsync(string mediaId, string chapterId, int startIndex, int count, CancellationToken cancellationToken)
    {
        var pagesResult = await runtime.Pipeline.GetPagesAsync(MediaId.Create(mediaId), chapterId, startIndex, count, cancellationToken);
        return pagesResult.Pages.Select(static page => new PageItem(page.PageId, page.Index, page.ContentUri.ToString())).ToArray();
    }

    public async Task<byte[]?> GetPageAssetAsync(string mediaId, string chapterId, CancellationToken cancellationToken)
    {
        var response = await api.GetPageAssetAsync(new PageAssetRequest
        {
            MediaId = mediaId,
            ChapterId = chapterId,
            Context = PluginDevRequestContext.Create("cli")
        }, cancellationToken);

        if (response.OutcomeCase == PageAssetResponse.OutcomeOneofCase.Error)
        {
            throw new InvalidOperationException($"Page asset failed: {response.Error.Code} {response.Error.Message}");
        }

        return response.Asset.Payload.ToArray();
    }

    public Task<string> ReloadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult("Host-bridge runtime has no explicit reload path yet.");
    }
}

public sealed class WasmCliRuntimeAdapter : IPluginDevRuntimeAdapter
{
    private readonly string _rootDirectory;
    private readonly string? _projectPath;

    public WasmCliRuntimeAdapter(string rootDirectory, string? projectPath)
    {
        _rootDirectory = rootDirectory;
        _projectPath = projectPath;
    }

    public string Name => "wasm-cli-direct";
    public bool SupportsReload => true;
    public bool SupportsPageAsset => false;
    public bool SupportsScenarios => true;

    public async Task<IReadOnlyList<SearchItem>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var json = await InvokeAsync(PluginOperationNames.Search, [query], cancellationToken);
        return DeserializeSearchItems(json);
    }

    public async Task<IReadOnlyList<ChapterItem>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken)
    {
        var json = await InvokeAsync(PluginOperationNames.Chapters, [mediaId], cancellationToken);
        return DeserializeChapterItems(json);
    }

    public async Task<PageItem?> GetPageAsync(string mediaId, string chapterId, int index, CancellationToken cancellationToken)
    {
        var json = await InvokeAsync(PluginOperationNames.Page, [mediaId, chapterId, index.ToString()], cancellationToken);
        return DeserializePageItem(json);
    }

    public async Task<IReadOnlyList<PageItem>> GetPagesAsync(string mediaId, string chapterId, int startIndex, int count, CancellationToken cancellationToken)
    {
        var json = await InvokeAsync(PluginOperationNames.Pages, [mediaId, chapterId, startIndex.ToString(), count.ToString()], cancellationToken);
        return DeserializePageItems(json);
    }

    public Task<byte[]?> GetPageAssetAsync(string mediaId, string chapterId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("Direct WASM runtime does not expose page-asset retrieval yet.");
    }

    public Task<string> ReloadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult("WASM direct runtime is process-per-invocation; each command already runs against the latest project state.");
    }

    private async Task<string> InvokeAsync(string operation, IReadOnlyList<string> operationArgs, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_projectPath))
        {
            throw new InvalidOperationException("No plugin project was discovered for the active WASM profile.");
        }

        var args = new List<string>
        {
            "run",
            "--project",
            _projectPath,
            "-p:PluginTransport=Wasm",
            "--"
        };
        args.Add(operation);
        args.AddRange(operationArgs);

        var result = await PluginDevProcessRunner.RunAsync(
            workingDirectory: _rootDirectory,
            fileName: "dotnet",
            arguments: args,
            cancellationToken: cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Direct WASM runtime invocation failed for operation '{operation}'.\n{result.StandardError}");
        }

        return result.StandardOutput.Trim();
    }

    private static IReadOnlyList<SearchItem> DeserializeSearchItems(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize(json, PluginDevJsonContexts.Runtime.SearchItemArray) ?? [];
    }

    private static IReadOnlyList<ChapterItem> DeserializeChapterItems(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize(json, PluginDevJsonContexts.Runtime.ChapterItemArray) ?? [];
    }

    private static PageItem? DeserializePageItem(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        return JsonSerializer.Deserialize(json, PluginDevJsonContexts.Runtime.PageItem);
    }

    private static IReadOnlyList<PageItem> DeserializePageItems(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize(json, PluginDevJsonContexts.Runtime.PageItemArray) ?? [];
    }
}

public sealed class PluginDevBuildService
{
    public PluginDevBuildPlan? GetBuildPlan(PluginDevSession session)
    {
        if (session.Profile.RuntimeTarget != PluginRuntimeTarget.Wasm)
        {
            return null;
        }

        var projectPath = session.Discovery.ProjectFilePath;
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return null;
        }

        var publishDirectory = Path.Combine(session.Discovery.RootDirectory, "artifacts", "build-wasm", "publish");

        return new PluginDevBuildPlan(
            "wasm-build",
            session.Discovery.RootDirectory,
            "dotnet",
            [
                "publish",
                projectPath,
                "-c",
                "Release",
                "-r",
                "wasi-wasm",
                "--self-contained",
                "true",
                "-p:PublishAot=false",
                "-p:NativeCodeGen=none",
                "-p:DebugType=None",
                "-p:DebugSymbols=false",
                "-p:WasmSingleFileBundle=true",
                "-p:PluginTransport=Wasm",
                "-o",
                publishDirectory
            ],
            publishDirectory,
            "Normalized WASM publish plan for CLI-driven plugin development.");
    }

    public async Task<string> BuildAsync(PluginDevSession session, CancellationToken cancellationToken)
    {
        var plan = GetBuildPlan(session)
            ?? throw new InvalidOperationException("No normalized build plan is available for the active profile.");

        var result = await PluginDevProcessRunner.RunAsync(plan.WorkingDirectory, plan.Command, plan.Arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Build failed.\n{result.StandardError}");
        }

        return string.IsNullOrWhiteSpace(result.StandardOutput)
            ? $"Build completed for profile '{session.Profile.Name}'."
            : result.StandardOutput.Trim();
    }

    public PluginDevPackResult PackWasm(PluginDevSession session)
    {
        if (session.Profile.RuntimeTarget != PluginRuntimeTarget.Wasm)
        {
            throw new InvalidOperationException("The normalized pack flow is only implemented for the WASM profile in Phase 3.");
        }

        var manifestPath = session.Discovery.ManifestPath;
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            throw new InvalidOperationException("Cannot pack a WASM plugin without a discovered plugin manifest.");
        }

        var artifactPath = ResolveWasmArtifactPath(session)
            ?? throw new InvalidOperationException("No WASM artifact could be resolved for packing.");

        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var pluginId = manifestDoc.RootElement.GetProperty("id").GetString() ?? "plugin";
        var version = manifestDoc.RootElement.GetProperty("version").GetString() ?? "0.0.0";

        var packageRoot = Path.Combine(session.Discovery.RootDirectory, "artifacts", "pack", $"{version}-wasm");
        var manifestOutDir = Path.Combine(packageRoot, "manifest");
        var pluginOutDir = Path.Combine(packageRoot, pluginId, "wasm");
        var zipPath = Path.Combine(session.Discovery.RootDirectory, "artifacts", "pack", $"{pluginId}_{version}_wasm.zip");

        if (Directory.Exists(packageRoot))
        {
            Directory.Delete(packageRoot, recursive: true);
        }

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        Directory.CreateDirectory(manifestOutDir);
        Directory.CreateDirectory(pluginOutDir);

        var manifestOutPath = Path.Combine(manifestOutDir, $"{pluginId}.json");
        File.Copy(manifestPath, manifestOutPath, overwrite: true);

        var artifactOutPath = Path.Combine(pluginOutDir, "plugin.wasm");
        File.Copy(artifactPath, artifactOutPath, overwrite: true);

        ZipFile.CreateFromDirectory(packageRoot, zipPath);
        return new PluginDevPackResult(zipPath, manifestOutPath, artifactOutPath);
    }

    private static string? ResolveWasmArtifactPath(PluginDevSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.Profile.ArtifactPath))
        {
            var explicitArtifact = ResolveWasmFile(session.Profile.ArtifactPath);
            if (!string.IsNullOrWhiteSpace(explicitArtifact))
            {
                return explicitArtifact;
            }
        }

        foreach (var candidate in session.Discovery.ArtifactCandidates.Where(static candidate => candidate.Target == PluginRuntimeTarget.Wasm))
        {
            var resolved = ResolveWasmFile(candidate.Path);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    private static string? ResolveWasmFile(string path)
    {
        if (File.Exists(path) && path.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        var candidates = Directory.EnumerateFiles(path, "*.wasm", SearchOption.AllDirectories)
            .Where(static file => !file.EndsWith("dotnet.wasm", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return candidates.FirstOrDefault();
    }
}

public sealed class PluginDevScenarioRunner
{
    public async Task<PluginDevScenarioResult> RunAsync(PluginDevSession session, string scenarioName, string? query, CancellationToken cancellationToken)
    {
        var normalizedScenario = (scenarioName ?? string.Empty).Trim().ToLowerInvariant();
        return normalizedScenario switch
        {
            "paged-smoke" => await RunPagedSmokeAsync(session, string.IsNullOrWhiteSpace(query) ? "naruto" : query.Trim(), cancellationToken),
            _ => new PluginDevScenarioResult(normalizedScenario, false, [$"Unknown scenario '{scenarioName}'. Supported scenarios: paged-smoke"])
        };
    }

    private static async Task<PluginDevScenarioResult> RunPagedSmokeAsync(PluginDevSession session, string query, CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        var runtime = session.RuntimeAdapter;

        var searchItems = await runtime.SearchAsync(query, cancellationToken);
        messages.Add($"Search('{query}') returned {searchItems.Count} item(s).");
        if (searchItems.Count == 0)
        {
            return new PluginDevScenarioResult("paged-smoke", false, messages);
        }

        var firstItem = searchItems[0];
        messages.Add($"Selected media '{firstItem.title}' ({firstItem.id}).");

        var chapters = await runtime.GetChaptersAsync(firstItem.id, cancellationToken);
        messages.Add($"Chapters returned {chapters.Count} item(s).");
        if (chapters.Count == 0)
        {
            return new PluginDevScenarioResult("paged-smoke", false, messages);
        }

        var firstChapter = chapters[0];
        messages.Add($"Selected chapter '{firstChapter.title}' ({firstChapter.id}).");

        var page = await runtime.GetPageAsync(firstItem.id, firstChapter.id, 0, cancellationToken);
        if (page is null)
        {
            messages.Add("Page(0) returned no page.");
            return new PluginDevScenarioResult("paged-smoke", false, messages);
        }

        messages.Add($"Page(0) resolved content URI '{page.contentUri}'.");
        return new PluginDevScenarioResult("paged-smoke", true, messages);
    }
}

public static class PluginDevRequestContext
{
    public static ApiRequestContext Create(string clientId)
    {
        return new ApiRequestContext
        {
            CorrelationId = Guid.NewGuid().ToString("n"),
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O"),
            ClientId = clientId
        };
    }
}

public sealed record PluginDevProcessResult(int ExitCode, string StandardOutput, string StandardError);

public static class PluginDevProcessRunner
{
    public static async Task<PluginDevProcessResult> RunAsync(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new PluginDevProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
}
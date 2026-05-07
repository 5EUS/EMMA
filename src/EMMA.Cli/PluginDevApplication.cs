using System.Collections.ObjectModel;
using EMMA.Plugin.Common;

namespace EMMA.Cli;

public sealed record PluginDevLogEntry(DateTimeOffset TimestampUtc, string Level, string Message);

public sealed record PluginDevSessionSnapshot(
    string Id,
    PluginDevSessionState State,
    string WorkingDirectory,
    string RootDirectory,
    string? ManifestPath,
    string? ProjectFilePath,
    string PluginId,
    string? PluginName,
    string RuntimeAdapterName,
    bool SupportsPageAsset,
    PluginDevProfile Profile,
    IReadOnlyList<PluginDevProfile> AvailableProfiles,
    IReadOnlyList<string> MediaTypes,
    IReadOnlyList<PluginRuntimeTarget> SupportedTargets,
    IReadOnlyList<PluginDevArtifactCandidate> ArtifactCandidates,
    IReadOnlyList<PluginDevDiagnostic> Diagnostics);

public sealed class PluginDevApplication
{
    private readonly object _gate = new();
    private readonly PluginDevSessionFactory _sessionFactory;
    private readonly string _workingDirectory;
    private readonly List<PluginDevLogEntry> _logs = [];
    private PluginDevSession _session;

    public PluginDevApplication(PluginDevSessionFactory sessionFactory, string workingDirectory, PluginDevSession session)
    {
        _sessionFactory = sessionFactory;
        _workingDirectory = workingDirectory;
        _session = session;
        RecordInfo($"Session '{session.Id}' initialized for profile '{session.Profile.Name}'.");
    }

    public PluginDevSessionSnapshot GetSessionSnapshot()
    {
        lock (_gate)
        {
            return Snapshot(_session);
        }
    }

    public IReadOnlyList<PluginDevLogEntry> GetLogs()
    {
        lock (_gate)
        {
            return new ReadOnlyCollection<PluginDevLogEntry>(_logs.ToArray());
        }
    }

    public PluginDevSessionSnapshot SelectProfile(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new InvalidOperationException("A profile name is required.");
        }

        lock (_gate)
        {
            _session = _sessionFactory.Create(_workingDirectory, profileName.Trim());
            _session.TransitionTo(PluginDevSessionState.Prepared);
            PluginDevSessionHolder.SetCurrent(_session);
            _logs.Clear();
            RecordInfo($"Switched active profile to '{_session.Profile.Name}'.");
            return Snapshot(_session);
        }
    }

    public async Task<IReadOnlyList<SearchItem>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var session = RequireSession();
        RecordInfo($"Search requested for query '{query}'.");
        var result = await RunWithRuntimeLogsAsync(session, adapter => adapter.SearchAsync(query, cancellationToken));
        RecordInfo($"Search('{query}') returned {result.Count} item(s).");
        return result;
    }

    public async Task<IReadOnlyList<ChapterItem>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken)
    {
        var session = RequireSession();
        RecordInfo($"Chapters requested for media '{mediaId}'.");
        var result = await RunWithRuntimeLogsAsync(session, adapter => adapter.GetChaptersAsync(mediaId, cancellationToken));
        RecordInfo($"Chapters('{mediaId}') returned {result.Count} item(s).");
        return result;
    }

    public async Task<PageItem?> GetPageAsync(string mediaId, string chapterId, int index, CancellationToken cancellationToken)
    {
        var session = RequireSession();
        RecordInfo($"Page requested for media '{mediaId}', chapter '{chapterId}', index {index}.");
        var result = await RunWithRuntimeLogsAsync(session, adapter => adapter.GetPageAsync(mediaId, chapterId, index, cancellationToken));
        RecordInfo(result is null
            ? $"Page('{chapterId}', {index}) returned no content."
            : $"Page('{chapterId}', {index}) resolved '{result.contentUri}'.");
        return result;
    }

    public async Task<IReadOnlyList<PageItem>> GetPagesAsync(string mediaId, string chapterId, int startIndex, int count, CancellationToken cancellationToken)
    {
        var session = RequireSession();
        RecordInfo($"Pages requested for media '{mediaId}', chapter '{chapterId}', start {startIndex}, count {count}.");
        var result = await RunWithRuntimeLogsAsync(session, adapter => adapter.GetPagesAsync(mediaId, chapterId, startIndex, count, cancellationToken));
        RecordInfo($"Pages('{chapterId}', start={startIndex}, count={count}) returned {result.Count} item(s).");
        return result;
    }

    public async Task<byte[]?> GetPageAssetAsync(string mediaId, string chapterId, CancellationToken cancellationToken)
    {
        var session = RequireSession();
        RecordInfo($"Page asset requested for media '{mediaId}', chapter '{chapterId}'.");
        var result = await RunWithRuntimeLogsAsync(session, adapter => adapter.GetPageAssetAsync(mediaId, chapterId, cancellationToken));
        RecordInfo(result is null
            ? $"Page asset('{chapterId}') returned no payload."
            : $"Page asset('{chapterId}') returned {result.Length} byte(s).");
        return result;
    }

    public async Task<string> BuildAsync(CancellationToken cancellationToken)
    {
        var session = RequireSession();
        var plan = session.BuildService.GetBuildPlan(session)
            ?? throw new InvalidOperationException("No normalized build plan is available for the active profile.");

        RecordInfo($"Build started using plan '{plan.Name}'.");
        var output = await session.BuildService.BuildAsync(session, cancellationToken);
        RecordInfo($"Build completed using plan '{plan.Name}'.");
        RecordInfo(output);
        return output;
    }

    public PluginDevPackResult Pack()
    {
        var session = RequireSession();
        var result = session.BuildService.PackWasm(session);
        RecordInfo($"Pack completed: {result.PackagePath}");
        return result;
    }

    public async Task<string> ReloadAsync(CancellationToken cancellationToken)
    {
        var session = RequireSession();
        RecordInfo($"Reload requested for profile '{session.Profile.Name}'.");
        var message = await RunWithRuntimeLogsAsync(session, adapter => adapter.ReloadAsync(cancellationToken));
        RecordInfo(message);
        return message;
    }

    public async Task<PluginDevScenarioResult> RunScenarioAsync(string name, string? query, CancellationToken cancellationToken)
    {
        var session = RequireSession();
        RecordInfo($"Scenario '{name}' requested with query '{query ?? string.Empty}'.");
        var result = await RunWithRuntimeLogsAsync(session, _ => session.ScenarioRunner.RunAsync(session, name, query, cancellationToken));
        foreach (var message in result.Messages)
        {
            RecordInfo(message);
        }

        if (!result.Succeeded)
        {
            RecordError($"Scenario '{result.Name}' failed.");
        }

        return result;
    }

    public void RecordInfo(string message)
    {
        Record("info", message);
    }

    public void RecordError(string message)
    {
        Record("error", message);
    }

    private void Record(string level, string message)
    {
        lock (_gate)
        {
            _logs.Add(new PluginDevLogEntry(DateTimeOffset.UtcNow, level, message));
            if (_logs.Count > 500)
            {
                _logs.RemoveRange(0, _logs.Count - 500);
            }
        }
    }

    private PluginDevSession RequireSession()
    {
        lock (_gate)
        {
            return _session;
        }
    }

    private async Task<T> RunWithRuntimeLogsAsync<T>(PluginDevSession session, Func<IPluginDevRuntimeAdapter, Task<T>> action)
    {
        try
        {
            return await action(session.RuntimeAdapter);
        }
        finally
        {
            FlushRuntimeLogs(session.RuntimeAdapter);
        }
    }

    private void FlushRuntimeLogs(IPluginDevRuntimeAdapter adapter)
    {
        if (adapter is not IPluginDevRuntimeLogSource runtimeLogSource)
        {
            return;
        }

        foreach (var entry in runtimeLogSource.DrainRuntimeLogs())
        {
            Record(entry.Level, entry.Message);
        }
    }

    private static PluginDevSessionSnapshot Snapshot(PluginDevSession session)
    {
        return new PluginDevSessionSnapshot(
            session.Id,
            session.State,
            session.WorkingDirectory,
            session.Discovery.RootDirectory,
            session.Discovery.ManifestPath,
            session.Discovery.ProjectFilePath,
            session.Discovery.PluginId ?? session.Profile.PluginId,
            session.Discovery.PluginName,
            session.RuntimeAdapter.Name,
            session.RuntimeAdapter.SupportsPageAsset,
            session.Profile,
            session.AvailableProfiles,
            session.Discovery.MediaTypes,
            session.Discovery.SupportedTargets,
            session.Discovery.ArtifactCandidates,
            session.Diagnostics);
    }
}

public static class PluginDevApplicationHolder
{
    public static PluginDevApplication Current { get; private set; } = null!;

    public static void SetCurrent(PluginDevApplication application)
    {
        Current = application;
    }

    public static PluginDevApplication RequireCurrent()
    {
        return Current ?? throw new InvalidOperationException("No active plugin development application is configured.");
    }
}
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    IReadOnlyList<PluginDevDiagnostic> Diagnostics,
    PluginDevUiOptions Ui,
    IReadOnlyList<PluginDevScenarioDefinition> Scenarios,
    PluginDevWatchSnapshot Watch);

public sealed class PluginDevApplication
{
    private static readonly TimeSpan WatchBuildEchoSuppressionWindow = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly PluginDevSessionFactory _sessionFactory;
    private readonly PluginDevConfigLoader _configLoader = new();
    private readonly string _workingDirectory;
    private readonly List<PluginDevLogEntry> _logs = [];
    private readonly PluginDevWatchService _watchService = new();
    private PluginDevSession _session;
    private string? _lastWatchBuildArtifactPath;
    private DateTimeOffset? _lastWatchBuildUtc;

    public PluginDevApplication(PluginDevSessionFactory sessionFactory, string workingDirectory, PluginDevSession session)
    {
        _sessionFactory = sessionFactory;
        _workingDirectory = workingDirectory;
        _session = session;
        ApplyProfileEnvironment(session.Profile);
        RecordInfo($"Session '{session.Id}' initialized for profile '{session.Profile.Name}'.");
        RecordScenarioDiagnostics(session);
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

    public void ClearLogs()
    {
        lock (_gate)
        {
            _logs.Clear();
            _logs.Add(new PluginDevLogEntry(DateTimeOffset.UtcNow, "info", "Console cleared."));
        }
    }

    public PluginDevSessionSnapshot UpdateUiDiagnosticsLevel(string diagnosticsLevel)
    {
        var normalizedLevel = NormalizeDiagnosticsLevel(diagnosticsLevel);

        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_session.Profile.ConfigPath))
            {
                throw new InvalidOperationException("UI settings can only be saved when a plugin.dev.json file is resolved for the active profile.");
            }

            var configPath = _session.Profile.ConfigPath!;
            var restartWatch = _watchService.IsEnabled;
            _watchService.Stop();

            _configLoader.UpdateUiDiagnosticsLevel(configPath, normalizedLevel);

            _session = _sessionFactory.Create(_workingDirectory, _session.Profile.Name);
            _session.TransitionTo(PluginDevSessionState.Prepared);
            ApplyProfileEnvironment(_session.Profile);
            _lastWatchBuildArtifactPath = null;
            _lastWatchBuildUtc = null;
            PluginDevSessionHolder.SetCurrent(_session);
            _logs.Clear();
            RecordInfo($"Saved UI diagnostics level '{normalizedLevel}' to ignored UI state next to '{configPath}'.");
            RecordScenarioDiagnostics(_session);

            if (restartWatch)
            {
                var watch = StartWatchInternal(_session);
                RecordInfo($"Watch restarted for profile '{_session.Profile.Name}' ({watch.WatchGlobs.Count} glob(s)) after UI config update.");
            }

            return Snapshot(_session);
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
            var restartWatch = _watchService.IsEnabled;
            _watchService.Stop();
            _session = _sessionFactory.Create(_workingDirectory, profileName.Trim());
            _session.TransitionTo(PluginDevSessionState.Prepared);
            ApplyProfileEnvironment(_session.Profile);
            _lastWatchBuildArtifactPath = null;
            _lastWatchBuildUtc = null;
            PluginDevSessionHolder.SetCurrent(_session);
            _logs.Clear();
            RecordInfo($"Switched active profile to '{_session.Profile.Name}'.");
            RecordScenarioDiagnostics(_session);

            if (restartWatch)
            {
                var watch = StartWatchInternal(_session);
                RecordInfo($"Watch restarted for profile '{_session.Profile.Name}' ({watch.WatchGlobs.Count} glob(s)).");
            }

            return Snapshot(_session);
        }
    }

    public PluginDevWatchSnapshot StartWatch()
    {
        var session = RequireSession();
        var snapshot = StartWatchInternal(session);
        RecordInfo($"Watch started for profile '{session.Profile.Name}'.");
        return snapshot;
    }

    public PluginDevWatchSnapshot StopWatch()
    {
        var snapshot = _watchService.Stop();
        RecordInfo("Watch stopped.");
        return snapshot;
    }

    public PluginDevWatchSnapshot GetWatchSnapshot()
    {
        return GetEffectiveWatchSnapshot(RequireSession());
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

    public async Task<IReadOnlyList<PluginDevVideoStream>> GetVideoStreamsAsync(string mediaId, CancellationToken cancellationToken)
    {
        var session = RequireSession();
        RecordInfo($"Video streams requested for media '{mediaId}'.");
        var result = await RunWithRuntimeLogsAsync(session, adapter => adapter.GetVideoStreamsAsync(mediaId, cancellationToken));
        RecordInfo($"VideoStreams('{mediaId}') returned {result.Count} item(s).");
        return result;
    }

    public async Task<PluginDevVideoSegment?> GetVideoSegmentAsync(string mediaId, string streamId, int sequence, CancellationToken cancellationToken)
    {
        var session = RequireSession();
        RecordInfo($"Video segment requested for media '{mediaId}', stream '{streamId}', sequence {sequence}.");
        var result = await RunWithRuntimeLogsAsync(session, adapter => adapter.GetVideoSegmentAsync(mediaId, streamId, sequence, cancellationToken));
        RecordInfo(result is null
            ? $"VideoSegment('{mediaId}', '{streamId}', {sequence}) returned no payload."
            : $"VideoSegment('{mediaId}', '{streamId}', {sequence}) returned {result.SizeBytes} byte(s) as '{result.ContentType}'.");
        return result;
    }

    public async Task<string> BuildAsync(CancellationToken cancellationToken)
    {
        var session = RequireSession();
        var plan = session.BuildService.GetBuildPlan(session)
            ?? throw new InvalidOperationException("No normalized build plan is available for the active profile.");

        RecordInfo($"Build started using plan '{plan.Name}'.");
        try
        {
            var output = await session.BuildService.BuildAsync(session, cancellationToken);
            var syncMessage = session.BuildService.SyncBuildArtifacts(session);
            RecordInfo(output);
            RecordInfo($"Build completed using plan '{plan.Name}'.");
            if (!string.IsNullOrWhiteSpace(syncMessage))
            {
                RecordInfo(syncMessage);
                return $"{output}\n{syncMessage}";
            }

            return output;
        }
        catch (PluginDevBuildException ex)
        {
            RecordError(ex.Message);
            throw;
        }
    }

    public PluginDevPackResult Pack()
    {
        var session = RequireSession();
        var result = session.BuildService.PackCurrentProfile(session);
        RecordInfo($"Pack completed: {result.PackagePath}");
        return result;
    }

    public string OpenPackDirectory()
    {
        var session = RequireSession();
        var packDirectory = session.BuildService.GetPackDirectory(session);
        Directory.CreateDirectory(packDirectory);

        var command = GetOpenDirectoryCommand(packDirectory);
        using var process = Process.Start(command.startInfo)
            ?? throw new InvalidOperationException($"Failed to open pack directory '{packDirectory}'.");

        RecordInfo($"Opened pack directory: {packDirectory}");
        return packDirectory;
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

    private void RecordScenarioDiagnostics(PluginDevSession session)
    {
        foreach (var diagnostic in session.Diagnostics.Where(static item => item.Type == "scenarios" && item.Severity != PluginDevDiagnosticSeverity.Info))
        {
            Record(diagnostic.Severity, $"{diagnostic.Code}: {diagnostic.Message}");
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

    private PluginDevWatchSnapshot StartWatchInternal(PluginDevSession session)
    {
        return _watchService.Start(session, trigger => HandleWatchTriggerAsync(session.Id, trigger));
    }

    private async Task<string> HandleWatchTriggerAsync(string sessionId, PluginDevWatchTrigger trigger)
    {
        PluginDevSession session;
        lock (_gate)
        {
            if (!string.Equals(_session.Id, sessionId, StringComparison.Ordinal))
            {
                return "Watch trigger ignored because the active profile changed.";
            }

            session = _session;
            session.TransitionTo(PluginDevSessionState.Reloading);
        }

        var relativePath = GetWatchDisplayPath(session, trigger.ChangedPath);
        RecordInfo($"Watch detected {trigger.EventCount} change(s). Last change: {relativePath}");

        try
        {
            if (ShouldIgnoreWatchTrigger(session, trigger.ChangedPath, trigger.ChangedUtc))
            {
                var ignoreMessage = $"Watch ignored build output echo for '{relativePath}'.";
                RecordInfo(ignoreMessage);
                return ignoreMessage;
            }

            string? refreshMessage = null;
            if (ShouldRefreshSessionConfiguration(trigger.ChangedPath))
            {
                refreshMessage = RefreshSessionConfiguration(session);
                RecordInfo(refreshMessage);
            }

            if (!session.RuntimeAdapter.SupportsReload)
            {
                var noReloadMessage = $"Watch observed '{relativePath}', but runtime adapter '{session.RuntimeAdapter.Name}' does not support explicit reload.";
                RecordInfo(noReloadMessage);
                return refreshMessage is null
                    ? noReloadMessage
                    : $"{refreshMessage}\n{noReloadMessage}";
            }

            string? buildMessage = null;
            if (ShouldBuildOnWatch(session, trigger.ChangedPath))
            {
                var plan = session.BuildService.GetBuildPlan(session);
                if (plan is not null)
                {
                    RecordInfo($"Watch build started using plan '{plan.Name}'.");
                    var buildOutput = await session.BuildService.BuildAsync(session, CancellationToken.None);
                    var syncMessage = session.BuildService.SyncBuildArtifacts(session);
                    RememberWatchBuildArtifactPath(plan.ArtifactPath ?? session.Profile.ArtifactPath);
                    var buildParts = new List<string>(3);
                    if (!string.IsNullOrWhiteSpace(buildOutput))
                    {
                        buildParts.Add(buildOutput);
                    }

                    buildParts.Add($"Watch build completed using plan '{plan.Name}'.");
                    if (!string.IsNullOrWhiteSpace(syncMessage))
                    {
                        buildParts.Add(syncMessage);
                    }

                    buildMessage = string.Join("\n", buildParts);
                    RecordInfo(buildMessage);
                }
            }

            var message = await RunWithRuntimeLogsAsync(session, adapter => adapter.ReloadAsync(CancellationToken.None));
            var combinedMessageParts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(refreshMessage))
            {
                combinedMessageParts.Add(refreshMessage);
            }

            if (!string.IsNullOrWhiteSpace(buildMessage))
            {
                combinedMessageParts.Add(buildMessage);
            }

            combinedMessageParts.Add(message);
            var combinedMessage = string.Join("\n", combinedMessageParts);
            RecordInfo($"Watch reload completed: {message}");
            return combinedMessage;
        }
        catch (Exception ex)
        {
            RecordError($"Watch build/reload failed:\n{ex.Message}");
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (string.Equals(_session.Id, sessionId, StringComparison.Ordinal)
                    && _session.State != PluginDevSessionState.Failed)
                {
                    _session.TransitionTo(PluginDevSessionState.Running);
                }
            }
        }
    }

    private static string GetWatchDisplayPath(PluginDevSession session, string changedPath)
    {
        try
        {
            return Path.GetRelativePath(session.Discovery.RootDirectory, changedPath);
        }
        catch
        {
            return changedPath;
        }
    }

    private static void ApplyProfileEnvironment(PluginDevProfile profile)
    {
        Environment.SetEnvironmentVariable("EMMA_PLUGIN_DEV_MODE", profile.Logging.Plugin ? "1" : "0");
    }

    private static (ProcessStartInfo startInfo, string commandName) GetOpenDirectoryCommand(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return (new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { directory },
                UseShellExecute = true
            }, "explorer.exe");
        }

        if (OperatingSystem.IsMacOS())
        {
            return (new ProcessStartInfo
            {
                FileName = "open",
                ArgumentList = { directory },
                UseShellExecute = false
            }, "open");
        }

        return (new ProcessStartInfo
        {
            FileName = "xdg-open",
            ArgumentList = { directory },
            UseShellExecute = false
        }, "xdg-open");
    }

    private static bool ShouldBuildOnWatch(PluginDevSession session, string changedPath)
    {
        var fullChangedPath = Path.GetFullPath(changedPath);

        if (!string.IsNullOrWhiteSpace(session.Profile.ConfigPath)
            && string.Equals(Path.GetFullPath(session.Profile.ConfigPath), fullChangedPath, StringComparison.Ordinal))
        {
            return false;
        }

        if (ShouldRefreshSessionConfiguration(fullChangedPath))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(session.Profile.ArtifactPath))
        {
            return true;
        }

        var artifactPath = Path.GetFullPath(session.Profile.ArtifactPath);
        if (File.Exists(artifactPath))
        {
            return !string.Equals(artifactPath, fullChangedPath, StringComparison.Ordinal);
        }

        if (Directory.Exists(artifactPath))
        {
            var normalizedArtifactPath = artifactPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return !fullChangedPath.StartsWith(normalizedArtifactPath, StringComparison.Ordinal);
        }

        return true;
    }

    private bool ShouldIgnoreWatchTrigger(PluginDevSession session, string changedPath, DateTimeOffset changedUtc)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_lastWatchBuildArtifactPath)
                || _lastWatchBuildUtc is null
                || changedUtc - _lastWatchBuildUtc > WatchBuildEchoSuppressionWindow)
            {
                return false;
            }

            var artifactPath = Path.GetFullPath(_lastWatchBuildArtifactPath);
            var fullChangedPath = Path.GetFullPath(changedPath);

            if (File.Exists(artifactPath))
            {
                return string.Equals(artifactPath, fullChangedPath, StringComparison.Ordinal);
            }

            if (!Directory.Exists(artifactPath))
            {
                return false;
            }

            var normalizedArtifactPath = artifactPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullChangedPath.StartsWith(normalizedArtifactPath, StringComparison.Ordinal);
        }
    }

    private void RememberWatchBuildArtifactPath(string? artifactPath)
    {
        lock (_gate)
        {
            _lastWatchBuildArtifactPath = artifactPath;
            _lastWatchBuildUtc = artifactPath is null ? null : DateTimeOffset.UtcNow;
        }
    }

    private string RefreshSessionConfiguration(PluginDevSession session)
    {
        var refreshed = _sessionFactory.Create(_workingDirectory, session.Profile.Name);
        session.RefreshConfigurationFrom(refreshed);
        ApplyProfileEnvironment(session.Profile);
        PluginDevSessionHolder.SetCurrent(session);
        RecordScenarioDiagnostics(session);

        if (_watchService.IsEnabled)
        {
            StartWatchInternal(session);
        }

        return $"Watch refreshed config-backed session state for profile '{session.Profile.Name}' with {session.ConfiguredScenarios.Count} custom scenario(s).";
    }

    private static bool ShouldRefreshSessionConfiguration(string changedPath)
    {
        return string.Equals(Path.GetExtension(changedPath), ".json", StringComparison.OrdinalIgnoreCase);
    }

    private PluginDevSessionSnapshot Snapshot(PluginDevSession session)
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
            session.Diagnostics,
            session.Ui,
            session.ScenarioRunner.GetAvailableScenarios(session),
            GetEffectiveWatchSnapshot(session));
    }

    private static string NormalizeDiagnosticsLevel(string? diagnosticsLevel)
    {
        return diagnosticsLevel?.Trim().ToLowerInvariant() switch
        {
            PluginDevDiagnosticSeverity.Info => PluginDevDiagnosticSeverity.Info,
            PluginDevDiagnosticSeverity.Warning => PluginDevDiagnosticSeverity.Warning,
            PluginDevDiagnosticSeverity.Error => PluginDevDiagnosticSeverity.Error,
            _ => throw new InvalidOperationException("Diagnostics level must be one of: info, warning, error.")
        };
    }

    private PluginDevWatchSnapshot GetEffectiveWatchSnapshot(PluginDevSession session)
    {
        var snapshot = _watchService.GetSnapshot();
        if (snapshot.IsEnabled)
        {
            return snapshot;
        }

        return snapshot with
        {
            CanWatch = session.Profile.WatchGlobs.Count > 0 || !string.IsNullOrWhiteSpace(session.Profile.ConfigPath),
            SupportsReload = session.RuntimeAdapter.SupportsReload,
            Behavior = DescribeWatchBehavior(session),
            WatchGlobs = session.Profile.WatchGlobs
        };
    }

    private static string DescribeWatchBehavior(PluginDevSession session)
    {
        if (!session.RuntimeAdapter.SupportsReload)
        {
            return $"Watch can observe matching changes, but runtime adapter '{session.RuntimeAdapter.Name}' does not support explicit reload.";
        }

        return session.Profile.RuntimeTarget switch
        {
            PluginRuntimeTarget.Wasm => "Watch will debounce matching changes and request a WASM runtime refresh. Source changes still require a rebuild before updated artifacts can be observed.",
            PluginRuntimeTarget.Linux or PluginRuntimeTarget.Windows => "Watch will debounce matching changes and restart the managed native runtime after each change batch.",
            _ => $"Watch will debounce matching changes and request reload through '{session.RuntimeAdapter.Name}'."
        };
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
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace EMMA.Cli;

public sealed record PluginDevWatchSnapshot(
    bool IsEnabled,
    bool CanWatch,
    bool SupportsReload,
    string Status,
    string Behavior,
    IReadOnlyList<string> WatchGlobs,
    int PendingChangeCount,
    int ReloadCount,
    string? LastChangedPath,
    DateTimeOffset? LastChangedUtc,
    string? LastReloadMessage,
    DateTimeOffset? LastReloadUtc);

public sealed record PluginDevWatchTrigger(
    string SessionId,
    string ChangedPath,
    int EventCount,
    DateTimeOffset ChangedUtc);

public sealed class PluginDevWatchService : IDisposable
{
    private static readonly ConcurrentDictionary<string, Regex> GlobRegexCache = new(StringComparer.Ordinal);
    private static readonly StringComparison PathComparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly Lock _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly TimeSpan _debounceDelay = TimeSpan.FromMilliseconds(750);

    private Timer? _debounceTimer;
    private PluginDevSession? _session;
    private Func<PluginDevWatchTrigger, Task<string>>? _onTriggered;
    private bool _reloadInProgress;
    private int _pendingEvents;
    private string? _pendingPath;
    private DateTimeOffset? _pendingUtc;
    private PluginDevWatchSnapshot _snapshot = DisabledSnapshot(false, [], "Watch is not active.");

    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _snapshot.IsEnabled;
            }
        }
    }

    public PluginDevWatchSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    public PluginDevWatchSnapshot Start(PluginDevSession session, Func<PluginDevWatchTrigger, Task<string>> onTriggered)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(onTriggered);

        var watchGlobs = session.Profile.WatchGlobs
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => NormalizePattern(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var behavior = DescribeBehavior(session);
        var rootDirectory = session.Discovery.RootDirectory;
        var configPath = session.Profile.ConfigPath;

        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            throw new InvalidOperationException("Watch requires a discovered project root directory.");
        }

        if (watchGlobs.Length == 0 && string.IsNullOrWhiteSpace(configPath))
        {
            throw new InvalidOperationException("Watch requires at least one configured watch glob or a plugin.dev.json file to monitor.");
        }

        var watcherSpecs = BuildWatcherSpecs(rootDirectory, configPath);

        lock (_gate)
        {
            StopInternal(updateSnapshot: false);

            _session = session;
            _onTriggered = onTriggered;
            _pendingEvents = 0;
            _pendingPath = null;
            _pendingUtc = null;
            _reloadInProgress = false;
            _debounceTimer = new Timer(static state => ((PluginDevWatchService)state!).OnDebounceElapsed(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            foreach (var spec in watcherSpecs)
            {
                var watcher = new FileSystemWatcher(spec.Directory)
                {
                    IncludeSubdirectories = spec.IncludeSubdirectories,
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.DirectoryName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.CreationTime
                        | NotifyFilters.Size
                };

                watcher.Changed += OnFileSystemEvent;
                watcher.Created += OnFileSystemEvent;
                watcher.Deleted += OnFileSystemEvent;
                watcher.Renamed += OnRenameEvent;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }

            _snapshot = new PluginDevWatchSnapshot(
                true,
                true,
                session.RuntimeAdapter.SupportsReload,
                "watching",
                behavior,
                watchGlobs,
                0,
                _snapshot.ReloadCount,
                _snapshot.LastChangedPath,
                _snapshot.LastChangedUtc,
                _snapshot.LastReloadMessage,
                _snapshot.LastReloadUtc);

            return _snapshot;
        }
    }

    public PluginDevWatchSnapshot Stop()
    {
        lock (_gate)
        {
            StopInternal(updateSnapshot: true);
            return _snapshot;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopInternal(updateSnapshot: false);
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs args)
    {
        QueueEvent(args.FullPath);
    }

    private void OnRenameEvent(object sender, RenamedEventArgs args)
    {
        QueueEvent(args.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Status = "error",
                LastReloadMessage = args.GetException().Message,
                LastReloadUtc = DateTimeOffset.UtcNow
            };
        }
    }

    private void QueueEvent(string changedPath)
    {
        lock (_gate)
        {
            if (_session is null || !_snapshot.IsEnabled)
            {
                return;
            }

            if (!ShouldWatchPath(_session, changedPath, _snapshot.WatchGlobs))
            {
                return;
            }

            _pendingEvents++;
            _pendingPath = changedPath;
            _pendingUtc = DateTimeOffset.UtcNow;
            _snapshot = _snapshot with
            {
                Status = _reloadInProgress ? "reload-pending" : "change-detected",
                PendingChangeCount = _pendingEvents,
                LastChangedPath = changedPath,
                LastChangedUtc = _pendingUtc
            };

            _debounceTimer?.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDebounceElapsed()
    {
        PluginDevWatchTrigger? trigger = null;
        Func<PluginDevWatchTrigger, Task<string>>? callback = null;

        lock (_gate)
        {
            if (_session is null || !_snapshot.IsEnabled || _pendingEvents == 0)
            {
                return;
            }

            if (_reloadInProgress)
            {
                _debounceTimer?.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
                return;
            }

            _reloadInProgress = true;
            callback = _onTriggered;
            trigger = new PluginDevWatchTrigger(
                _session.Id,
                _pendingPath ?? _session.WorkingDirectory,
                _pendingEvents,
                _pendingUtc ?? DateTimeOffset.UtcNow);

            _pendingEvents = 0;
            _pendingPath = null;
            _pendingUtc = null;
            _snapshot = _snapshot with
            {
                Status = "reloading",
                PendingChangeCount = 0
            };
        }

        if (trigger is null || callback is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var message = await callback(trigger);
                lock (_gate)
                {
                    _snapshot = _snapshot with
                    {
                        Status = "watching",
                        ReloadCount = _snapshot.ReloadCount + 1,
                        LastReloadMessage = message,
                        LastReloadUtc = DateTimeOffset.UtcNow
                    };
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _snapshot = _snapshot with
                    {
                        Status = "error",
                        LastReloadMessage = ex.Message,
                        LastReloadUtc = DateTimeOffset.UtcNow
                    };
                }
            }
            finally
            {
                lock (_gate)
                {
                    _reloadInProgress = false;
                    if (_snapshot.IsEnabled && _pendingEvents > 0)
                    {
                        _debounceTimer?.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
                    }
                }
            }
        });
    }

    private void StopInternal(bool updateSnapshot)
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnFileSystemEvent;
            watcher.Created -= OnFileSystemEvent;
            watcher.Deleted -= OnFileSystemEvent;
            watcher.Renamed -= OnRenameEvent;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
        }

        _watchers.Clear();
        _debounceTimer?.Dispose();
        _debounceTimer = null;
        _session = null;
        _onTriggered = null;
        _pendingEvents = 0;
        _pendingPath = null;
        _pendingUtc = null;
        _reloadInProgress = false;

        if (updateSnapshot)
        {
            _snapshot = _snapshot with
            {
                IsEnabled = false,
                Status = "stopped",
                PendingChangeCount = 0,
                LastReloadMessage = _snapshot.LastReloadMessage ?? "Watch stopped."
            };
        }
    }

    private static IReadOnlyList<WatcherSpec> BuildWatcherSpecs(string rootDirectory, string? configPath)
    {
        var specs = new Dictionary<string, WatcherSpec>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetFullPath(rootDirectory)] = new WatcherSpec(Path.GetFullPath(rootDirectory), true)
        };

        if (!string.IsNullOrWhiteSpace(configPath))
        {
            var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            if (!string.IsNullOrWhiteSpace(configDirectory)
                && !specs.ContainsKey(configDirectory))
            {
                specs[configDirectory] = new WatcherSpec(configDirectory, false);
            }
        }

        return specs.Values.ToArray();
    }

    private static bool ShouldWatchPath(PluginDevSession session, string changedPath, IReadOnlyList<string> watchGlobs)
    {
        var fullChangedPath = Path.GetFullPath(changedPath);
        if (!string.IsNullOrWhiteSpace(session.Profile.ConfigPath)
            && string.Equals(Path.GetFullPath(session.Profile.ConfigPath), fullChangedPath, PathComparison))
        {
            return true;
        }

        if (watchGlobs.Count == 0)
        {
            return false;
        }

        var root = Path.GetFullPath(session.Discovery.RootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!fullChangedPath.StartsWith(root, PathComparison))
        {
            return false;
        }

        var relativePath = Path.GetRelativePath(root, fullChangedPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        foreach (var pattern in watchGlobs)
        {
            var regex = GlobRegexCache.GetOrAdd(pattern, CreateRegex);
            if (regex.IsMatch(relativePath))
            {
                return true;
            }
        }

        return false;
    }

    private static Regex CreateRegex(string pattern)
    {
        var normalized = NormalizePattern(pattern);
        var escaped = Regex.Escape(normalized)
            .Replace(@"\*\*/", @"(?:.*/)?")
            .Replace(@"\*\*", @".*")
            .Replace(@"\*", @"[^/]*")
            .Replace(@"\?", @"[^/]");
        return new Regex($"^{escaped}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    private static string NormalizePattern(string value)
    {
        return value.Trim()
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string DescribeBehavior(PluginDevSession session)
    {
        if (!session.RuntimeAdapter.SupportsReload)
        {
            return $"Watch will record matching changes, but runtime adapter '{session.RuntimeAdapter.Name}' does not support explicit reload.";
        }

        return session.Profile.RuntimeTarget switch
        {
            PluginRuntimeTarget.Wasm => "Watch will debounce matching changes and request a WASM runtime refresh. Source changes still require a rebuild before the next invocation can observe updated artifacts.",
            PluginRuntimeTarget.Linux or PluginRuntimeTarget.Windows => "Watch will debounce matching changes and restart the managed native runtime after each change batch.",
            _ => $"Watch will debounce matching changes and request reload through '{session.RuntimeAdapter.Name}'."
        };
    }

    private static PluginDevWatchSnapshot DisabledSnapshot(bool canWatch, IReadOnlyList<string> watchGlobs, string behavior)
    {
        return new PluginDevWatchSnapshot(
            false,
            canWatch,
            false,
            "stopped",
            behavior,
            watchGlobs,
            0,
            0,
            null,
            null,
            null,
            null);
    }

    private sealed record WatcherSpec(string Directory, bool IncludeSubdirectories);
}
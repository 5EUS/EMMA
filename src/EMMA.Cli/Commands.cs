using ConsoleAppFramework;
using EMMA.Plugin.Common;

namespace EMMA.Cli;

public class MyCommands
{
    private readonly PluginDevApplication _application;

    public MyCommands()
    {
        _application = PluginDevApplicationHolder.RequireCurrent();
    }

    /// <summary>
    /// Search command test.
    /// </summary>
    /// <param name="query">-q, Search query.</param>
    [Command("search")]
    public async Task SearchAsync(string query)
    {
        var response = await _application.SearchAsync(query, CancellationToken.None);

        foreach (var item in response)
        {
            CommandContextHolder.Context.AddResult(new SimpleResult(
                $"{item.title} ({item.id})",
                [
                    new ResultAction
                {
                    Name = "Open",
                    Execute = async () =>
                    {
                        Console.WriteLine($"Fetching media chapters (id: {item.id})...");
                        await ChaptersAsync(item.id);
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
        var response = await _application.GetChaptersAsync(id, CancellationToken.None);
        foreach (var item in response)
        {
            CommandContextHolder.Context.AddResult(new SimpleResult(
                $"{item.title} ({item.id})",
                [
                    new ResultAction
                    {
                        Name = "Open",
                        Execute = async () =>
                        {
                            Console.WriteLine($"Fetching chapter pages (media id: {id}, chapter id: {item.id})...");
                            await PageAsync(id, item.id, 0);
                        }
                    },
                    new ResultAction
                    {
                        Name = "Pages",
                        Execute = async () =>
                        {
                            Console.WriteLine($"Fetching pages list (media id: {id}, chapter id: {item.id})...");
                            try
                            {
                                var startIndex = 0;
                                var pagesResult = await _application.GetPagesAsync(id, item.id, startIndex, 10000, CancellationToken.None);
                                for (int p = 0; p < pagesResult.Count; p++)
                                {
                                    var pageIndex = startIndex + p;
                                    CommandContextHolder.Context.AddResult(new SimpleResult(
                                        $"Page {pageIndex}",
                                        [
                                            new ResultAction
                                            {
                                                Name = "Open",
                                                Execute = async () =>
                                                {
                                                    await PageAsync(id, item.id, pageIndex);
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
        var session = _application.GetSessionSnapshot();
        if (!session.SupportsPageAsset)
        {
            Console.WriteLine($"Page asset is not supported by runtime adapter '{session.RuntimeAdapterName}'.");
            return;
        }

        var response = await _application.GetPageAssetAsync(mid, cid, CancellationToken.None);
        if (response is null)
        {
            Console.WriteLine("Page asset returned no payload.");
            return;
        }

        CommandContextHolder.Context.AddResult(new SimpleResult(
            $"Page Asset (media: {mid}, chapter: {cid})",
            [
                new ResultAction
                {
                    Name = "Show Base64",
                    Execute = () =>
                    {
                        Console.WriteLine($"Content Base64: {Convert.ToBase64String(response)}");
                        return Task.CompletedTask;
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
        var response = await _application.GetPageAsync(mid, cid, index, CancellationToken.None);

        if (response is null)
        {
            Console.WriteLine("Page returned no content.");
            return;
        }

        Console.WriteLine($"Content URI: {response.contentUri}");
        Console.WriteLine($"Index: {response.index}");

    }

    /// <summary>
    /// Shows the current resolved plugin development session.
    /// </summary>
    [Command("session")]
    public void Session()
    {
        var session = _application.GetSessionSnapshot();

        Console.WriteLine($"Session ID: {session.Id}");
        Console.WriteLine($"State: {session.State}");
        Console.WriteLine($"Working Directory: {session.WorkingDirectory}");
        Console.WriteLine($"Profile: {session.Profile.Name}");
        Console.WriteLine($"Plugin ID: {session.Profile.PluginId}");
        Console.WriteLine($"Host URL: {session.Profile.HostUrl}");
        Console.WriteLine($"Runtime Target: {session.Profile.RuntimeTarget}");
        Console.WriteLine($"Execution Mode: {session.Profile.ExecutionMode}");
        Console.WriteLine($"Logging: plugin={(session.Profile.Logging.Plugin ? "on" : "off")}, aspNetHost={(session.Profile.Logging.AspNetHost ? "on" : "off")}, httpClient={(session.Profile.Logging.HttpClient ? "on" : "off")}");
        if (session.Profile.Sync.Enabled)
        {
            Console.WriteLine($"Sync: onBuild={(session.Profile.Sync.OnBuild ? "on" : "off")}, cleanDestination={(session.Profile.Sync.CleanDestination ? "on" : "off")}, destination={session.Profile.Sync.DestinationPath}");
        }
        if (!string.IsNullOrWhiteSpace(session.Profile.WasiSdkPath))
        {
            Console.WriteLine($"WASI SDK Path: {session.Profile.WasiSdkPath}");
        }
        Console.WriteLine($"Runtime Adapter: {session.RuntimeAdapterName}");
        Console.WriteLine($"Profile Source: {(session.Profile.IsInferred ? "inferred" : "configured")}");

        if (!string.IsNullOrWhiteSpace(session.Profile.ArtifactPath))
        {
            Console.WriteLine($"Artifact Path: {session.Profile.ArtifactPath}");
        }

        if (session.Profile.WatchGlobs.Count > 0)
        {
            Console.WriteLine($"Watch Globs: {string.Join(", ", session.Profile.WatchGlobs)}");
        }

        Console.WriteLine($"Watch Status: {session.Watch.Status}");
        Console.WriteLine($"Watch Behavior: {session.Watch.Behavior}");

        if (session.Watch.LastChangedUtc is not null)
        {
            Console.WriteLine($"Watch Last Change: {session.Watch.LastChangedUtc:O} ({session.Watch.LastChangedPath})");
        }

        if (session.Watch.LastReloadUtc is not null)
        {
            Console.WriteLine($"Watch Last Reload: {session.Watch.LastReloadUtc:O} ({session.Watch.LastReloadMessage})");
        }

        Console.WriteLine($"Discovery Root: {session.RootDirectory}");
        Console.WriteLine($"Manifest: {session.ManifestPath ?? "<not found>"}");
        Console.WriteLine($"Project: {session.ProjectFilePath ?? "<not found>"}");

        if (session.AvailableProfiles.Count > 0)
        {
            Console.WriteLine("Available Profiles:");
            foreach (var profile in session.AvailableProfiles)
            {
                var source = profile.IsInferred ? "inferred" : "configured";
                var artifactSuffix = string.IsNullOrWhiteSpace(profile.ArtifactPath) ? string.Empty : $" artifact={profile.ArtifactPath}";
                var syncSuffix = profile.Sync.Enabled ? $" sync={profile.Sync.DestinationPath}" : string.Empty;
                Console.WriteLine($"  - {profile.Name} [{source}] target={profile.RuntimeTarget} mode={profile.ExecutionMode}{artifactSuffix}{syncSuffix}");
            }
        }

        if (session.Diagnostics.Count == 0)
        {
            return;
        }

        Console.WriteLine("Diagnostics:");
        foreach (var diagnostic in session.Diagnostics)
        {
            PluginDevConsoleTheme.WriteDiagnostic("  ", diagnostic);
        }
    }

    [Command("build")]
    public async Task BuildAsync([Argument] string? scope = null)
    {
        if (IsAllScope(scope))
        {
            await ExecuteAcrossProfilesAsync(async profile =>
            {
                var session = PluginDevSessionHolder.RequireCurrent();
                var plan = session.BuildService.GetBuildPlan(session);
                if (plan is null)
                {
                    Console.WriteLine($"[{profile.Name}] No normalized build plan is available.");
                    return;
                }

                Console.WriteLine($"[{profile.Name}] Running build plan '{plan.Name}'...");
                var output = await _application.BuildAsync(CancellationToken.None);
                Console.WriteLine(output);
            });

            return;
        }

        var session = PluginDevSessionHolder.RequireCurrent();
        var plan = session.BuildService.GetBuildPlan(session);
        if (plan is null)
        {
            Console.WriteLine("No normalized build plan is available for the active profile.");
            return;
        }

        Console.WriteLine($"Running build plan '{plan.Name}'...");
        var output = await _application.BuildAsync(CancellationToken.None);
        Console.WriteLine(output);
    }

    [Command("pack")]
    public async Task PackAsync([Argument] string? scope = null)
    {
        if (IsAllScope(scope))
        {
            await ExecuteAcrossProfilesAsync(profile =>
            {
                var result = _application.Pack();
                WritePackResult(profile.Name, result);
                return Task.CompletedTask;
            });

            return;
        }

        var result = _application.Pack();
        WritePackResult(null, result);
    }

    [Command("build-pack")]
    public async Task BuildPackAsync([Argument] string? scope = null)
    {
        if (IsAllScope(scope))
        {
            await ExecuteAcrossProfilesAsync(async profile =>
            {
                var session = PluginDevSessionHolder.RequireCurrent();
                var plan = session.BuildService.GetBuildPlan(session);
                if (plan is null)
                {
                    Console.WriteLine($"[{profile.Name}] No normalized build plan is available.");
                    return;
                }

                Console.WriteLine($"[{profile.Name}] Running build plan '{plan.Name}'...");
                var output = await _application.BuildAsync(CancellationToken.None);
                Console.WriteLine(output);

                var packResult = _application.Pack();
                WritePackResult(profile.Name, packResult);
            });

            return;
        }

        var activeSession = PluginDevSessionHolder.RequireCurrent();
        var activePlan = activeSession.BuildService.GetBuildPlan(activeSession);
        if (activePlan is null)
        {
            Console.WriteLine("No normalized build plan is available for the active profile.");
            return;
        }

        Console.WriteLine($"Running build plan '{activePlan.Name}'...");
        var buildOutput = await _application.BuildAsync(CancellationToken.None);
        Console.WriteLine(buildOutput);

        var result = _application.Pack();
        WritePackResult(null, result);
    }

    [Command("reload")]
    public async Task ReloadAsync()
    {
        var message = await _application.ReloadAsync(CancellationToken.None);
        Console.WriteLine(message);
    }

    [Command("watch")]
    public async Task WatchAsync([Argument] string action = "start")
    {
        PluginDevWatchSnapshot snapshot;

        switch (action.Trim().ToLowerInvariant())
        {
            case "start":
                snapshot = _application.StartWatch();
                Console.WriteLine($"Watch Enabled: {snapshot.IsEnabled}");
                Console.WriteLine($"Watch Status: {snapshot.Status}");
                Console.WriteLine($"Supports Reload: {snapshot.SupportsReload}");
                Console.WriteLine($"Behavior: {snapshot.Behavior}");

                if (snapshot.WatchGlobs.Count > 0)
                {
                    Console.WriteLine($"Watch Globs: {string.Join(", ", snapshot.WatchGlobs)}");
                }

                Console.WriteLine("Watching for matching file changes. Press Ctrl+C to stop.");

                using (var cts = new CancellationTokenSource())
                {
                    var seenLogCount = _application.GetLogs().Count;
                    Console.CancelKeyPress += (_, args) =>
                    {
                        args.Cancel = true;
                        cts.Cancel();
                    };

                    try
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);
                            var entries = _application.GetLogs();
                            for (var index = seenLogCount; index < entries.Count; index++)
                            {
                                var entry = entries[index];
                                PluginDevConsoleTheme.WriteLogEntry(entry);
                            }

                            seenLogCount = entries.Count;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                snapshot = _application.StopWatch();
                break;
            case "stop":
                snapshot = _application.StopWatch();
                break;
            case "status":
                snapshot = _application.GetWatchSnapshot();
                break;
            default:
                throw new InvalidOperationException($"Unknown watch action '{action}'. Use start, stop, or status.");
        }

        Console.WriteLine($"Watch Enabled: {snapshot.IsEnabled}");
        Console.WriteLine($"Watch Status: {snapshot.Status}");
        Console.WriteLine($"Supports Reload: {snapshot.SupportsReload}");
        Console.WriteLine($"Behavior: {snapshot.Behavior}");

        if (snapshot.WatchGlobs.Count > 0)
        {
            Console.WriteLine($"Watch Globs: {string.Join(", ", snapshot.WatchGlobs)}");
        }

        if (snapshot.LastChangedUtc is not null)
        {
            Console.WriteLine($"Last Change: {snapshot.LastChangedUtc:O} ({snapshot.LastChangedPath})");
        }

        if (snapshot.LastReloadUtc is not null)
        {
            Console.WriteLine($"Last Reload: {snapshot.LastReloadUtc:O} ({snapshot.LastReloadMessage})");
        }
    }

    [Command("scenario")]
    public async Task ScenarioAsync([Argument] string name, [Argument] string? query = null)
    {
        var result = await _application.RunScenarioAsync(name, query, CancellationToken.None);
        foreach (var message in result.Messages)
        {
            Console.WriteLine(message);
        }

        if (!result.Succeeded)
        {
            Console.WriteLine($"Scenario '{result.Name}' failed.");
        }
    }

    /// <summary>
    /// Shows pre-launch discovery and diagnostic information for the current plugin development session.
    /// </summary>
    [Command("doctor")]
    public void Doctor()
    {
        var session = _application.GetSessionSnapshot();
        Console.WriteLine("Plugin development doctor");
        Console.WriteLine($"  Root: {session.RootDirectory}");
        Console.WriteLine($"  Manifest: {session.ManifestPath ?? "<not found>"}");
        Console.WriteLine($"  Project: {session.ProjectFilePath ?? "<not found>"}");
        Console.WriteLine($"  Plugin ID: {session.PluginId}");

        if (!string.IsNullOrWhiteSpace(session.PluginName))
        {
            Console.WriteLine($"  Plugin Name: {session.PluginName}");
        }

        if (session.MediaTypes.Count > 0)
        {
            Console.WriteLine($"  Media Types: {string.Join(", ", session.MediaTypes)}");
        }

        if (session.SupportedTargets.Count > 0)
        {
            Console.WriteLine($"  Supported Targets: {string.Join(", ", session.SupportedTargets)}");
        }

        if (session.ArtifactCandidates.Count > 0)
        {
            Console.WriteLine("  Artifact Candidates:");
            foreach (var artifact in session.ArtifactCandidates)
            {
                var status = artifact.Exists ? "present" : "missing";
                Console.WriteLine($"    - {artifact.Target} [{artifact.Kind}] {status}: {artifact.Path}");
            }
        }

        if (session.Diagnostics.Count == 0)
        {
            Console.WriteLine("  No diagnostics.");
            return;
        }

        Console.WriteLine("  Diagnostics:");
        foreach (var diagnostic in session.Diagnostics)
        {
            PluginDevConsoleTheme.WriteDiagnostic("    ", diagnostic);
        }
    }

    [Command("serve")]
    public async Task ServeAsync([Argument] int port = 5075)
    {
        if (PluginDevCliRuntimeContext.IsInteractive)
        {
            Console.WriteLine(PluginDevLocalServer.StartInBackground(_application, port));
            return;
        }

        Console.WriteLine($"Serving plugin dev UI at http://127.0.0.1:{port}");
        await PluginDevLocalServer.RunAsync(_application, port, CancellationToken.None);
    }

    /// <summary>
    /// Shows a list of commands and how to use them.
    /// </summary>
    /// <returns></returns>
    [Command("help")]
    public Task Help()
    {
        Console.WriteLine("Available commands:");
        Console.WriteLine("  build [all]                                    Run the normalized build plan for the active profile or every profile");
        Console.WriteLine("  build-pack [all]                               Build then package the active profile or every profile");
        Console.WriteLine("  doctor                                         Show discovery and pre-launch diagnostics");
        Console.WriteLine("  pack [all]                                     Package the active profile or every profile");
        Console.WriteLine("  reload                                         Refresh runtime state for the active profile");
        Console.WriteLine("  scenario <name> [query]                        Run a built-in dev scenario (for example paged-smoke)");
        Console.WriteLine("  session                                        Show resolved session details");
        Console.WriteLine("  watch [start|stop|status]                      Manage file watching and debounced reload");
        Console.WriteLine("  search -q <query>                              Search for media");
        Console.WriteLine("  chapters -i <mediaId>                          List chapters of a media");
        Console.WriteLine("  page-asset -mi <mediaId> -ci <chapterId>       Get a chapter page asset");
        Console.WriteLine("  page -mi <mediaId> -ci <chapterId> -i <index>  Get a chapter page");
        Console.WriteLine("  serve [port]                                   Start the local session API and browser UI");
        Console.WriteLine("  help                                           Show this help message");
        return Task.CompletedTask;
    }

    private async Task ExecuteAcrossProfilesAsync(Func<PluginDevProfile, Task> action)
    {
        var snapshot = _application.GetSessionSnapshot();
        var originalProfileName = snapshot.Profile.Name;

        try
        {
            foreach (var profile in snapshot.AvailableProfiles)
            {
                _application.SelectProfile(profile.Name);
                await action(profile);
            }
        }
        finally
        {
            _application.SelectProfile(originalProfileName);
        }
    }

    private static bool IsAllScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return false;
        }

        if (string.Equals(scope.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        throw new InvalidOperationException($"Unknown scope '{scope}'. Use 'all' or omit the argument.");
    }

    private static void WritePackResult(string? profileName, PluginDevPackResult result)
    {
        var prefix = string.IsNullOrWhiteSpace(profileName) ? string.Empty : $"[{profileName}] ";
        Console.WriteLine($"{prefix}Package: {result.PackagePath}");
        Console.WriteLine($"{prefix}Pack Directory: {result.PackageDirectory}");
        Console.WriteLine($"{prefix}Manifest: {result.ManifestPath}");
        Console.WriteLine($"{prefix}Artifact: {result.ArtifactPath}");
    }
}
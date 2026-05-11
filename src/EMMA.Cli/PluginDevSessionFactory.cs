using EMMA.Api;
using EMMA.Api.Embedded;
using EMMA.Infrastructure.Http;
using EMMA.Infrastructure.InMemory;
using EMMA.Infrastructure.Policy;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace EMMA.Cli;

public sealed class PluginDevSessionFactory
{
    private const string HostAuthHeaderName = "x-emma-plugin-host-auth";
    private const string HostAuthTokenEnvironmentVariable = "EMMA_PLUGIN_HOST_AUTH_TOKEN";
    private const string DefaultProfileName = "host-bridge";
    private const string DefaultPluginId = "emma.plugin.test";
    private const string DefaultHostBridgeUrl = "http://localhost:5000";
    private const string DefaultLinuxDirectUrl = "http://127.0.0.1:5017";
    private const string DefaultWindowsDirectUrl = "http://127.0.0.1:5018";

    private readonly PluginDevConfigLoader _configLoader = new();
    private readonly PluginDevDiscoveryService _discoveryService = new();
    private readonly PluginDevDoctor _doctor = new();
    private readonly PluginDevBuildService _buildService = new();
    private readonly PluginDevScenarioRunner _scenarioRunner = new();
    private readonly PluginDevDesignTimeBuildProfileSync _designTimeBuildProfileSync = new();

    public PluginDevSession Create(string workingDirectory, string? requestedProfileName = null)
    {
        var loadResult = _configLoader.Load(workingDirectory);
        var discovery = _discoveryService.Discover(workingDirectory);
        var availableProfiles = BuildAvailableProfiles(loadResult, discovery);
        var profile = ResolveProfile(loadResult, availableProfiles, requestedProfileName);
        var ui = ResolveUi(loadResult.Document.Ui);
        var configuredScenarios = ResolveConfiguredScenarios(loadResult.Document, profile.Name);
        var hostAuthToken = ResolveHostAuthToken();

        var httpClient = new HttpClient { BaseAddress = new Uri(profile.HostUrl, UriKind.Absolute) };
        if (!string.IsNullOrWhiteSpace(hostAuthToken))
        {
            httpClient.DefaultRequestHeaders.Remove(HostAuthHeaderName);
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(HostAuthHeaderName, hostAuthToken);
        }

        var pluginPort = new PluginHostPagedMediaPort(
            httpClient,
            Options.Create(new PluginHostClientOptions
            {
                BaseUrl = profile.HostUrl,
                PluginId = profile.PluginId
            }));

        var runtime = EmbeddedRuntimeFactory.Create(
            pluginPort,
            pluginPort,
            new HostPolicyEvaluator(),
            metadataCache: new InMemoryCachePort());

        var api = new EmbeddedPagedMediaApi(runtime);
        var runtimeAdapter = CreateRuntimeAdapter(profile, runtime, api, discovery, availableProfiles);

        var session = new PluginDevSession(
            workingDirectory,
            discovery,
            availableProfiles,
            profile,
            ui,
            configuredScenarios,
            runtimeAdapter,
            _buildService,
            _scenarioRunner,
            runtime,
            api);

        session.TransitionTo(PluginDevSessionState.Prepared);
        session.AddDiagnostic(
            "session.profile.resolved",
            $"Resolved profile '{profile.Name}' for plugin '{profile.PluginId}' using host '{profile.HostUrl}'.");

        TrySyncDesignTimeBuildProfile(session, profile, discovery.RootDirectory);

        if (!string.IsNullOrWhiteSpace(profile.ConfigPath))
        {
            session.AddDiagnostic(
                "session.config.loaded",
                $"Loaded plugin development config from '{profile.ConfigPath}'.");
        }
        else
        {
            session.AddDiagnostic(
                "session.config.inferred",
                "No plugin.dev.json file was found. Using inferred host-bridge defaults.");
        }

        if (profile.ExecutionMode == PluginExecutionMode.HostBridge)
        {
            session.AddDiagnostic(
                string.IsNullOrWhiteSpace(hostAuthToken)
                    ? "session.host_bridge.auth_token_missing"
                    : "session.host_bridge.auth_token_configured",
                string.IsNullOrWhiteSpace(hostAuthToken)
                    ? $"Host-bridge execution for profile '{profile.Name}' has no {HostAuthTokenEnvironmentVariable} configured. Requests to auth-protected plugin hosts may fail with unauthenticated errors."
                    : $"Host-bridge execution for profile '{profile.Name}' will forward {HostAuthTokenEnvironmentVariable} to the plugin host.",
                string.IsNullOrWhiteSpace(hostAuthToken) ? PluginDevDiagnosticSeverity.Warning : PluginDevDiagnosticSeverity.Info,
                "auth");
        }

        foreach (var diagnostic in _doctor.Run(discovery, profile, availableProfiles))
        {
            session.AddDiagnostic(diagnostic.Code, diagnostic.Message, diagnostic.Severity, diagnostic.Type);
        }

        if (configuredScenarios.Count > 0)
        {
            session.AddDiagnostic(
                "session.scenarios.loaded",
                $"Loaded {configuredScenarios.Count} custom scenario(s) for profile '{profile.Name}'.",
                PluginDevDiagnosticSeverity.Info,
                "scenarios");
        }

        foreach (var diagnostic in _scenarioRunner.LintConfiguredScenarios(configuredScenarios))
        {
            session.AddDiagnostic(diagnostic.Code, diagnostic.Message, diagnostic.Severity, diagnostic.Type);
        }

        return session;
    }

    private void TrySyncDesignTimeBuildProfile(PluginDevSession session, PluginDevProfile profile, string rootDirectory)
    {
        try
        {
            var result = _designTimeBuildProfileSync.Sync(rootDirectory, profile);
            session.AddDiagnostic(
                "session.design_time_profile.synced",
                $"Saved design-time PluginTransport='{result.PluginTransport}' for profile '{profile.Name}' to '{result.FilePath}'. VS Code linting and IntelliSense will follow that transport after the workspace reloads its project state.",
                PluginDevDiagnosticSeverity.Info,
                "profile");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            session.AddDiagnostic(
                "session.design_time_profile.sync_failed",
                $"Failed to save the design-time build profile for '{profile.Name}': {ex.Message}",
                PluginDevDiagnosticSeverity.Warning,
                "profile");
        }
    }

    private static IPluginDevRuntimeAdapter CreateRuntimeAdapter(
        PluginDevProfile profile,
        EmbeddedRuntime runtime,
        EmbeddedPagedMediaApi api,
        PluginDevDiscoveryResult discovery,
        IReadOnlyList<PluginDevProfile> availableProfiles)
    {
        if (profile.ExecutionMode == PluginExecutionMode.Direct && profile.RuntimeTarget == PluginRuntimeTarget.Wasm)
        {
            var buildService = new PluginDevBuildService();
            var tempSession = new PluginDevSession(
                discovery.RootDirectory,
                discovery,
                availableProfiles,
                profile,
                PluginDevUiOptions.Default,
                [],
                null!,
                buildService,
                null!,
                runtime,
                api);

            var runtimeLibraryPath = PluginDevRuntimeLibraryResolver.Resolve(discovery.RootDirectory);
            return new DeferredWasmRuntimeAdapter(
                discovery.RootDirectory,
                runtimeLibraryPath,
                discovery.PermittedDomains,
                () => buildService.ResolveWasmArtifactPath(tempSession));
        }

        if (profile.ExecutionMode == PluginExecutionMode.Direct
            && profile.RuntimeTarget is PluginRuntimeTarget.Linux or PluginRuntimeTarget.Windows)
        {
            if (!IsNativeTargetRunnable(profile.RuntimeTarget))
            {
                return new UnsupportedRuntimeAdapter(
                    $"native-{profile.RuntimeTarget.ToString().ToLowerInvariant()}-unavailable",
                    $"Profile '{profile.Name}' targets {profile.RuntimeTarget}, but local direct execution is not supported on this host OS. Use 'host-bridge' or validate the packaged artifact on a matching machine.");
            }

            var buildService = new PluginDevBuildService();
            var tempSession = new PluginDevSession(
                discovery.RootDirectory,
                discovery,
                availableProfiles,
                profile,
                PluginDevUiOptions.Default,
                [],
                null!,
                buildService,
                null!,
                runtime,
                api);

            var entryPointPath = buildService.ResolveNativeEntrypointPath(tempSession);
            if (string.IsNullOrWhiteSpace(entryPointPath))
            {
                return new UnsupportedRuntimeAdapter(
                    $"native-{profile.RuntimeTarget.ToString().ToLowerInvariant()}-missing-artifact",
                    $"Profile '{profile.Name}' could not resolve a published native entrypoint. Run 'build' for the active profile first.");
            }

            return new NativeProcessRuntimeAdapter(
                entryPointPath,
                new Uri(profile.HostUrl, UriKind.Absolute),
                profile.RuntimeTarget,
                profile.Logging);
        }

        return new HostBridgeRuntimeAdapter(runtime, api);
    }

    private static bool IsNativeTargetRunnable(PluginRuntimeTarget target)
    {
        return target switch
        {
            PluginRuntimeTarget.Linux => OperatingSystem.IsLinux(),
            PluginRuntimeTarget.Windows => OperatingSystem.IsWindows(),
            _ => true
        };
    }

    private static PluginDevProfile ResolveProfile(
        PluginDevConfigLoadResult loadResult,
        IReadOnlyList<PluginDevProfile> availableProfiles,
        string? requestedProfileName = null)
    {
        var requestedProfile = string.IsNullOrWhiteSpace(requestedProfileName)
            ? Environment.GetEnvironmentVariable("EMMA_PLUGIN_PROFILE")?.Trim()
            : requestedProfileName.Trim();
        var profileName = FirstNonEmpty(
                requestedProfile,
                loadResult.Document.DefaultProfile,
                availableProfiles.FirstOrDefault()?.Name,
                DefaultProfileName)
            ?? DefaultProfileName;

        var profile = availableProfiles.FirstOrDefault(candidate => string.Equals(candidate.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            throw new InvalidOperationException(
                $"Profile '{profileName}' was requested, but it was not found in plugin.dev.json or the inferred profile set.");
        }

        return profile;
    }

    private static IReadOnlyList<PluginDevProfile> BuildAvailableProfiles(
        PluginDevConfigLoadResult loadResult,
        PluginDevDiscoveryResult discovery)
    {
        var profiles = new Dictionary<string, PluginDevProfile>(StringComparer.OrdinalIgnoreCase);
        var configuredProfiles = loadResult.Document.Profiles;

        if (configuredProfiles is { Count: > 0 })
        {
            foreach (var (profileName, profileDocument) in configuredProfiles)
            {
                profiles[profileName] = BuildProfile(profileName, profileDocument, loadResult.ConfigPath, discovery, isInferred: false);
            }
        }

        foreach (var inferredProfile in BuildInferredProfiles(loadResult.ConfigPath, discovery))
        {
            profiles.TryAdd(inferredProfile.Name, inferredProfile);
        }

        return profiles.Values
            .OrderBy(static profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<PluginDevProfile> BuildInferredProfiles(string? configPath, PluginDevDiscoveryResult discovery)
    {
        const PluginExecutionMode hostBridgeMode = PluginExecutionMode.HostBridge;

        yield return new PluginDevProfile(
            DefaultProfileName,
            ResolvePluginId(null, discovery),
            ResolveHostUrl(null, PluginRuntimeTarget.Auto, hostBridgeMode),
            PluginRuntimeTarget.Auto,
            hostBridgeMode,
            PluginDevLoggingOptions.Default,
            PluginDevSyncOptions.Disabled,
            null,
            Array.Empty<string>(),
            configPath,
            null,
            true);

        foreach (var target in discovery.SupportedTargets)
        {
            var artifactPath = discovery.ArtifactCandidates.FirstOrDefault(candidate => candidate.Target == target && candidate.Exists)?.Path
                ?? discovery.ArtifactCandidates.FirstOrDefault(candidate => candidate.Target == target)?.Path;
            var executionMode = target is PluginRuntimeTarget.Wasm or PluginRuntimeTarget.Linux or PluginRuntimeTarget.Windows
                ? PluginExecutionMode.Direct
                : PluginExecutionMode.HostBridge;

            yield return new PluginDevProfile(
                ToProfileName(target),
                ResolvePluginId(null, discovery),
                ResolveHostUrl(null, target, executionMode),
                target,
                executionMode,
                PluginDevLoggingOptions.Default,
                PluginDevSyncOptions.Disabled,
                null,
                Array.Empty<string>(),
                configPath,
                artifactPath,
                true);
        }
    }

    private static PluginDevProfile BuildProfile(
        string profileName,
        PluginDevProfileDocument profileDocument,
        string? configPath,
        PluginDevDiscoveryResult discovery,
        bool isInferred)
    {
        var runtimeTarget = ParseRuntimeTarget(
            FirstNonEmpty(
                Environment.GetEnvironmentVariable("EMMA_PLUGIN_TARGET"),
                profileDocument.RuntimeTarget,
                PluginRuntimeTarget.Auto.ToString()));
        var executionMode = ParseExecutionMode(
            FirstNonEmpty(
                Environment.GetEnvironmentVariable("EMMA_PLUGIN_EXECUTION_MODE"),
                profileDocument.ExecutionMode,
                PluginExecutionMode.HostBridge.ToString()));

        return new PluginDevProfile(
            profileName,
            ResolvePluginId(profileDocument, discovery),
            ResolveHostUrl(profileDocument, runtimeTarget, executionMode),
            runtimeTarget,
            executionMode,
            ResolveLogging(profileDocument.Logging),
                    ResolveSync(profileDocument.Sync, configPath, discovery.RootDirectory),
            ResolveWasiSdkPath(profileDocument),
            (IReadOnlyList<string>)(profileDocument.WatchGlobs?.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? []),
            configPath,
            discovery.ArtifactCandidates.FirstOrDefault(candidate => candidate.Target == runtimeTarget && candidate.Exists)?.Path
                ?? discovery.ArtifactCandidates.FirstOrDefault(candidate => candidate.Target == runtimeTarget)?.Path,
            isInferred);
    }

    private static string ResolvePluginId(PluginDevProfileDocument? profileDocument, PluginDevDiscoveryResult discovery)
    {
        return FirstNonEmpty(
                Environment.GetEnvironmentVariable("EMMA_PLUGIN_ID"),
                profileDocument?.PluginId,
                discovery.PluginId,
                DefaultPluginId)
            ?? DefaultPluginId;
    }

    private static string ResolveHostUrl(PluginDevProfileDocument? profileDocument, PluginRuntimeTarget runtimeTarget, PluginExecutionMode executionMode)
    {
        return NormalizeHostUrl(
            FirstNonEmpty(
                Environment.GetEnvironmentVariable("EMMA_PLUGIN_HOST_URL"),
                profileDocument?.HostUrl,
                ResolveDefaultHostUrl(runtimeTarget, executionMode))
            ?? ResolveDefaultHostUrl(runtimeTarget, executionMode));
    }

    private static string ResolveDefaultHostUrl(PluginRuntimeTarget runtimeTarget, PluginExecutionMode executionMode)
    {
        if (executionMode == PluginExecutionMode.Direct)
        {
            return runtimeTarget switch
            {
                PluginRuntimeTarget.Linux => DefaultLinuxDirectUrl,
                PluginRuntimeTarget.Windows => DefaultWindowsDirectUrl,
                _ => DefaultHostBridgeUrl
            };
        }

        return DefaultHostBridgeUrl;
    }

    private static string ToProfileName(PluginRuntimeTarget target)
    {
        return target switch
        {
            PluginRuntimeTarget.Wasm => "wasm-dev",
            PluginRuntimeTarget.Linux => "linux-dev",
            PluginRuntimeTarget.Windows => "windows-dev",
            _ => DefaultProfileName
        };
    }

    private static string NormalizeHostUrl(string hostUrl)
    {
        return hostUrl.Trim().TrimEnd('/');
    }

    private static PluginDevLoggingOptions ResolveLogging(PluginDevLoggingDocument? logging)
    {
        var defaults = PluginDevLoggingOptions.Default;
        return new PluginDevLoggingOptions(
            logging?.Plugin ?? defaults.Plugin,
            logging?.AspNetHost ?? defaults.AspNetHost,
            logging?.HttpClient ?? defaults.HttpClient);
    }

    private static PluginDevSyncOptions ResolveSync(PluginDevSyncDocument? sync, string? configPath, string rootDirectory)
    {
        if (sync is null)
        {
            return PluginDevSyncOptions.Disabled;
        }

        var destinationPath = ResolveOptionalPath(sync.DestinationPath, configPath, rootDirectory);
        var enabled = sync.Enabled ?? !string.IsNullOrWhiteSpace(destinationPath);
        if (!enabled)
        {
            return PluginDevSyncOptions.Disabled;
        }

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new InvalidOperationException("Profile sync is enabled, but no sync.destinationPath was configured.");
        }

        return new PluginDevSyncOptions(
            Enabled: true,
            DestinationPath: destinationPath,
            OnBuild: sync.OnBuild ?? true,
            CleanDestination: sync.CleanDestination ?? false);
    }

    private static string? ResolveWasiSdkPath(PluginDevProfileDocument profileDocument)
    {
        var value = FirstNonEmpty(
            Environment.GetEnvironmentVariable("WASI_SDK_PATH"),
            profileDocument.WasiSdkPath);
        return string.IsNullOrWhiteSpace(value) ? null : ResolveOptionalPath(value, configPath: null, rootDirectory: Directory.GetCurrentDirectory());
    }

    private static string? ResolveOptionalPath(string? value, string? configPath, string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var expanded = ExpandPath(value.Trim());
        if (Path.IsPathRooted(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        var baseDirectory = !string.IsNullOrWhiteSpace(configPath)
            ? Path.GetDirectoryName(configPath)
            : rootDirectory;
        baseDirectory ??= rootDirectory;
        return Path.GetFullPath(Path.Combine(baseDirectory, expanded));
    }

    private static PluginDevUiOptions ResolveUi(PluginDevUiDocument? ui)
    {
        var diagnosticsLevel = ui?.DiagnosticsLevel?.Trim().ToLowerInvariant();
        return diagnosticsLevel switch
        {
            null or "" => PluginDevUiOptions.Default with
            {
                StartWatchByDefault = ui?.StartWatchByDefault ?? PluginDevUiOptions.Default.StartWatchByDefault,
                StartServeByDefault = ui?.StartServeByDefault ?? PluginDevUiOptions.Default.StartServeByDefault
            },
            PluginDevDiagnosticSeverity.Info => new PluginDevUiOptions(
                PluginDevDiagnosticSeverity.Info,
                ui?.StartWatchByDefault ?? PluginDevUiOptions.Default.StartWatchByDefault,
                ui?.StartServeByDefault ?? PluginDevUiOptions.Default.StartServeByDefault),
            PluginDevDiagnosticSeverity.Warning => new PluginDevUiOptions(
                PluginDevDiagnosticSeverity.Warning,
                ui?.StartWatchByDefault ?? PluginDevUiOptions.Default.StartWatchByDefault,
                ui?.StartServeByDefault ?? PluginDevUiOptions.Default.StartServeByDefault),
            PluginDevDiagnosticSeverity.Error => new PluginDevUiOptions(
                PluginDevDiagnosticSeverity.Error,
                ui?.StartWatchByDefault ?? PluginDevUiOptions.Default.StartWatchByDefault,
                ui?.StartServeByDefault ?? PluginDevUiOptions.Default.StartServeByDefault),
            _ => throw new InvalidOperationException($"UI diagnosticsLevel '{ui?.DiagnosticsLevel}' is invalid. Expected one of: info, warning, error.")
        };
    }

    private static string ExpandPath(string value)
    {
        if (value.StartsWith("~/", StringComparison.Ordinal) || value == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                return value.Length == 1
                    ? home
                    : Path.Combine(home, value[2..]);
            }
        }

        return value;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static PluginRuntimeTarget ParseRuntimeTarget(string? value)
    {
        return Enum.TryParse<PluginRuntimeTarget>(value, ignoreCase: true, out var target)
            ? target
            : PluginRuntimeTarget.Auto;
    }

    private static PluginExecutionMode ParseExecutionMode(string? value)
    {
        return Enum.TryParse<PluginExecutionMode>(value, ignoreCase: true, out var mode)
            ? mode
            : PluginExecutionMode.HostBridge;
    }

    private static string? ResolveHostAuthToken()
    {
        var value = Environment.GetEnvironmentVariable(HostAuthTokenEnvironmentVariable);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static IReadOnlyList<PluginDevConfiguredScenario> ResolveConfiguredScenarios(PluginDevConfigDocument document, string profileName)
    {
        if (document.Scenarios is null || document.Scenarios.Count == 0)
        {
            return [];
        }

        var scenarios = new List<PluginDevConfiguredScenario>();
        foreach (var (name, scenarioDocument) in document.Scenarios)
        {
            if (string.IsNullOrWhiteSpace(name) || scenarioDocument is null)
            {
                continue;
            }

            var appliesToProfiles = scenarioDocument.Profiles?
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .ToArray() ?? [];
            if (appliesToProfiles.Length > 0
                && !appliesToProfiles.Any(value => string.Equals(value, profileName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var steps = scenarioDocument.Steps?
                .Where(static step => !string.IsNullOrWhiteSpace(step.Op))
                .Select(static step => new PluginDevScenarioStep(
                    step.Op!.Trim(),
                    string.IsNullOrWhiteSpace(step.Save) ? null : step.Save.Trim(),
                    step.Parameters ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase),
                    ResolveSuppressedScenarioWarnings(step.NoWarn)))
                .ToArray() ?? [];
            if (steps.Length == 0)
            {
                continue;
            }

            scenarios.Add(new PluginDevConfiguredScenario(
                Name: name.Trim(),
                DisplayName: string.IsNullOrWhiteSpace(scenarioDocument.DisplayName) ? name.Trim() : scenarioDocument.DisplayName.Trim(),
                Description: string.IsNullOrWhiteSpace(scenarioDocument.Description) ? $"Custom scenario '{name.Trim()}'." : scenarioDocument.Description.Trim(),
                DefaultQuery: string.IsNullOrWhiteSpace(scenarioDocument.DefaultQuery) ? null : scenarioDocument.DefaultQuery.Trim(),
                SupportsQuery: scenarioDocument.SupportsQuery ?? true,
                QueryLabel: string.IsNullOrWhiteSpace(scenarioDocument.QueryLabel) ? "Query" : scenarioDocument.QueryLabel.Trim(),
                Steps: steps));
        }

        return scenarios;
    }

    private static IReadOnlySet<string> ResolveSuppressedScenarioWarnings(JsonElement? noWarn)
    {
        if (noWarn is null || noWarn.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (noWarn.Value.ValueKind)
        {
            case JsonValueKind.String:
            {
                var value = noWarn.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value.Trim());
                }

                break;
            }
            case JsonValueKind.Array:
            {
                foreach (var item in noWarn.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        values.Add(value.Trim());
                    }
                }

                break;
            }
        }

        return values;
    }
}
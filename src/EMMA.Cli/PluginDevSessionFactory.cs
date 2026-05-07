using EMMA.Api;
using EMMA.Api.Embedded;
using EMMA.Infrastructure.Http;
using EMMA.Infrastructure.InMemory;
using EMMA.Infrastructure.Policy;
using Microsoft.Extensions.Options;

namespace EMMA.Cli;

public sealed class PluginDevSessionFactory
{
    private const string DefaultProfileName = "host-bridge";
    private const string DefaultPluginId = "emma.plugin.test";
    private const string DefaultHostUrl = "http://localhost:5000";

    private readonly PluginDevConfigLoader _configLoader = new();
    private readonly PluginDevDiscoveryService _discoveryService = new();
    private readonly PluginDevDoctor _doctor = new();
    private readonly PluginDevBuildService _buildService = new();
    private readonly PluginDevScenarioRunner _scenarioRunner = new();

    public PluginDevSession Create(string workingDirectory)
    {
        var loadResult = _configLoader.Load(workingDirectory);
        var discovery = _discoveryService.Discover(workingDirectory);
        var availableProfiles = BuildAvailableProfiles(loadResult, discovery);
        var profile = ResolveProfile(loadResult, availableProfiles);

        var httpClient = new HttpClient { BaseAddress = new Uri(profile.HostUrl, UriKind.Absolute) };
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
            runtimeAdapter,
            _buildService,
            _scenarioRunner,
            runtime,
            api);

        session.TransitionTo(PluginDevSessionState.Prepared);
        session.AddDiagnostic(
            "session.profile.resolved",
            $"Resolved profile '{profile.Name}' for plugin '{profile.PluginId}' using host '{profile.HostUrl}'.");

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

        foreach (var diagnostic in _doctor.Run(discovery, profile, availableProfiles))
        {
            session.AddDiagnostic(diagnostic.Code, diagnostic.Message, diagnostic.IsError);
        }

        return session;
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
            return new WasmCliRuntimeAdapter(discovery.RootDirectory, discovery.ProjectFilePath);
        }

        return new HostBridgeRuntimeAdapter(runtime, api);
    }

    private static PluginDevProfile ResolveProfile(
        PluginDevConfigLoadResult loadResult,
        IReadOnlyList<PluginDevProfile> availableProfiles)
    {
        var requestedProfile = Environment.GetEnvironmentVariable("EMMA_PLUGIN_PROFILE")?.Trim();
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
        yield return new PluginDevProfile(
            DefaultProfileName,
            ResolvePluginId(null, discovery),
            ResolveHostUrl(null),
            PluginRuntimeTarget.Auto,
            PluginExecutionMode.HostBridge,
            Array.Empty<string>(),
            configPath,
            null,
            true);

        foreach (var target in discovery.SupportedTargets)
        {
            var artifactPath = discovery.ArtifactCandidates.FirstOrDefault(candidate => candidate.Target == target && candidate.Exists)?.Path
                ?? discovery.ArtifactCandidates.FirstOrDefault(candidate => candidate.Target == target)?.Path;

            yield return new PluginDevProfile(
                ToProfileName(target),
                ResolvePluginId(null, discovery),
                ResolveHostUrl(null),
                target,
                target == PluginRuntimeTarget.Wasm ? PluginExecutionMode.Direct : PluginExecutionMode.HostBridge,
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

        return new PluginDevProfile(
            profileName,
            ResolvePluginId(profileDocument, discovery),
            ResolveHostUrl(profileDocument),
            runtimeTarget,
            ParseExecutionMode(
                FirstNonEmpty(
                    Environment.GetEnvironmentVariable("EMMA_PLUGIN_EXECUTION_MODE"),
                    profileDocument.ExecutionMode,
                    PluginExecutionMode.HostBridge.ToString())),
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

    private static string ResolveHostUrl(PluginDevProfileDocument? profileDocument)
    {
        return NormalizeHostUrl(
            FirstNonEmpty(
                Environment.GetEnvironmentVariable("EMMA_PLUGIN_HOST_URL"),
                profileDocument?.HostUrl,
                DefaultHostUrl)
            ?? DefaultHostUrl);
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
}
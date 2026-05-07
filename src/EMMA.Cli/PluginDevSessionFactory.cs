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

    public PluginDevSession Create(string workingDirectory)
    {
        var loadResult = _configLoader.Load(workingDirectory);
        var profile = ResolveProfile(loadResult);

        if (profile.ExecutionMode != PluginExecutionMode.HostBridge)
        {
            throw new NotSupportedException(
                $"Execution mode '{profile.ExecutionMode}' is not implemented yet. Phase 1 only supports host-bridge sessions.");
        }

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

        var session = new PluginDevSession(
            workingDirectory,
            profile,
            runtime,
            new EmbeddedPagedMediaApi(runtime));

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

        return session;
    }

    private static PluginDevProfile ResolveProfile(PluginDevConfigLoadResult loadResult)
    {
        var requestedProfile = Environment.GetEnvironmentVariable("EMMA_PLUGIN_PROFILE")?.Trim();
        var profiles = loadResult.Document.Profiles;

        var profileName = FirstNonEmpty(
                requestedProfile,
                loadResult.Document.DefaultProfile,
                profiles is { Count: > 0 } ? profiles.Keys.FirstOrDefault() : null,
                DefaultProfileName)
            ?? DefaultProfileName;

        PluginDevProfileDocument? profileDocument = null;
        if (profiles is { Count: > 0 })
        {
            if (!profiles.TryGetValue(profileName, out profileDocument))
            {
                throw new InvalidOperationException(
                    $"Profile '{profileName}' was requested, but it was not found in plugin.dev.json.");
            }
        }

        var pluginId = FirstNonEmpty(
                Environment.GetEnvironmentVariable("EMMA_PLUGIN_ID"),
                profileDocument?.PluginId,
                DefaultPluginId)
            ?? DefaultPluginId;

        var hostUrl = NormalizeHostUrl(
            FirstNonEmpty(
                Environment.GetEnvironmentVariable("EMMA_PLUGIN_HOST_URL"),
                profileDocument?.HostUrl,
                DefaultHostUrl)
            ?? DefaultHostUrl);

        var runtimeTarget = ParseRuntimeTarget(
            FirstNonEmpty(
                Environment.GetEnvironmentVariable("EMMA_PLUGIN_TARGET"),
                profileDocument?.RuntimeTarget,
                PluginRuntimeTarget.Auto.ToString()));

        var executionMode = ParseExecutionMode(
            FirstNonEmpty(
                Environment.GetEnvironmentVariable("EMMA_PLUGIN_EXECUTION_MODE"),
                profileDocument?.ExecutionMode,
                PluginExecutionMode.HostBridge.ToString()));

        var watchGlobs = (IReadOnlyList<string>)(profileDocument?.WatchGlobs?.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray()
            ?? []);

        return new PluginDevProfile(
            profileName,
            pluginId,
            hostUrl,
            runtimeTarget,
            executionMode,
            watchGlobs,
            loadResult.ConfigPath);
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
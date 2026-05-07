using EMMA.Api;
using EMMA.Api.Embedded;

namespace EMMA.Cli;

public enum PluginRuntimeTarget
{
    Auto,
    Wasm,
    Linux,
    Windows
}

public enum PluginExecutionMode
{
    HostBridge,
    Direct
}

public enum PluginDevSessionState
{
    Discovered,
    Prepared,
    Starting,
    Running,
    Reloading,
    Stopped,
    Failed
}

public sealed record PluginDevDiagnostic(string Code, string Message, bool IsError = false);

public sealed record PluginDevLoggingOptions(
    bool Plugin,
    bool AspNetHost,
    bool HttpClient)
{
    public static PluginDevLoggingOptions Default { get; } = new(
        Plugin: true,
        AspNetHost: false,
        HttpClient: false);
}

public sealed record PluginDevSyncOptions(
    bool Enabled,
    string? DestinationPath,
    bool OnBuild,
    bool CleanDestination)
{
    public static PluginDevSyncOptions Disabled { get; } = new(
        Enabled: false,
        DestinationPath: null,
        OnBuild: false,
        CleanDestination: false);
}

public sealed record PluginDevProfile(
    string Name,
    string PluginId,
    string HostUrl,
    PluginRuntimeTarget RuntimeTarget,
    PluginExecutionMode ExecutionMode,
    PluginDevLoggingOptions Logging,
    PluginDevSyncOptions Sync,
    string? WasiSdkPath,
    IReadOnlyList<string> WatchGlobs,
    string? ConfigPath,
    string? ArtifactPath,
    bool IsInferred);

public sealed class PluginDevSession
{
    private readonly List<PluginDevDiagnostic> _diagnostics = [];

    public PluginDevSession(
        string workingDirectory,
        PluginDevDiscoveryResult discovery,
        IReadOnlyList<PluginDevProfile> availableProfiles,
        PluginDevProfile profile,
        IPluginDevRuntimeAdapter runtimeAdapter,
        PluginDevBuildService buildService,
        PluginDevScenarioRunner scenarioRunner,
        EmbeddedRuntime runtime,
        EmbeddedPagedMediaApi api)
    {
        Id = Guid.NewGuid().ToString("n");
        WorkingDirectory = workingDirectory;
        Discovery = discovery;
        AvailableProfiles = availableProfiles;
        Profile = profile;
        RuntimeAdapter = runtimeAdapter;
        BuildService = buildService;
        ScenarioRunner = scenarioRunner;
        Runtime = runtime;
        Api = api;
        State = PluginDevSessionState.Discovered;
        CreatedUtc = DateTimeOffset.UtcNow;
    }

    public string Id { get; }

    public string WorkingDirectory { get; }

    public PluginDevDiscoveryResult Discovery { get; }

    public IReadOnlyList<PluginDevProfile> AvailableProfiles { get; }

    public PluginDevProfile Profile { get; }

    public IPluginDevRuntimeAdapter RuntimeAdapter { get; }

    public PluginDevBuildService BuildService { get; }

    public PluginDevScenarioRunner ScenarioRunner { get; }

    public EmbeddedRuntime Runtime { get; }

    public EmbeddedPagedMediaApi Api { get; }

    public PluginDevSessionState State { get; private set; }

    public DateTimeOffset CreatedUtc { get; }

    public IReadOnlyList<PluginDevDiagnostic> Diagnostics => _diagnostics;

    public bool HasErrors => _diagnostics.Any(static diagnostic => diagnostic.IsError);

    public void TransitionTo(PluginDevSessionState newState)
    {
        State = newState;
    }

    public void AddDiagnostic(string code, string message, bool isError = false)
    {
        _diagnostics.Add(new PluginDevDiagnostic(code, message, isError));

        if (isError)
        {
            State = PluginDevSessionState.Failed;
        }
    }
}

public static class PluginDevSessionHolder
{
    public static PluginDevSession Current { get; private set; } = null!;

    public static void SetCurrent(PluginDevSession session)
    {
        Current = session;
    }

    public static PluginDevSession RequireCurrent()
    {
        return Current ?? throw new InvalidOperationException("No active plugin development session has been configured.");
    }
}
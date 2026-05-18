using EMMA.Api;
using EMMA.Api.Embedded;
using EMMA.Domain;

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

public static class PluginDevDiagnosticSeverity
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";
}

public sealed record PluginDevDiagnostic(string Code, string Message, string Severity = PluginDevDiagnosticSeverity.Info, string Type = "general")
{
    public bool IsError => string.Equals(Severity, PluginDevDiagnosticSeverity.Error, StringComparison.OrdinalIgnoreCase);

    public static string InferType(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "general";
        }

        if (string.Equals(code, ErrorCodes.Unauthenticated, StringComparison.OrdinalIgnoreCase))
        {
            return "auth";
        }

        if (string.Equals(code, ErrorCodes.Timeout, StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, ErrorCodes.Cancelled, StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, ErrorCodes.UpstreamFailure, StringComparison.OrdinalIgnoreCase))
        {
            return "runtime";
        }

        if (string.Equals(code, ErrorCodes.InvalidRequest, StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, ErrorCodes.NotFound, StringComparison.OrdinalIgnoreCase))
        {
            return "request";
        }

        var segments = code.Split(['.', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length >= 2)
        {
            return segments[1];
        }

        return segments.Length == 1 ? segments[0] : "general";
    }
}

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

public sealed record PluginDevUiOptions(
    string DiagnosticsLevel,
    bool StartWatchByDefault,
    bool StartServeByDefault)
{
    public static PluginDevUiOptions Default { get; } = new(
        PluginDevDiagnosticSeverity.Info,
        StartWatchByDefault: false,
        StartServeByDefault: false);
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
        PluginDevUiOptions ui,
        IReadOnlyList<PluginDevConfiguredScenario> configuredScenarios,
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
        Ui = ui;
        ConfiguredScenarios = configuredScenarios;
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

    public PluginDevDiscoveryResult Discovery { get; private set; }

    public IReadOnlyList<PluginDevProfile> AvailableProfiles { get; private set; }

    public PluginDevProfile Profile { get; private set; }

    public PluginDevUiOptions Ui { get; private set; }

    public IReadOnlyList<PluginDevConfiguredScenario> ConfiguredScenarios { get; private set; }

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
        AddDiagnostic(
            code,
            message,
            isError ? PluginDevDiagnosticSeverity.Error : PluginDevDiagnosticSeverity.Info,
            PluginDevDiagnostic.InferType(code));

        if (isError)
        {
            State = PluginDevSessionState.Failed;
        }
    }

    public void AddDiagnostic(string code, string message, string severity, string? type = null)
    {
        _diagnostics.Add(new PluginDevDiagnostic(
            code,
            message,
            severity,
            string.IsNullOrWhiteSpace(type) ? PluginDevDiagnostic.InferType(code) : type));

        if (string.Equals(severity, PluginDevDiagnosticSeverity.Error, StringComparison.OrdinalIgnoreCase))
        {
            State = PluginDevSessionState.Failed;
        }
    }

    public void RefreshConfigurationFrom(PluginDevSession source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Discovery = source.Discovery;
        AvailableProfiles = source.AvailableProfiles;
        Profile = source.Profile;
        Ui = source.Ui;
        ConfiguredScenarios = source.ConfiguredScenarios;

        _diagnostics.Clear();
        _diagnostics.AddRange(source.Diagnostics);
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
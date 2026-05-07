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

public sealed record PluginDevProfile(
    string Name,
    string PluginId,
    string HostUrl,
    PluginRuntimeTarget RuntimeTarget,
    PluginExecutionMode ExecutionMode,
    IReadOnlyList<string> WatchGlobs,
    string? ConfigPath);

public sealed class PluginDevSession
{
    private readonly List<PluginDevDiagnostic> _diagnostics = [];

    public PluginDevSession(
        string workingDirectory,
        PluginDevProfile profile,
        EmbeddedRuntime runtime,
        EmbeddedPagedMediaApi api)
    {
        Id = Guid.NewGuid().ToString("n");
        WorkingDirectory = workingDirectory;
        Profile = profile;
        Runtime = runtime;
        Api = api;
        State = PluginDevSessionState.Discovered;
        CreatedUtc = DateTimeOffset.UtcNow;
    }

    public string Id { get; }

    public string WorkingDirectory { get; }

    public PluginDevProfile Profile { get; }

    public EmbeddedRuntime Runtime { get; }

    public EmbeddedPagedMediaApi Api { get; }

    public PluginDevSessionState State { get; private set; }

    public DateTimeOffset CreatedUtc { get; }

    public IReadOnlyList<PluginDevDiagnostic> Diagnostics => _diagnostics;

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
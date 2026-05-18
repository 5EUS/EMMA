using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EMMA.Cli;

public static class PluginDevLocalServer
{
    private static readonly object BackgroundGate = new();
    private static readonly JsonSerializerOptions UiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
    private static readonly IReadOnlyList<PluginDevConsoleCommand> ConsoleCommands =
    [
      new("build", "build", "Run the normalized build plan for the active profile."),
    new("build-pack", "build-pack", "Build and then pack the active profile."),
    new("chapters", "chapters -i <mediaId>", "List chapters for a media item."),
    new("doctor", "doctor", "Show discovery and pre-launch diagnostics."),
    new("help", "help", "Show the browser console command list."),
    new("pack", "pack", "Pack the active profile."),
    new("page", "page -mi <mediaId> -ci <chapterId> -i <index>", "Get one page for a chapter."),
    new("page-asset", "page-asset -mi <mediaId> -ci <chapterId>", "Get summary information for a chapter page asset."),
    new("reload", "reload", "Refresh runtime state for the active profile."),
    new("scenario", "scenario <name> [query]", "Run a built-in dev scenario for the active profile."),
    new("search", "search -q <query>", "Search for media using the active runtime."),
    new("session", "session", "Show the resolved plugin development session."),
    new("video-segment", "video-segment -mi <mediaId> -si <streamId> -s <sequence>", "Get one video segment for a stream."),
    new("video-streams", "video-streams -i <mediaId>", "List video streams for a media item."),
    new("watch", "watch [start|stop|status]", "Manage file watching for the active profile.")
    ];
    private static Task? _backgroundTask;
    private static CancellationTokenSource? _backgroundCancellation;
    private static int? _backgroundPort;

    internal static string ConsoleCommandCatalogJson => JsonSerializer.Serialize(ConsoleCommands, UiJsonOptions);

    public static async Task RunAsync(PluginDevApplication application, int port, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.Services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.SerializerOptions.WriteIndented = true;
        });
        builder.Services.AddSingleton(application);

        var app = builder.Build();

        app.MapGet("/", () => Results.Content(PluginDevLocalUi.Html, "text/html"));
        app.MapGet("/api/session", (PluginDevApplication backend) => backend.GetSessionSnapshot());
        app.MapGet("/api/logs", (PluginDevApplication backend) => backend.GetLogs());
        app.MapPost("/api/ui/diagnostics-level", async (UpdateUiDiagnosticsLevelRequest request, PluginDevApplication backend) =>
          await ExecuteAsync(() => Task.FromResult<object>(backend.UpdateUiDiagnosticsLevel(request.DiagnosticsLevel)), backend));
        app.MapPost("/api/console/execute", async (ConsoleExecuteRequest request, PluginDevApplication backend) =>
          await ExecuteAsync(async () => new MessageResponse(await ExecuteConsoleCommandAsync(request.CommandLine, backend)), backend));
        app.MapPost("/api/logs/clear", async (PluginDevApplication backend) =>
          await ExecuteAsync(() =>
          {
              backend.ClearLogs();
              return Task.FromResult<object>(new MessageResponse("Console cleared."));
          }, backend));

        app.MapPost("/api/profiles/select", async (SelectProfileRequest request, PluginDevApplication backend) =>
            await ExecuteAsync(() => Task.FromResult<object>(backend.SelectProfile(request.Name)), backend));

        app.MapPost("/api/build", async (PluginDevApplication backend) =>
            await ExecuteAsync(async () => new MessageResponse(await backend.BuildAsync(CancellationToken.None)), backend));

        app.MapPost("/api/pack", async (PluginDevApplication backend) =>
          await ExecuteAsync(() => Task.FromResult<object>(backend.Pack()), backend));

        app.MapPost("/api/pack/open-directory", async (PluginDevApplication backend) =>
          await ExecuteAsync(() => Task.FromResult<object>(new OpenDirectoryResponse(backend.OpenPackDirectory())), backend));

        app.MapPost("/api/reload", async (PluginDevApplication backend) =>
            await ExecuteAsync(async () => new MessageResponse(await backend.ReloadAsync(CancellationToken.None)), backend));

        app.MapPost("/api/watch/start", async (PluginDevApplication backend) =>
          await ExecuteAsync(() => Task.FromResult<object>(backend.StartWatch()), backend));

        app.MapPost("/api/watch/stop", async (PluginDevApplication backend) =>
          await ExecuteAsync(() => Task.FromResult<object>(backend.StopWatch()), backend));

        app.MapPost("/api/scenarios/run", async (RunScenarioRequest request, PluginDevApplication backend) =>
            await ExecuteAsync(async () => await backend.RunScenarioAsync(request.Name, request.Query, CancellationToken.None), backend));

        application.RecordInfo($"Local session API started at http://127.0.0.1:{port}.");
        cancellationToken.ThrowIfCancellationRequested();
        await app.RunAsync();
    }

    public static string StartInBackground(PluginDevApplication application, int port)
    {
        lock (BackgroundGate)
        {
            if (_backgroundTask is { IsCompleted: false })
            {
                if (_backgroundPort == port)
                {
                    return $"Plugin dev UI already running at http://127.0.0.1:{port}.";
                }

                throw new InvalidOperationException($"Plugin dev UI is already running at http://127.0.0.1:{_backgroundPort}. Stop that session before starting another port.");
            }

            _backgroundCancellation?.Dispose();
            _backgroundCancellation = new CancellationTokenSource();
            _backgroundPort = port;

            var cancellationToken = _backgroundCancellation.Token;
            _backgroundTask = Task.Run(async () =>
            {
                try
                {
                    await RunAsync(application, port, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    application.RecordError($"Local session API stopped unexpectedly: {ex.Message}");
                }
                finally
                {
                    lock (BackgroundGate)
                    {
                        _backgroundCancellation?.Dispose();
                        _backgroundCancellation = null;
                        _backgroundTask = null;
                        _backgroundPort = null;
                    }
                }
            }, cancellationToken);

            return $"Serving plugin dev UI at http://127.0.0.1:{port} in the background.";
        }
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<object>> action, PluginDevApplication backend)
    {
        try
        {
            return Results.Json(await action());
        }
        catch (Exception ex)
        {
            backend.RecordError(ex.Message);
            return Results.Json(new ErrorResponse(ex.Message), statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<string> ExecuteConsoleCommandAsync(string commandLine, PluginDevApplication backend)
    {
        var trimmed = commandLine.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("Enter a command before running the browser console.");
        }

        var tokens = TokenizeCommandLine(trimmed);
        if (tokens.Count == 0)
        {
            throw new InvalidOperationException("Enter a command before running the browser console.");
        }

        backend.RecordInfo($"> {trimmed}");

        var command = tokens[0].ToLowerInvariant();
        var args = tokens.Skip(1).ToArray();

        switch (command)
        {
            case "help":
                EnsureNoArguments(command, args);
                backend.RecordInfo(BuildHelpText());
                return $"Executed '{trimmed}'.";

            case "session":
                EnsureNoArguments(command, args);
                backend.RecordInfo(FormatSessionSnapshot(backend.GetSessionSnapshot()));
                return $"Executed '{trimmed}'.";

            case "doctor":
                EnsureNoArguments(command, args);
                backend.RecordInfo(FormatDoctorSnapshot(backend.GetSessionSnapshot()));
                return $"Executed '{trimmed}'.";

            case "build":
                RejectAllScope(command, args);
                EnsureNoArguments(command, args);
                await backend.BuildAsync(CancellationToken.None);
                return $"Executed '{trimmed}'.";

            case "pack":
                RejectAllScope(command, args);
                EnsureNoArguments(command, args);
                backend.RecordInfo(FormatPackResult(null, backend.Pack()));
                return $"Executed '{trimmed}'.";

            case "build-pack":
                RejectAllScope(command, args);
                EnsureNoArguments(command, args);
                await backend.BuildAsync(CancellationToken.None);
                backend.RecordInfo(FormatPackResult(null, backend.Pack()));
                return $"Executed '{trimmed}'.";

            case "reload":
                EnsureNoArguments(command, args);
                await backend.ReloadAsync(CancellationToken.None);
                return $"Executed '{trimmed}'.";

            case "watch":
                {
                    var action = args.Length == 0 ? "start" : args[0].Trim().ToLowerInvariant();
                    if (args.Length > 1)
                    {
                        throw new InvalidOperationException("Usage: watch [start|stop|status]");
                    }

                    PluginDevWatchSnapshot snapshot = action switch
                    {
                        "start" => backend.StartWatch(),
                        "stop" => backend.StopWatch(),
                        "status" => backend.GetWatchSnapshot(),
                        _ => throw new InvalidOperationException($"Unknown watch action '{action}'. Use start, stop, or status.")
                    };

                    backend.RecordInfo(FormatWatchSnapshot(snapshot));
                    return $"Executed '{trimmed}'.";
                }

            case "scenario":
                {
                    if (args.Length == 0)
                    {
                        throw new InvalidOperationException("Usage: scenario <name> [query]");
                    }

                    var scenarioName = args[0];
                    var query = args.Length > 1 ? string.Join(' ', args.Skip(1)) : null;
                    var result = await backend.RunScenarioAsync(scenarioName, query, CancellationToken.None);
                    backend.RecordInfo($"Scenario '{result.Name}' {(result.Succeeded ? "completed" : "failed")}.");
                    return $"Executed '{trimmed}'.";
                }

            case "search":
                {
                    var query = ReadRequiredOption(command, args, "-q", "--query");
                    var results = await backend.SearchAsync(query, CancellationToken.None);
                    backend.RecordInfo(JsonSerializer.Serialize(results, UiJsonOptions));
                    return $"Executed '{trimmed}'.";
                }

            case "chapters":
                {
                    var mediaId = ReadRequiredOption(command, args, "-i", "--mediaId");
                    var results = await backend.GetChaptersAsync(mediaId, CancellationToken.None);
                    backend.RecordInfo(JsonSerializer.Serialize(results, UiJsonOptions));
                    return $"Executed '{trimmed}'.";
                }

            case "page":
                {
                    var mediaId = ReadRequiredOption(command, args, "-mi", "--mediaId");
                    var chapterId = ReadRequiredOption(command, args, "-ci", "--chapterId");
                    var indexText = ReadRequiredOption(command, args, "-i", "--index");
                    if (!int.TryParse(indexText, out var index))
                    {
                        throw new InvalidOperationException("Page index must be an integer.");
                    }

                    var result = await backend.GetPageAsync(mediaId, chapterId, index, CancellationToken.None);
                    backend.RecordInfo(result is null
                      ? "Page returned no content."
                      : JsonSerializer.Serialize(result, UiJsonOptions));
                    return $"Executed '{trimmed}'.";
                }

            case "page-asset":
                {
                    var mediaId = ReadRequiredOption(command, args, "-mi", "--mediaId");
                    var chapterId = ReadRequiredOption(command, args, "-ci", "--chapterId");
                    var result = await backend.GetPageAssetAsync(mediaId, chapterId, CancellationToken.None);
                    backend.RecordInfo(result is null
                      ? "Page asset returned no payload."
                      : $"Page asset size: {result.Length} byte(s). Browser console output is summary-only for binary responses.");
                    return $"Executed '{trimmed}'.";
                }

            case "video-streams":
                {
                    var mediaId = ReadRequiredOption(command, args, "-i", "--mediaId");
                    var result = await backend.GetVideoStreamsAsync(mediaId, CancellationToken.None);
                    backend.RecordInfo(JsonSerializer.Serialize(result, UiJsonOptions));
                    return $"Executed '{trimmed}'.";
                }

            case "video-segment":
                {
                    var mediaId = ReadRequiredOption(command, args, "-mi", "--mediaId");
                    var streamId = ReadRequiredOption(command, args, "-si", "--streamId");
                    var sequenceText = ReadRequiredOption(command, args, "-s", "--sequence");
                    if (!int.TryParse(sequenceText, out var sequence))
                    {
                        throw new InvalidOperationException("Segment sequence must be an integer.");
                    }

                    var result = await backend.GetVideoSegmentAsync(mediaId, streamId, sequence, CancellationToken.None);
                    backend.RecordInfo(result is null
                      ? "Video segment returned no payload."
                      : JsonSerializer.Serialize(result, UiJsonOptions));
                    return $"Executed '{trimmed}'.";
                }

            default:
                throw new InvalidOperationException($"Unknown browser console command '{command}'. Run 'help' to see supported commands.");
        }
    }

    private static IReadOnlyList<string> TokenizeCommandLine(string commandLine)
    {
        var tokens = new List<string>();
        var buffer = new StringBuilder();
        char? quote = null;
        var escape = false;

        foreach (var ch in commandLine)
        {
            if (escape)
            {
                buffer.Append(ch);
                escape = false;
                continue;
            }

            if (ch == '\\')
            {
                escape = true;
                continue;
            }

            if (quote is not null)
            {
                if (ch == quote)
                {
                    quote = null;
                }
                else
                {
                    buffer.Append(ch);
                }

                continue;
            }

            if (ch == '\'' || ch == '"')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (buffer.Length > 0)
                {
                    tokens.Add(buffer.ToString());
                    buffer.Clear();
                }

                continue;
            }

            buffer.Append(ch);
        }

        if (escape)
        {
            buffer.Append('\\');
        }

        if (quote is not null)
        {
            throw new InvalidOperationException("Unterminated quoted string in browser console command.");
        }

        if (buffer.Length > 0)
        {
            tokens.Add(buffer.ToString());
        }

        return tokens;
    }

    private static void EnsureNoArguments(string command, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException($"Command '{command}' does not accept additional arguments in the browser console.");
    }

    private static void RejectAllScope(string command, IReadOnlyList<string> args)
    {
        if (args.Count == 1 && string.Equals(args[0], "all", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Command '{command} all' is not supported in the browser console. Use the terminal CLI when you need multi-profile execution.");
        }
    }

    private static string ReadRequiredOption(string command, IReadOnlyList<string> args, params string[] optionNames)
    {
        var matches = new List<string>();

        for (var index = 0; index < args.Count; index++)
        {
            var token = args[index];
            if (!optionNames.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Count)
            {
                throw new InvalidOperationException($"Option '{token}' requires a value.");
            }

            matches.Add(args[index + 1]);
            index += 1;
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"Usage: {ConsoleCommands.First(item => string.Equals(item.Name, command, StringComparison.OrdinalIgnoreCase)).Usage}");
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException($"Option '{optionNames[0]}' was provided more than once.");
        }

        return matches[0];
    }

    private static string BuildHelpText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Available browser console commands:");
        foreach (var command in ConsoleCommands)
        {
            builder.Append("  ");
            builder.Append(command.Usage.PadRight(42));
            builder.AppendLine(command.Description);
        }

        builder.AppendLine();
        builder.AppendLine("Arrow Up/Down recalls previous commands in the input field.");
        builder.AppendLine("Quote values when they contain spaces, for example: search -q \"full metal\"");
        return builder.ToString().TrimEnd();
    }

    private static string FormatSessionSnapshot(PluginDevSessionSnapshot session)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Session ID: {session.Id}");
        builder.AppendLine($"State: {session.State}");
        builder.AppendLine($"Working Directory: {session.WorkingDirectory}");
        builder.AppendLine($"Profile: {session.Profile.Name}");
        builder.AppendLine($"Plugin ID: {session.Profile.PluginId}");
        builder.AppendLine($"Host URL: {session.Profile.HostUrl}");
        builder.AppendLine($"Runtime Target: {session.Profile.RuntimeTarget}");
        builder.AppendLine($"Execution Mode: {session.Profile.ExecutionMode}");
        builder.AppendLine($"Logging: plugin={(session.Profile.Logging.Plugin ? "on" : "off")}, aspNetHost={(session.Profile.Logging.AspNetHost ? "on" : "off")}, httpClient={(session.Profile.Logging.HttpClient ? "on" : "off")}");
        if (session.Profile.Sync.Enabled)
        {
            builder.AppendLine($"Sync: onBuild={(session.Profile.Sync.OnBuild ? "on" : "off")}, cleanDestination={(session.Profile.Sync.CleanDestination ? "on" : "off")}, destination={session.Profile.Sync.DestinationPath}");
        }

        if (!string.IsNullOrWhiteSpace(session.Profile.WasiSdkPath))
        {
            builder.AppendLine($"WASI SDK Path: {session.Profile.WasiSdkPath}");
        }

        builder.AppendLine($"Runtime Adapter: {session.RuntimeAdapterName}");
        builder.AppendLine($"Profile Source: {(session.Profile.IsInferred ? "inferred" : "configured")}");

        if (!string.IsNullOrWhiteSpace(session.Profile.ArtifactPath))
        {
            builder.AppendLine($"Artifact Path: {session.Profile.ArtifactPath}");
        }

        if (session.Profile.WatchGlobs.Count > 0)
        {
            builder.AppendLine($"Watch Globs: {string.Join(", ", session.Profile.WatchGlobs)}");
        }

        builder.AppendLine($"Watch Status: {session.Watch.Status}");
        builder.AppendLine($"Watch Behavior: {session.Watch.Behavior}");

        if (session.Watch.LastChangedUtc is not null)
        {
            builder.AppendLine($"Watch Last Change: {session.Watch.LastChangedUtc:O} ({session.Watch.LastChangedPath})");
        }

        if (session.Watch.LastReloadUtc is not null)
        {
            builder.AppendLine($"Watch Last Reload: {session.Watch.LastReloadUtc:O} ({session.Watch.LastReloadMessage})");
        }

        builder.AppendLine($"Discovery Root: {session.RootDirectory}");
        builder.AppendLine($"Manifest: {session.ManifestPath ?? "<not found>"}");
        builder.AppendLine($"Project: {session.ProjectFilePath ?? "<not found>"}");

        if (session.AvailableProfiles.Count > 0)
        {
            builder.AppendLine("Available Profiles:");
            foreach (var profile in session.AvailableProfiles)
            {
                var source = profile.IsInferred ? "inferred" : "configured";
                var artifactSuffix = string.IsNullOrWhiteSpace(profile.ArtifactPath) ? string.Empty : $" artifact={profile.ArtifactPath}";
                var syncSuffix = profile.Sync.Enabled ? $" sync={profile.Sync.DestinationPath}" : string.Empty;
                builder.AppendLine($"  - {profile.Name} [{source}] target={profile.RuntimeTarget} mode={profile.ExecutionMode}{artifactSuffix}{syncSuffix}");
            }
        }

        if (session.Diagnostics.Count > 0)
        {
            builder.AppendLine("Diagnostics:");
            foreach (var diagnostic in session.Diagnostics)
            {
                builder.AppendLine($"  - [{diagnostic.Severity}] {diagnostic.Code}: {diagnostic.Message}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatDoctorSnapshot(PluginDevSessionSnapshot session)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Plugin development doctor");
        builder.AppendLine($"  Root: {session.RootDirectory}");
        builder.AppendLine($"  Manifest: {session.ManifestPath ?? "<not found>"}");
        builder.AppendLine($"  Project: {session.ProjectFilePath ?? "<not found>"}");
        builder.AppendLine($"  Plugin ID: {session.PluginId}");

        if (!string.IsNullOrWhiteSpace(session.PluginName))
        {
            builder.AppendLine($"  Plugin Name: {session.PluginName}");
        }

        if (session.MediaTypes.Count > 0)
        {
            builder.AppendLine($"  Media Types: {string.Join(", ", session.MediaTypes)}");
        }

        if (session.SupportedTargets.Count > 0)
        {
            builder.AppendLine($"  Supported Targets: {string.Join(", ", session.SupportedTargets)}");
        }

        if (session.ArtifactCandidates.Count > 0)
        {
            builder.AppendLine("  Artifact Candidates:");
            foreach (var artifact in session.ArtifactCandidates)
            {
                var status = artifact.Exists ? "present" : "missing";
                builder.AppendLine($"    - {artifact.Target} [{artifact.Kind}] {status}: {artifact.Path}");
            }
        }

        if (session.Diagnostics.Count == 0)
        {
            builder.AppendLine("  No diagnostics.");
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine("  Diagnostics:");
        foreach (var diagnostic in session.Diagnostics)
        {
            builder.AppendLine($"    - [{diagnostic.Severity}] {diagnostic.Code}: {diagnostic.Message}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatPackResult(string? profileName, PluginDevPackResult result)
    {
        var prefix = string.IsNullOrWhiteSpace(profileName) ? string.Empty : $"[{profileName}] ";
        return string.Join(Environment.NewLine,
        [
          $"{prefix}Package: {result.PackagePath}",
            $"{prefix}Pack Directory: {result.PackageDirectory}",
            $"{prefix}Manifest: {result.ManifestPath}",
            $"{prefix}Artifact: {result.ArtifactPath}"
        ]);
    }

    private static string FormatWatchSnapshot(PluginDevWatchSnapshot snapshot)
    {
        var lines = new List<string>
          {
            $"Watch Enabled: {snapshot.IsEnabled}",
            $"Watch Status: {snapshot.Status}",
            $"Supports Reload: {snapshot.SupportsReload}",
            $"Behavior: {snapshot.Behavior}"
          };

        if (snapshot.WatchGlobs.Count > 0)
        {
            lines.Add($"Watch Globs: {string.Join(", ", snapshot.WatchGlobs)}");
        }

        if (snapshot.LastChangedUtc is not null)
        {
            lines.Add($"Last Change: {snapshot.LastChangedUtc:O} ({snapshot.LastChangedPath})");
        }

        if (snapshot.LastReloadUtc is not null)
        {
            lines.Add($"Last Reload: {snapshot.LastReloadUtc:O} ({snapshot.LastReloadMessage})");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private sealed record SelectProfileRequest(string Name);
    private sealed record RunScenarioRequest(string Name, string? Query);
    private sealed record UpdateUiDiagnosticsLevelRequest(string DiagnosticsLevel);
    private sealed record ConsoleExecuteRequest(string CommandLine);
    private sealed record MessageResponse(string Message);
    private sealed record OpenDirectoryResponse(string Directory);
    private sealed record ErrorResponse(string Error);
    private sealed record PluginDevConsoleCommand(string Name, string Usage, string Description);
}

internal static class PluginDevLocalUi
{
    public static string Html => $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>EMMA Plugin Dev</title>
  <style>
    :root {
      --bg: #f3efe6;
      --panel: rgba(255,255,255,0.84);
      --panel-strong: rgba(255,255,255,0.92);
      --ink: #1c1b19;
      --muted: #655f55;
      --accent: #0f766e;
      --accent-2: #d97706;
      --success: #15803d;
      --info: #0f766e;
      --warning: #b45309;
      --error: #b91c1c;
      --line: rgba(28,27,25,0.12);
      --shadow: 0 18px 60px rgba(43,34,18,0.12);
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      font-family: "Segoe UI Variable", "Aptos", "Trebuchet MS", sans-serif;
      color: var(--ink);
      background:
        radial-gradient(circle at top left, rgba(15,118,110,0.14), transparent 26%),
        radial-gradient(circle at top right, rgba(217,119,6,0.18), transparent 28%),
        linear-gradient(180deg, #f7f3eb 0%, var(--bg) 100%);
      min-height: 100vh;
    }
    .shell {
      max-width: 1280px;
      margin: 0 auto;
      padding: 28px;
    }
    .hero {
      display: grid;
      gap: 12px;
      margin-bottom: 20px;
    }
    .eyebrow {
      letter-spacing: 0.14em;
      text-transform: uppercase;
      color: var(--accent);
      font-size: 12px;
      font-weight: 700;
    }
    h1 {
      margin: 0;
      font-size: clamp(28px, 4vw, 46px);
      line-height: 1;
      font-family: Georgia, "Iowan Old Style", serif;
      font-weight: 600;
    }
    .sub {
      color: var(--muted);
      max-width: 760px;
      font-size: 15px;
      line-height: 1.5;
    }
    .grid {
      display: grid;
      grid-template-columns: 360px 1fr;
      gap: 20px;
      align-items: start;
    }
    .card {
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: 22px;
      box-shadow: var(--shadow);
      backdrop-filter: blur(12px);
    }
    .card > .inner {
      padding: 18px;
    }
    .stack {
      display: grid;
      gap: 18px;
      align-content: start;
    }
    .stack > .card { align-self: start; }
    .section-title {
      margin: 0 0 12px;
      font-size: 15px;
      text-transform: uppercase;
      letter-spacing: 0.08em;
      color: var(--muted);
    }
    .kv { display: grid; gap: 8px; min-width: 0; }
    .kv div { display: grid; gap: 3px; min-width: 0; }
    .watch-panel {
      display: grid;
      gap: 8px;
      max-height: 280px;
      overflow: auto;
      padding-right: 4px;
    }
    .label { font-size: 12px; color: var(--muted); text-transform: uppercase; letter-spacing: 0.08em; }
    .value {
      font-size: 14px;
      min-width: 0;
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      word-break: break-word;
    }
    .control-row { display: grid; gap: 10px; }
    select, input, button {
      width: 100%;
      border-radius: 14px;
      border: 1px solid var(--line);
      padding: 11px 12px;
      font: inherit;
      background: var(--panel-strong);
      color: var(--ink);
    }
    button {
      cursor: pointer;
      font-weight: 700;
      transition: transform 120ms ease, background 120ms ease;
    }
    button:hover { transform: translateY(-1px); }
    .btn-accent { background: linear-gradient(135deg, var(--accent), #155e75); color: white; border: 0; }
    .btn-warm { background: linear-gradient(135deg, var(--accent-2), #b45309); color: white; border: 0; }
    .inline { display: grid; grid-template-columns: 1fr auto; gap: 10px; }
    .badge-row { display: flex; gap: 8px; flex-wrap: wrap; }
    .badge {
      border-radius: 999px;
      padding: 6px 10px;
      background: rgba(15,118,110,0.1);
      color: var(--accent);
      font-size: 12px;
      font-weight: 700;
    }
    .diag { display: grid; gap: 8px; }
    .diag-item, .log-item {
      border: 1px solid var(--line);
      border-radius: 14px;
      padding: 10px 12px;
      background: rgba(255,255,255,0.55);
      min-width: 0;
      position: relative;
    }
    .diag-item { border-left-width: 6px; }
    .log-item { border-left-width: 6px; }
    .diag-item.info { border-color: rgba(15,118,110,0.2); background: rgba(240,253,250,0.88); }
    .diag-item.warning { border-color: rgba(180,83,9,0.24); background: rgba(255,247,237,0.88); }
    .diag-item.error { border-color: rgba(185,28,28,0.24); background: rgba(254,242,242,0.88); }
    .log-item.success { border-color: rgba(21,128,61,0.24); background: rgba(240,253,244,0.92); }
    .log-item.info { border-color: rgba(15,118,110,0.2); background: rgba(240,253,250,0.88); }
    .log-item.warning { border-color: rgba(180,83,9,0.24); background: rgba(255,247,237,0.88); }
    .log-item.error { border-color: rgba(185,28,28,0.24); background: rgba(254,242,242,0.88); }
    .log-item.recent::before {
      content: '';
      position: absolute;
      left: -1px;
      top: -1px;
      bottom: -1px;
      width: 4px;
      background: var(--accent-2);
      border-radius: 14px 0 0 14px;
      pointer-events: none;
    }
    .meta-chips { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; min-width: 0; }
    .chip {
      border-radius: 999px;
      padding: 5px 9px;
      font-size: 11px;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 0.08em;
      border: 1px solid transparent;
    }
    .chip.success { color: var(--success); background: rgba(21,128,61,0.12); border-color: rgba(21,128,61,0.18); }
    .chip.info { color: var(--info); background: rgba(15,118,110,0.1); border-color: rgba(15,118,110,0.16); }
    .chip.warning { color: var(--warning); background: rgba(217,119,6,0.12); border-color: rgba(217,119,6,0.18); }
    .chip.error { color: var(--error); background: rgba(185,28,28,0.12); border-color: rgba(185,28,28,0.18); }
    .chip.type { color: var(--muted); background: rgba(101,95,85,0.08); border-color: rgba(101,95,85,0.14); }
    .scenario-help { color: var(--muted); font-size: 13px; line-height: 1.45; }
    .toolbar-row { display: flex; justify-content: space-between; gap: 12px; align-items: center; }
    .toolbar-row button { width: auto; min-width: 132px; }
    .toolbar-inline { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
    .toolbar-inline label {
      color: var(--muted);
      font-size: 12px;
      text-transform: uppercase;
      letter-spacing: 0.08em;
      font-weight: 700;
    }
    .toolbar-inline select { width: auto; min-width: 160px; padding-right: 30px; }
    .console-shell {
      display: grid;
      gap: 8px;
      margin: 8px 0 12px;
      padding: 12px;
      border-radius: 16px;
      border: 1px solid var(--line);
      background: rgba(255,255,255,0.58);
    }
    .console-input-row {
      display: grid;
      grid-template-columns: 1fr auto;
      gap: 10px;
      align-items: center;
      position: relative;
    }
    .console-input-row button { width: auto; min-width: 96px; }
    .console-input {
      font-family: "Cascadia Code", "Consolas", monospace;
      font-size: 13px;
    }
    .console-autocomplete {
      position: absolute;
      left: 0;
      right: 106px;
      top: calc(100% + 6px);
      z-index: 10;
      display: none;
      max-height: 220px;
      overflow: auto;
      border-radius: 14px;
      border: 1px solid var(--line);
      background: rgba(255,255,255,0.96);
      box-shadow: 0 18px 40px rgba(43,34,18,0.16);
      backdrop-filter: blur(12px);
    }
    .console-autocomplete.visible { display: grid; }
    .console-suggestion {
      padding: 10px 12px;
      border: 0;
      border-bottom: 1px solid rgba(28,27,25,0.08);
      background: transparent;
      text-align: left;
      width: 100%;
      min-width: 0;
      color: var(--ink);
      font-weight: 500;
      border-radius: 0;
      font-family: "Cascadia Code", "Consolas", monospace;
      font-size: 12px;
    }
    .console-suggestion:last-child { border-bottom: 0; }
    .console-suggestion:hover,
    .console-suggestion:focus-visible {
      background: rgba(15,118,110,0.1);
      outline: 0;
      transform: none;
    }
    .console-hint {
      color: var(--muted);
      font-size: 12px;
      line-height: 1.45;
      min-height: 1.45em;
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      word-break: break-word;
    }
    .log-panel {
      min-height: 320px;
      max-height: 640px;
      overflow: auto;
      display: grid;
      gap: 8px;
    }
    .log-meta {
      display: flex;
      justify-content: space-between;
      gap: 10px;
      margin-bottom: 4px;
      color: var(--muted);
      font-size: 11px;
      text-transform: uppercase;
      letter-spacing: 0.08em;
      min-width: 0;
      flex-wrap: wrap;
    }
    .log-meta > * { min-width: 0; }
    .log-message {
      font-family: "Cascadia Code", "Consolas", monospace;
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      word-break: break-word;
      font-size: 12px;
      line-height: 1.5;
    }
    .ansi-bold { font-weight: 700; }
    .diag-message {
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      word-break: break-word;
      min-width: 0;
    }
    .empty { color: var(--muted); font-size: 14px; }
    @media (max-width: 960px) {
      .grid { grid-template-columns: 1fr; }
      .shell { padding: 18px; }
    }
  </style>
</head>
<body>
  <div class="shell">
    <header class="hero">
      <div class="eyebrow">EMMA Plugin Dev Platform</div>
      <h1>Session API and browser control surface</h1>
      <div class="sub">Refer to the documentation for more details.</div>
    </header>

    <main class="grid">
      <section class="stack">
        <article class="card"><div class="inner">
          <h2 class="section-title">Session</h2>
          <div class="kv" id="session-meta"></div>
        </div></article>

        <article class="card"><div class="inner stack">
          <div>
            <h2 class="section-title">Profile</h2>
            <div class="control-row">
              <select id="profile-select"></select>
              <button class="btn-accent" id="profile-switch">Switch profile</button>
            </div>
          </div>
          <div>
            <h2 class="section-title">Actions</h2>
            <div class="control-row">
              <button id="build-btn">Build active profile</button>
              <button id="pack-btn">Pack active profile</button>
              <button id="open-pack-dir-btn">Open pack directory</button>
              <button id="reload-btn">Reload active runtime</button>
            </div>
          </div>
          <div>
            <h2 class="section-title">Watch</h2>
            <div class="kv watch-panel" id="watch-meta"></div>
            <div class="control-row">
              <button id="watch-start-btn">Start watch</button>
              <button id="watch-stop-btn">Stop watch</button>
            </div>
          </div>
          <div>
            <h2 class="section-title">Scenario</h2>
            <div class="control-row">
              <select id="scenario-select" aria-label="scenario selection"></select>
              <div class="scenario-help" id="scenario-description"></div>
              <input id="scenario-query" value="naruto" aria-label="scenario query" />
              <button class="btn-warm" id="scenario-btn">Run scenario</button>
            </div>
          </div>
        </div></article>
      </section>

      <section class="stack">
        <article class="card"><div class="inner">
          <div class="toolbar-row">
            <h2 class="section-title">Diagnostics</h2>
            <div class="toolbar-inline">
              <label for="diagnostics-level">Level</label>
              <select id="diagnostics-level" aria-label="diagnostics level">
                <option value="info">Info+</option>
                <option value="warning">Warning+</option>
                <option value="error">Error only</option>
              </select>
            </div>
          </div>
          <div class="diag" id="diagnostics"></div>
        </div></article>

        <article class="card"><div class="inner">
          <div class="toolbar-row">
            <div class="log-meta"><span>Operation log</span><span id="log-status">idle</span></div>
            <button id="clear-logs-btn">Clear console</button>
          </div>
          <div class="console-shell">
            <label class="label" for="console-input">Console Input</label>
            <div class="console-input-row">
              <input class="console-input" id="console-input" aria-label="console input" placeholder="Type a command and press Enter" autocapitalize="off" autocomplete="off" autocorrect="off" spellcheck="false" />
              <button class="btn-accent" id="console-run-btn">Run</button>
              <div class="console-autocomplete" id="console-autocomplete" role="listbox" aria-label="command suggestions"></div>
            </div>
            <div class="console-hint" id="console-hint">Type help for the available browser console commands. Arrow Up and Arrow Down recall previous commands.</div>
          </div>
          <div class="log-panel" id="logs"></div>
        </div></article>
      </section>
    </main>
  </div>

  <script>
    const consoleCommandCatalog = {{PluginDevLocalServer.ConsoleCommandCatalogJson}};
    const sessionMeta = document.getElementById('session-meta');
    const profileSelect = document.getElementById('profile-select');
    const watchMeta = document.getElementById('watch-meta');
    const diagnostics = document.getElementById('diagnostics');
    const diagnosticsLevel = document.getElementById('diagnostics-level');
    const logs = document.getElementById('logs');
    const logStatus = document.getElementById('log-status');
    const consoleInput = document.getElementById('console-input');
    const consoleRunButton = document.getElementById('console-run-btn');
    const consoleAutocomplete = document.getElementById('console-autocomplete');
    const consoleHint = document.getElementById('console-hint');
    const scenarioSelect = document.getElementById('scenario-select');
    const scenarioDescription = document.getElementById('scenario-description');
    const scenarioQuery = document.getElementById('scenario-query');
    let refreshEpoch = 0;
    let suppressRefreshRender = false;
    let lastSessionProfileName = null;
    let pendingProfileName = null;
    let profileSwitchInFlight = false;
    let lastScenarioName = null;
    let diagnosticsLevelInFlight = false;
    let latestScenarios = [];
    let consoleHistory = loadConsoleHistory();
    let consoleHistoryIndex = null;
    let consoleDraft = '';
    let consoleSuggestionValues = [];
    let consoleAutocompletePinned = false;
    let hasRenderedLogsOnce = false;
    let seenLogKeys = new Set();

    const severityRank = { info: 0, warning: 1, error: 2 };

    function loadConsoleHistory() {
      try {
        const raw = window.localStorage.getItem('emma-plugin-dev-console-history');
        const parsed = raw ? JSON.parse(raw) : [];
        return Array.isArray(parsed) ? parsed.filter(item => typeof item === 'string') : [];
      } catch {
        return [];
      }
    }

    function saveConsoleHistory() {
      try {
        window.localStorage.setItem('emma-plugin-dev-console-history', JSON.stringify(consoleHistory));
      } catch {
      }
    }

    function rememberConsoleCommand(commandLine) {
      const trimmed = commandLine.trim();
      if (!trimmed) {
        return;
      }

      consoleHistory = consoleHistory.filter(item => item !== trimmed);
      consoleHistory.push(trimmed);
      if (consoleHistory.length > 50) {
        consoleHistory = consoleHistory.slice(consoleHistory.length - 50);
      }

      saveConsoleHistory();
      consoleHistoryIndex = null;
      consoleDraft = '';
    }

    function resetConsoleHistoryCursor() {
      consoleHistoryIndex = null;
      consoleDraft = '';
    }

    function moveConsoleHistory(direction) {
      if (!consoleHistory.length) {
        return;
      }

      if (consoleHistoryIndex === null) {
        consoleDraft = consoleInput.value;
        consoleHistoryIndex = consoleHistory.length;
      }

      const nextIndex = Math.max(0, Math.min(consoleHistory.length, consoleHistoryIndex + direction));
      consoleHistoryIndex = nextIndex;

      if (nextIndex === consoleHistory.length) {
        consoleInput.value = consoleDraft;
        return;
      }

      consoleInput.value = consoleHistory[nextIndex];
      updateConsoleHint();
      renderConsoleSuggestions();
    }

    function buildConsoleSuggestions() {
      const values = [];
      const seen = new Set();

      function addSuggestion(value) {
        const normalized = (value || '').trim();
        if (!normalized || seen.has(normalized)) {
          return;
        }

        seen.add(normalized);
        values.push(normalized);
      }

      consoleCommandCatalog.forEach(item => {
        addSuggestion(item.usage);
        addSuggestion(item.name);
      });

      latestScenarios.forEach(item => {
        addSuggestion(`scenario ${item.name}${item.defaultQuery ? ` ${item.defaultQuery}` : ''}`);
      });

      consoleSuggestionValues = values;
      if (consoleAutocomplete.classList.contains('visible')) {
        renderConsoleSuggestions();
      }
    }

    function hideConsoleSuggestions() {
      consoleAutocomplete.classList.remove('visible');
      consoleAutocomplete.innerHTML = '';
      consoleAutocompletePinned = false;
    }

    function showConsoleSuggestions() {
      renderConsoleSuggestions();
    }

    function renderConsoleSuggestions() {
      const filter = consoleInput.value.trim().toLowerCase();
      const matches = consoleSuggestionValues.filter(value => {
        if (!filter) {
          return true;
        }

        return value.toLowerCase().includes(filter);
      }).slice(0, 8);

      if (!matches.length) {
        hideConsoleSuggestions();
        return;
      }

      consoleAutocomplete.innerHTML = '';
      matches.forEach(value => {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'console-suggestion';
        button.textContent = value;
        button.setAttribute('role', 'option');
        button.addEventListener('pointerdown', event => {
          event.preventDefault();
          consoleInput.value = value;
          updateConsoleHint();
          hideConsoleSuggestions();
          consoleInput.focus();
        });
        consoleAutocomplete.appendChild(button);
      });

      consoleAutocomplete.classList.add('visible');
    }

    function updateConsoleHint() {
      const value = consoleInput.value.trim();
      if (!value) {
        consoleHint.textContent = 'Type help for the available browser console commands. Arrow Up and Arrow Down recall previous commands.';
        return;
      }

      const commandName = value.split(/\s+/)[0].toLowerCase();
      const match = consoleCommandCatalog.find(item => item.name.toLowerCase() === commandName)
        || consoleCommandCatalog.find(item => item.name.toLowerCase().startsWith(commandName));

      if (match) {
        consoleHint.textContent = `${match.usage} - ${match.description}`;
        return;
      }

      consoleHint.textContent = `Unknown command '${commandName}'. Type help to see supported commands.`;
    }

    async function runConsoleCommand() {
      const commandLine = consoleInput.value.trim();
      if (!commandLine) {
        updateConsoleHint();
        return;
      }

      hideConsoleSuggestions();
      rememberConsoleCommand(commandLine);
      consoleRunButton.disabled = true;
      consoleInput.disabled = true;
      try {
        await perform(`running ${commandLine.split(/\s+/, 1)[0]}`, () => api('/api/console/execute', {
          method: 'POST',
          body: JSON.stringify({ commandLine })
        }));
        consoleInput.value = '';
        resetConsoleHistoryCursor();
        updateConsoleHint();
      } finally {
        consoleRunButton.disabled = false;
        consoleInput.disabled = false;
        consoleInput.focus();
      }
    }

    function normalizeSeverity(item) {
      return (item.severity || item.level || (item.isError ? 'error' : 'info') || 'info').toLowerCase();
    }

    function normalizeLogSeverity(entry) {
      const baseSeverity = normalizeSeverity(entry);
      if (baseSeverity !== 'info') {
        return baseSeverity;
      }

      const message = (entry.message || '').trim().toLowerCase();
      if (!message) {
        return baseSeverity;
      }

      if (message.startsWith('build completed')
          || message.startsWith('watch build completed')
          || message.startsWith('watch reload completed')
          || message.startsWith('pack completed')
          || message.startsWith('opened pack directory')
          || message.startsWith('reloaded')
          || message.startsWith('scenario ') && message.endsWith(' completed.')) {
        return 'success';
      }

      return baseSeverity;
    }

    function getLogEntryKey(entry) {
      return `${entry.timestampUtc || ''}|${entry.level || ''}|${entry.message || ''}`;
    }

    function isRecentLogEntry(entry, isFirstRender, hasSeenEntry) {
      const timestamp = Date.parse(entry.timestampUtc || '');
      const ageMs = Number.isNaN(timestamp)
        ? Number.POSITIVE_INFINITY
        : Date.now() - timestamp;

      if (ageMs <= 15000) {
        return true;
      }

      return !isFirstRender && !hasSeenEntry;
    }

    function appendAnsiText(container, value) {
      const text = value || '';
      const oscStripped = text.replace(/\u001b\][^\u0007]*(\u0007|\u001b\\)/g, '');
      const parts = oscStripped.split(/(\u001b\[[0-9;]*m)/g);
      let state = createAnsiState();

      parts.forEach(part => {
        if (!part) {
          return;
        }

        const sgrMatch = part.match(/^\u001b\[([0-9;]*)m$/);
        if (sgrMatch) {
          state = applyAnsiCodes(state, sgrMatch[1]);
          return;
        }

        const span = document.createElement('span');
        if (state.color) {
          span.style.color = state.color;
        }
        if (state.bold) {
          span.classList.add('ansi-bold');
        }
        span.textContent = part;
        container.appendChild(span);
      });
    }

    function createAnsiState() {
      return { color: '', bold: false };
    }

    function applyAnsiCodes(state, codeText) {
      const next = { ...state };
      const codes = codeText
        ? codeText.split(';').map(value => Number.parseInt(value || '0', 10)).filter(value => !Number.isNaN(value))
        : [0];

      if (!codes.length) {
        codes.push(0);
      }

      codes.forEach(code => {
        if (code === 0) {
          next.color = '';
          next.bold = false;
          return;
        }

        if (code === 1) {
          next.bold = true;
          return;
        }

        if (code === 22) {
          next.bold = false;
          return;
        }

        if (code === 39) {
          next.color = '';
          return;
        }

        const mappedColor = mapAnsiColor(code);
        if (mappedColor) {
          next.color = mappedColor;
        }
      });

      return next;
    }

    function mapAnsiColor(code) {
      switch (code) {
        case 30: return '#1f2937';
        case 31: return '#dc2626';
        case 32: return '#16a34a';
        case 33: return '#d97706';
        case 34: return '#2563eb';
        case 35: return '#c026d3';
        case 36: return '#0891b2';
        case 37: return '#e5e7eb';
        case 90: return '#6b7280';
        case 91: return '#ef4444';
        case 92: return '#22c55e';
        case 93: return '#f59e0b';
        case 94: return '#60a5fa';
        case 95: return '#e879f9';
        case 96: return '#22d3ee';
        case 97: return '#f9fafb';
        default: return '';
      }
    }

    function normalizeDiagnosticsLevel(level) {
      return severityRank[level] === undefined ? 'info' : level;
    }

    function shouldRenderDiagnostic(item, minimumLevel) {
      const severity = normalizeSeverity(item);
      return (severityRank[severity] ?? 0) >= (severityRank[minimumLevel] ?? 0);
    }

    function renderScenarioCatalog(session) {
      const scenarios = session.scenarios || [];
      latestScenarios = scenarios;
      buildConsoleSuggestions();
      const selectedName = scenarios.some(item => item.name === lastScenarioName)
        ? lastScenarioName
        : (scenarios[0]?.name || '');

      scenarioSelect.innerHTML = '';
      scenarios.forEach(item => {
        const option = document.createElement('option');
        option.value = item.name;
        option.textContent = item.displayName;
        option.selected = item.name === selectedName;
        scenarioSelect.appendChild(option);
      });

      scenarioSelect.disabled = scenarios.length === 0;
      scenarioQuery.disabled = scenarios.length === 0;
      document.getElementById('scenario-btn').disabled = scenarios.length === 0;

      const selected = scenarios.find(item => item.name === selectedName) || scenarios[0] || null;
      if (!selected) {
        lastScenarioName = null;
        scenarioDescription.textContent = 'No scenarios are available for the active runtime adapter.';
        scenarioQuery.value = '';
        scenarioQuery.placeholder = 'Scenario query';
        return;
      }

      lastScenarioName = selected.name;
      scenarioDescription.textContent = selected.description;
      scenarioQuery.placeholder = selected.queryLabel || 'Query';
      scenarioQuery.disabled = !selected.supportsQuery;
      if (!scenarioQuery.value || scenarioQuery.dataset.scenario !== selected.name) {
        scenarioQuery.value = selected.defaultQuery || '';
      }

      scenarioQuery.dataset.scenario = selected.name;
    }

    async function api(path, options = {}) {
      const response = await fetch(path, {
        headers: { 'content-type': 'application/json', ...(options.headers || {}) },
        ...options
      });

      const body = await response.json().catch(() => ({}));
      if (!response.ok) {
        throw new Error(body.error || response.statusText || 'Request failed');
      }

      return body;
    }

    function renderSession(session) {
      lastSessionProfileName = session.profile.name;
      if (pendingProfileName === session.profile.name) {
        pendingProfileName = null;
      }

      const availableProfileNames = new Set(session.availableProfiles.map(profile => profile.name));
      const selectedProfileName = pendingProfileName && availableProfileNames.has(pendingProfileName)
        ? pendingProfileName
        : session.profile.name;

      sessionMeta.innerHTML = '';
      const fields = [
        ['Session', session.id],
        ['State', session.state],
        ['Profile', session.profile.name],
        ['Plugin', session.pluginId],
        ['Host', session.profile.hostUrl],
        ['Runtime', `${session.profile.runtimeTarget} / ${session.profile.executionMode}`],
        ['Adapter', session.runtimeAdapterName],
        ['Manifest', session.manifestPath || '<not found>']
      ];

      fields.forEach(([label, value]) => {
        const wrap = document.createElement('div');
        wrap.innerHTML = `<div class="label">${label}</div><div class="value">${value}</div>`;
        sessionMeta.appendChild(wrap);
      });

      profileSelect.innerHTML = '';
      session.availableProfiles.forEach(profile => {
        const option = document.createElement('option');
        option.value = profile.name;
        option.textContent = `${profile.name} (${profile.runtimeTarget}/${profile.executionMode})`;
        option.selected = profile.name === selectedProfileName;
        profileSelect.appendChild(option);
      });

      watchMeta.innerHTML = '';
      const watchFields = [
        ['Status', session.watch.status],
        ['Enabled', String(session.watch.isEnabled)],
        ['Behavior', session.watch.behavior],
        ['Globs', session.watch.watchGlobs.length ? session.watch.watchGlobs.join(', ') : '<none>'],
        ['Last Change', session.watch.lastChangedUtc ? `${new Date(session.watch.lastChangedUtc).toLocaleTimeString()} · ${session.watch.lastChangedPath}` : '<none>'],
        ['Last Reload', session.watch.lastReloadUtc ? `${new Date(session.watch.lastReloadUtc).toLocaleTimeString()} · ${session.watch.lastReloadMessage}` : '<none>']
      ];

      watchFields.forEach(([label, value]) => {
        const wrap = document.createElement('div');
        wrap.innerHTML = `<div class="label">${label}</div><div class="value">${value}</div>`;
        watchMeta.appendChild(wrap);
      });

      renderScenarioCatalog(session);

      const selectedDiagnosticsLevel = normalizeDiagnosticsLevel(session.ui?.diagnosticsLevel || diagnosticsLevel.value || 'info');
      if (!diagnosticsLevelInFlight) {
        diagnosticsLevel.value = selectedDiagnosticsLevel;
      }

      diagnostics.innerHTML = '';
      const visibleDiagnostics = session.diagnostics.filter(item => shouldRenderDiagnostic(item, selectedDiagnosticsLevel));
      if (!visibleDiagnostics.length) {
        diagnostics.innerHTML = '<div class="empty">No diagnostics.</div>';
      } else {
        visibleDiagnostics.forEach(item => {
          const node = document.createElement('div');
          const severity = normalizeSeverity(item);
          const type = (item.type || 'general').toLowerCase();
          node.className = `diag-item ${severity}`;
          node.innerHTML = `<div class="log-meta"><div class="meta-chips"><span class="chip ${severity}">${severity}</span><span class="chip type">${type}</span></div><span>${item.code}</span></div><div class="diag-message"></div>`;
          node.querySelector('.diag-message').textContent = item.message;
          diagnostics.appendChild(node);
        });
      }
    }

    function renderLogs(entries) {
      logs.innerHTML = '';
      if (!entries.length) {
        logs.innerHTML = '<div class="empty">No log entries yet.</div>';
        return;
      }

      const isFirstRender = !hasRenderedLogsOnce;
      const currentKeys = new Set();
      entries.slice().reverse().forEach(entry => {
        const node = document.createElement('div');
        const severity = normalizeLogSeverity(entry);
        const key = getLogEntryKey(entry);
        currentKeys.add(key);
        const recent = isRecentLogEntry(entry, isFirstRender, seenLogKeys.has(key));
        node.className = `log-item ${severity}${recent ? ' recent' : ''}`;
        node.innerHTML = `<div class="log-meta"><div class="meta-chips"><span class="chip ${severity}">${severity}</span></div><span>${new Date(entry.timestampUtc).toLocaleTimeString()}</span></div><div class="log-message"></div>`;
        appendAnsiText(node.querySelector('.log-message'), entry.message);
        logs.appendChild(node);
      });

      seenLogKeys = currentKeys;
      hasRenderedLogsOnce = true;
    }

    function deriveStatusLabel(session, entries) {
      const latestInfo = entries.length ? entries[entries.length - 1] : null;
      const latestMessage = latestInfo?.message || '';

      if (session.watch.status === 'error') {
        return 'watch error';
      }

      if (session.watch.status === 'change-detected') {
        return 'watch change detected';
      }

      if (session.watch.status === 'reload-pending') {
        return 'watch reload pending';
      }

      if (session.watch.status === 'reloading') {
        return latestMessage.startsWith('Watch build started')
          ? 'watch building'
          : 'watch reloading';
      }

      if (latestInfo) {
        const ageMs = Date.now() - new Date(latestInfo.timestampUtc).getTime();
        if (ageMs <= 15000) {
          if (latestMessage.startsWith('Watch build completed')) {
            return 'watch build completed';
          }

          if (latestMessage.startsWith('Watch reload completed')) {
            return 'watch reload completed';
          }

          if (latestMessage.startsWith('Watch detected')) {
            return 'watch change detected';
          }
        }
      }

      return session.watch.isEnabled ? 'watching' : 'idle';
    }

    async function refresh() {
      const currentEpoch = ++refreshEpoch;
      const [session, entries] = await Promise.all([
        api('/api/session'),
        api('/api/logs')
      ]);

      if (suppressRefreshRender || currentEpoch !== refreshEpoch) {
        return;
      }

      renderSession(session);
      renderLogs(entries);
      logStatus.textContent = deriveStatusLabel(session, entries);
    }

    async function perform(label, action) {
      suppressRefreshRender = true;
      refreshEpoch += 1;
      logStatus.textContent = label;
      try {
        await action();
      } catch (error) {
        alert(error.message);
      } finally {
        suppressRefreshRender = false;
        logStatus.textContent = 'idle';
        await refresh();
      }
    }

    async function switchProfile(name) {
      if (!name || profileSwitchInFlight || name === lastSessionProfileName) {
        pendingProfileName = null;
        return;
      }

      pendingProfileName = name;
      profileSwitchInFlight = true;
      try {
        await perform('switching profile', () => api('/api/profiles/select', {
          method: 'POST',
          body: JSON.stringify({ name })
        }));
        pendingProfileName = null;
      } finally {
        profileSwitchInFlight = false;
      }
    }

    profileSelect.addEventListener('change', () => {
      pendingProfileName = profileSelect.value;
      return switchProfile(profileSelect.value);
    });

    document.getElementById('profile-switch').addEventListener('click', () => switchProfile(profileSelect.value));

    document.getElementById('build-btn').addEventListener('click', () => perform('building', () => api('/api/build', { method: 'POST' })));
    document.getElementById('pack-btn').addEventListener('click', () => perform('packing', () => api('/api/pack', { method: 'POST' })));
    document.getElementById('open-pack-dir-btn').addEventListener('click', () => perform('opening pack directory', () => api('/api/pack/open-directory', { method: 'POST' })));
    document.getElementById('reload-btn').addEventListener('click', () => perform('reloading', () => api('/api/reload', { method: 'POST' })));
    document.getElementById('watch-start-btn').addEventListener('click', () => perform('starting watch', () => api('/api/watch/start', { method: 'POST' })));
    document.getElementById('watch-stop-btn').addEventListener('click', () => perform('stopping watch', () => api('/api/watch/stop', { method: 'POST' })));
    document.getElementById('clear-logs-btn').addEventListener('click', () => perform('clearing console', () => api('/api/logs/clear', { method: 'POST' })));
    consoleRunButton.addEventListener('click', () => runConsoleCommand());
    consoleInput.addEventListener('input', () => {
      resetConsoleHistoryCursor();
      updateConsoleHint();
      showConsoleSuggestions();
    });
    consoleInput.addEventListener('focus', () => {
      showConsoleSuggestions();
    });
    consoleInput.addEventListener('blur', () => {
      if (!consoleAutocompletePinned) {
        hideConsoleSuggestions();
      }
    });
    consoleInput.addEventListener('keydown', event => {
      if (event.key === 'Enter') {
        event.preventDefault();
        runConsoleCommand();
        return;
      }

      if (event.key === 'ArrowUp') {
        event.preventDefault();
        hideConsoleSuggestions();
        moveConsoleHistory(-1);
        return;
      }

      if (event.key === 'ArrowDown') {
        event.preventDefault();
        hideConsoleSuggestions();
        moveConsoleHistory(1);
      }
    });
    consoleAutocomplete.addEventListener('mouseenter', () => {
      consoleAutocompletePinned = true;
    });
    consoleAutocomplete.addEventListener('mouseleave', () => {
      consoleAutocompletePinned = false;
      hideConsoleSuggestions();
    });
    diagnosticsLevel.addEventListener('change', async () => {
      diagnosticsLevelInFlight = true;
      try {
        await perform('saving diagnostics filter', () => api('/api/ui/diagnostics-level', {
          method: 'POST',
          body: JSON.stringify({ diagnosticsLevel: diagnosticsLevel.value })
        }));
      } finally {
        diagnosticsLevelInFlight = false;
      }
    });
    scenarioSelect.addEventListener('change', () => {
      lastScenarioName = scenarioSelect.value;
      scenarioQuery.dataset.scenario = '';
      refresh();
    });
    document.getElementById('scenario-btn').addEventListener('click', () => perform('running scenario', () => api('/api/scenarios/run', {
      method: 'POST',
      body: JSON.stringify({ name: scenarioSelect.value, query: scenarioQuery.value })
    })));

    buildConsoleSuggestions();
    updateConsoleHint();
    refresh();
    setInterval(refresh, 2500);
  </script>
</body>
</html>
""";
}
using EMMA.Cli.Commands;
using EMMA.Domain;

namespace EMMA.Cli;

/// <summary>The command line: parses, dispatches and turns every outcome into an exit code (0 done, 1 failed, 2 wrong usage).</summary>
internal static class CliApp
{
    private const string Usage = @"emma - manage and develop EMMA plugins

Usage:
  emma plugin list                          List installed plugins
  emma plugin info <id>                     Show a plugin and what it declares
  emma plugin install <package.zip> --repository <id>
                                            Install a package from a trusted repository
  emma plugin enable <id> | disable <id> | remove <id>
  emma plugin settings <id>                 Show a plugin's settings and what each is set to
  emma plugin settings <id> set <key> <value>...
                                            Change a setting (several values only for a multi-choice setting)
  emma plugin settings <id> clear <key>     Go back to the setting's default
  emma plugin settings <id> set-secret <key>
                                            Set a secret setting from the first line of standard input
  emma plugin new <dir> --id <plugin id> [--name <name>]
                                            Scaffold a Rust plugin
  emma plugin pack <dir> [--out <dir|file>] [--sign-key <pem> --key-id <id> --repository <id> [--valid-days <n>]]
                                            Package (and optionally sign) a plugin directory
  emma plugin run <dir|package.zip> <describe|search|children|get|assets|content> [args] [--settings <file.json>]
                                            Try a plugin in a throwaway host, reporting what EMMA would refuse
  emma playback plan <ref> [--hint <quality>]
                                            Show the streams, subtitles, audio tracks and chapters of a node
  emma playback serve <ref> [--role <role>] [--index <n>] [--hint <quality>]
                                            Serve a node's media on a local address (try it with: mpv <address>)
  emma downloads list                       List downloads and how far along they are
  emma downloads add <ref> --role <role> [--hint <quality>]
                                            Queue a download of a node (and everything beneath it)
  emma downloads pause <id> | resume <id> | retry <id> | cancel <id> | delete <id>
  emma downloads clear <state>...           Delete the downloads in these states (queued, running, paused,
                                            completed, failed, canceled)
  emma downloads retry-failed               Queue every failed download again
  emma downloads parallel [<n>]             Show or set how many downloads run at once (1 to 8)
  emma downloads run                        Work through the queue now, then exit (an app or server does this itself)

Options:
  --data-dir <dir>   Where EMMA keeps its state (default: the same place the HTTP API uses)
  --json             Print results as JSON (the shapes the HTTP API uses)
  --help             Show this help

Trust settings (which repositories may sign plugins) are read from <data-dir>/config.json.
Plugins installed here are served by the HTTP API after it restarts, and by an app that embeds the native
library after it calls plugins.reload.";

    /// <summary>Runs the command line.</summary>
    /// <param name="args">The arguments.</param>
    /// <param name="output">Standard output.</param>
    /// <param name="error">Standard error.</param>
    /// <param name="cancellationToken">Fires when the user interrupts.</param>
    /// <param name="input">Standard input, read only by commands that take a secret from it.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken, TextReader? input = null)
    {
        Arguments? parsed = Arguments.Parse(args, out string? problem);
        if (parsed is null)
        {
            await error.WriteLineAsync($"error: {problem}\nRun 'emma --help' for usage.").ConfigureAwait(false);
            return CommandContext.Usage;
        }

        CommandContext context = new(parsed, output, error, cancellationToken) { Input = input ?? TextReader.Null };
        if (parsed.Help || parsed.Words.Count == 0)
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return parsed.Help ? CommandContext.Success : CommandContext.Usage;
        }

        try
        {
            return await DispatchAsync(context, [.. parsed.Words]).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Cancelled.").ConfigureAwait(false);
            return CommandContext.Failed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return CommandContext.Failed;
        }
    }

    private static Task<int> DispatchAsync(CommandContext context, string[] words) => words switch
    {
        ["plugin", "list"] => PluginCommands.ListAsync(context),
        ["plugin", "info", var id] => PluginCommands.InfoAsync(context, id),
        ["plugin", "install", var path] => PluginCommands.InstallAsync(context, path),
        ["plugin", "enable", var id] => PluginCommands.SetStatusAsync(context, id, PluginStatus.Enabled),
        ["plugin", "disable", var id] => PluginCommands.SetStatusAsync(context, id, PluginStatus.Disabled),
        ["plugin", "remove", var id] => PluginCommands.RemoveAsync(context, id),
        ["plugin", "settings", var id] => SettingsCommands.ShowAsync(context, id),
        ["plugin", "settings", var id, "set", var key, .. var values] when values.Length > 0 => SettingsCommands.SetAsync(context, id, key, values),
        ["plugin", "settings", var id, "clear", var key] => SettingsCommands.SetAsync(context, id, key, []),
        ["plugin", "settings", var id, "set-secret", var key] => SettingsCommands.SetSecretAsync(context, id, key),
        ["plugin", "new", var directory] => NewCommand.RunAsync(context, directory),
        ["plugin", "pack", var directory] => PackCommand.RunAsync(context, directory),
        ["plugin", "run", var source, var operation, .. var operands] => RunCommand.RunAsync(context, source, operation, operands),
        ["playback", "plan", var reference] => PlaybackCommands.PlanAsync(context, reference),
        ["playback", "serve", var reference] => PlaybackCommands.ServeAsync(context, reference),
        ["downloads", "list"] => DownloadCommands.ListAsync(context),
        ["downloads", "add", var reference] => DownloadCommands.AddAsync(context, reference),
        ["downloads", "pause", var id] => DownloadCommands.ChangeAsync(context, id, (downloads, job, token) => downloads.PauseAsync(job, token)),
        ["downloads", "resume", var id] => DownloadCommands.ChangeAsync(context, id, (downloads, job, token) => downloads.ResumeAsync(job, token)),
        ["downloads", "retry", var id] => DownloadCommands.ChangeAsync(context, id, (downloads, job, token) => downloads.RetryAsync(job, token)),
        ["downloads", "cancel", var id] => DownloadCommands.ChangeAsync(context, id, (downloads, job, token) => downloads.CancelAsync(job, token)),
        ["downloads", "delete", var id] => DownloadCommands.DeleteAsync(context, id),
        ["downloads", "clear", .. var states] when states.Length > 0 => DownloadCommands.ClearAsync(context, states),
        ["downloads", "retry-failed"] => DownloadCommands.RetryFailedAsync(context),
        ["downloads", "parallel"] => DownloadCommands.ParallelAsync(context, null),
        ["downloads", "parallel", var count] => DownloadCommands.ParallelAsync(context, count),
        ["downloads", "run"] => DownloadCommands.RunAsync(context),
        _ => context.MisuseAsync($"Unknown command '{string.Join(' ', words)}'."),
    };
}

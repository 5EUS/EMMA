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
  emma plugin new <dir> --id <plugin id> [--name <name>]
                                            Scaffold a Rust plugin
  emma plugin pack <dir> [--out <dir|file>] [--sign-key <pem> --key-id <id> --repository <id> [--valid-days <n>]]
                                            Package (and optionally sign) a plugin directory
  emma plugin run <dir|package.zip> <describe|search|children|get|assets|content> [args]
                                            Try a plugin in a throwaway host, reporting what EMMA would refuse

Options:
  --data-dir <dir>   Where EMMA keeps its state (default: the same place the HTTP API uses)
  --json             Print results as JSON (the shapes the HTTP API uses)
  --help             Show this help

Trust settings (which repositories may sign plugins) are read from <data-dir>/config.json.
Plugins installed here are served by the HTTP API and the native library after they restart.";

    /// <summary>Runs the command line.</summary>
    /// <param name="args">The arguments.</param>
    /// <param name="output">Standard output.</param>
    /// <param name="error">Standard error.</param>
    /// <param name="cancellationToken">Fires when the user interrupts.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        Arguments? parsed = Arguments.Parse(args, out string? problem);
        if (parsed is null)
        {
            await error.WriteLineAsync($"error: {problem}\nRun 'emma --help' for usage.").ConfigureAwait(false);
            return CommandContext.Usage;
        }

        CommandContext context = new(parsed, output, error, cancellationToken);
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
        ["plugin", "new", var directory] => NewCommand.RunAsync(context, directory),
        ["plugin", "pack", var directory] => PackCommand.RunAsync(context, directory),
        ["plugin", "run", var source, var operation, .. var operands] => RunCommand.RunAsync(context, source, operation, operands),
        _ => context.MisuseAsync($"Unknown command '{string.Join(' ', words)}'."),
    };
}

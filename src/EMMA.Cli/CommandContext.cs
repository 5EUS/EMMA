using System.Text;

using EMMA.Api.Contracts;
using EMMA.Domain;
using EMMA.Runtime;

namespace EMMA.Cli;

/// <summary>What a command can use: its arguments, where to write, and how to open EMMA.</summary>
/// <param name="Arguments">The parsed command line.</param>
/// <param name="Output">Standard output.</param>
/// <param name="Error">Standard error.</param>
/// <param name="CancellationToken">Fires when the user interrupts.</param>
internal sealed record CommandContext(Arguments Arguments, TextWriter Output, TextWriter Error, CancellationToken CancellationToken)
{
    /// <summary>The exit code for a command that did what was asked.</summary>
    public const int Success = 0;

    /// <summary>The exit code for a command that ran and could not do what was asked.</summary>
    public const int Failed = 1;

    /// <summary>The exit code for a command line that is wrong.</summary>
    public const int Usage = 2;

    /// <summary>Gets standard input, from which a command reads what must not appear on a command line, such as a secret.</summary>
    public TextReader Input { get; init; } = TextReader.Null;

    /// <summary>Gets the directory EMMA keeps its state in: <c>--data-dir</c>, else the same default the HTTP API uses, so a plugin installed here is the one the API serves.</summary>
    public string DataDirectory
    {
        get
        {
            if (this.Arguments.Option("data-dir") is { } given)
            {
                return Path.GetFullPath(given);
            }

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local.Length == 0 ? Path.GetTempPath() : local, "emma");
        }
    }

    /// <summary>Opens EMMA over the data directory, reading trust settings from <c>config.json</c> there when it exists.</summary>
    /// <returns>The initialized runtime, or why it could not be opened. The caller disposes it.</returns>
    public async Task<Outcome<EmmaRuntime>> OpenAsync()
    {
        string configPath = Path.Combine(this.DataDirectory, "config.json");
        byte[] document = File.Exists(configPath) ? await File.ReadAllBytesAsync(configPath, this.CancellationToken).ConfigureAwait(false) : Encoding.UTF8.GetBytes("{}");
        Outcome<RuntimeConfig> config = RuntimeConfigReader.Parse(document, this.DataDirectory);
        if (!config.IsSuccess)
        {
            return Outcome.Failure<EmmaRuntime>(config.Error.Code, $"{configPath}: {config.Error.Message}");
        }

        // The caller owns the runtime inside the outcome and disposes it; the disposal analyzer cannot follow that.
#pragma warning disable CA2000
        EmmaRuntime runtime = new(config.Value.ToOptions());
#pragma warning restore CA2000
        await runtime.InitializeAsync(this.CancellationToken).ConfigureAwait(false);
        return Outcome.Success(runtime);
    }

    /// <summary>Writes a line to standard output.</summary>
    /// <param name="line">The text.</param>
    /// <returns>A task that completes when it is written.</returns>
    public Task SayAsync(string line) => this.Output.WriteLineAsync(line.AsMemory(), this.CancellationToken);

    /// <summary>Reports a failure and returns the exit code for it.</summary>
    /// <param name="error">The failure.</param>
    /// <returns><see cref="Failed"/>.</returns>
    public async Task<int> FailAsync(PluginError error)
    {
        await this.Error.WriteLineAsync($"error: {error.Message} ({ApiErrors.Describe(error.Code).Code})".AsMemory(), this.CancellationToken).ConfigureAwait(false);
        return Failed;
    }

    /// <summary>Reports a failure and returns the exit code for it.</summary>
    /// <param name="message">What went wrong.</param>
    /// <returns><see cref="Failed"/>.</returns>
    public async Task<int> FailAsync(string message)
    {
        await this.Error.WriteLineAsync($"error: {message}".AsMemory(), this.CancellationToken).ConfigureAwait(false);
        return Failed;
    }

    /// <summary>Reports a wrong command line and returns the exit code for it.</summary>
    /// <param name="message">What is wrong.</param>
    /// <returns><see cref="Usage"/>.</returns>
    public async Task<int> MisuseAsync(string message)
    {
        await this.Error.WriteLineAsync($"error: {message}\nRun 'emma --help' for usage.".AsMemory(), this.CancellationToken).ConfigureAwait(false);
        return Usage;
    }
}

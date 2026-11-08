using EMMA.Cli;

using CancellationTokenSource interrupt = new();
Task cancelled = Task.CompletedTask;
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancelled = interrupt.CancelAsync();
};

int exitCode = await CliApp.RunAsync(args, Console.Out, Console.Error, interrupt.Token, Console.In).ConfigureAwait(false);
await cancelled.ConfigureAwait(false);
return exitCode;

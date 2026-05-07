using EMMA.Cli;
using ConsoleAppFramework;

try
{
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EMMA_PLUGIN_DEV_MODE")))
{
    Environment.SetEnvironmentVariable("EMMA_PLUGIN_DEV_MODE", "1");
}

PluginDevCliRuntimeContext.IsInteractive = args.Length == 0;

var sessionFactory = new PluginDevSessionFactory();
var session = sessionFactory.Create(Environment.CurrentDirectory);
session.TransitionTo(PluginDevSessionState.Starting);
PluginDevSessionHolder.SetCurrent(session);
var pluginApplication = new PluginDevApplication(sessionFactory, Environment.CurrentDirectory, session);
PluginDevApplicationHolder.SetCurrent(pluginApplication);

if (args.Length == 0)
{
    if (session.Ui.StartWatchByDefault)
    {
        try
        {
            pluginApplication.StartWatch();
        }
        catch (Exception ex)
        {
            pluginApplication.RecordError($"Default watch startup failed: {ex.Message}");
        }
    }

    if (session.Ui.StartServeByDefault)
    {
        try
        {
            Console.WriteLine(PluginDevLocalServer.StartInBackground(pluginApplication, 5075));
        }
        catch (Exception ex)
        {
            pluginApplication.RecordError($"Default serve startup failed: {ex.Message}");
        }
    }
}

var app = ConsoleApp.Create();
app.Add<MyCommands>();

var selector = new ResultSelector();

void ExecuteCommand(string[] commandArgs)
{
    var context = CommandContextHolder.Context;
    context.Clear();

    session.TransitionTo(PluginDevSessionState.Running);
    app.Run(commandArgs);

    if (context.Results.Count <= 0)
    {
        return;
    }

    while (context.Results.Count > 0)
    {
        var toDisplay = context.Results.ToList();
        context.Clear();
        selector.Display(toDisplay).Wait();
    }
}

if (args.Length != 0)
{
    ExecuteCommand(args);
    session.TransitionTo(PluginDevSessionState.Stopped);
    return;
}

var loop = new CommandLoop(commandArgs =>
{
    ExecuteCommand(commandArgs);
});

loop.Run().Wait();
session.TransitionTo(PluginDevSessionState.Stopped);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"EMMA CLI startup failed: {ex.Message}");
    Environment.ExitCode = 1;
}

using EMMA.Cli;
using ConsoleAppFramework;

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

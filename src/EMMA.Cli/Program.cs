using EMMA.Cli;
using ConsoleAppFramework;

var app = ConsoleApp.Create();
app.Add<MyCommands>();

var selector = new ResultSelector();

if (args.Length != 0)
{
    app.Run(args);
    var context = CommandContextHolder.Context;
    if (context.Results.Count > 0)
    {
        // Display results batch-by-batch. Clear the context before showing
        // each batch so actions can add new results that will be shown
        // in subsequent iterations.
        while (context.Results.Count > 0)
        {
            var toDisplay = context.Results.ToList();
            context.Clear();
            selector.Display(toDisplay).Wait();
        }
    }
    return;
}

var loop = new CommandLoop(args =>
{
    var context = CommandContextHolder.Context;
    context.Clear();

    app.Run(args);

    if (context.Results.Count > 0)
    {
        while (context.Results.Count > 0)
        {
            var toDisplay = context.Results.ToList();
            context.Clear();
            selector.Display(toDisplay).Wait();
        }
    }
});

loop.Run().Wait();

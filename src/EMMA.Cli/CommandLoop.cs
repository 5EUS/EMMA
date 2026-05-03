namespace EMMA.Cli;

public class CommandLoop(Action<string[]> executor)
{
    private readonly CommandHistory _history = new();
    private readonly LineEditor _editor = new();
    private readonly Action<string[]> _executor = executor;

    public async Task Run()
    {
        while (true)
        {
            var input = _editor.ReadLine(_history);

            if (input == "exit")
                break;

            if (string.IsNullOrWhiteSpace(input))
                continue;
    
            _history.Add(input);

            var args = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            _executor(args);

            // 🔥 pause so output is visible before next prompt
            Console.WriteLine();
        }
    }
}
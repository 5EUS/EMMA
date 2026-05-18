namespace EMMA.Cli;

public interface ISelectableResult
{
    string Label { get; }
    List<ResultAction> Actions { get; }
}

public class ResultAction
{
    public string Name { get; set; } = "";
    public Func<Task> Execute { get; set; } = () => Task.CompletedTask;
}

public class SimpleResult(string label, List<ResultAction> actions) : ISelectableResult
{
    public string Label { get; set; } = label;
    public List<ResultAction> Actions { get; set; } = actions;
}
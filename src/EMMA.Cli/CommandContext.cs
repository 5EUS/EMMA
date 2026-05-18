namespace EMMA.Cli;

public class CommandContext
{
    public List<ISelectableResult> Results { get; } = new();

    public void AddResult(ISelectableResult result)
    {
        Results.Add(result);
    }

    public void Clear() => Results.Clear();
}

public static class CommandContextHolder
{
    public static CommandContext Context { get; set; } = new();
}
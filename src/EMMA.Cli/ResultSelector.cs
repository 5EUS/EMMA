namespace EMMA.Cli;

public class ResultSelector
{
    public async Task Display(IEnumerable<ISelectableResult> results)
    {
        var list = results.ToList();

        for (int i = 0; i < list.Count; i++)
        {
            var result = list[i];
            Console.WriteLine($"[{i}] {result.Label}");

            for (int j = 0; j < result.Actions.Count; j++)
            {
                Console.WriteLine($"    ({j}) {result.Actions[j].Name}");
            }
        }

        Console.Write("Select (result.action): ");
        var input = Console.ReadLine();

        await HandleSelection(input, list);
    }

    private async Task HandleSelection(string? input, List<ISelectableResult> list)
    {
        if (string.IsNullOrWhiteSpace(input)) return;

        var parts = input.Split('.');
        if (parts.Length != 2) return;

        if (int.TryParse(parts[0], out int resultIndex) &&
            int.TryParse(parts[1], out int actionIndex))
        {
            if (resultIndex < list.Count &&
                actionIndex < list[resultIndex].Actions.Count)
            {
                await list[resultIndex].Actions[actionIndex].Execute();
            }
        }
    }
}
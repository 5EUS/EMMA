namespace EMMA.Cli;

/// <summary>
/// The command line split into words and options. Options are <c>--name value</c>, <c>--name=value</c> or, for the few
/// switches, <c>--name</c>. A name that is not known is an error rather than a guess.
/// </summary>
internal sealed class Arguments
{
    private static readonly HashSet<string> Switches = new(StringComparer.Ordinal) { "json", "help" };

    private static readonly HashSet<string> Valued = new(StringComparer.Ordinal)
    {
        "data-dir", "repository", "out", "sign-key", "key-id", "valid-days", "id", "name", "index", "cursor", "limit", "settings", "role", "hint",
    };

    private readonly Dictionary<string, string> options = new(StringComparer.Ordinal);

    private Arguments(IReadOnlyList<string> words) => this.Words = words;

    /// <summary>Gets the words that are not options, in order.</summary>
    public IReadOnlyList<string> Words { get; }

    /// <summary>Gets a value indicating whether <c>--json</c> was given.</summary>
    public bool Json => this.options.ContainsKey("json");

    /// <summary>Gets a value indicating whether <c>--help</c> was given.</summary>
    public bool Help => this.options.ContainsKey("help");

    /// <summary>Splits a command line.</summary>
    /// <param name="args">The arguments.</param>
    /// <param name="error">Why the command line is wrong, or <see langword="null"/>.</param>
    /// <returns>The parsed arguments, or <see langword="null"/> when <paramref name="error"/> is set.</returns>
    public static Arguments? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        List<string> words = [];
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        int index = 0;
        while (index < args.Count)
        {
            string argument = args[index++];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                words.Add(argument);
                continue;
            }

            string name = argument[2..];
            string? inline = null;
            int equals = name.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                inline = name[(equals + 1)..];
                name = name[..equals];
            }

            if (Switches.Contains(name))
            {
                found[name] = string.Empty;
            }
            else if (Valued.Contains(name))
            {
                if (inline is null && index >= args.Count)
                {
                    error = $"--{name} needs a value.";
                    return null;
                }

                found[name] = inline ?? args[index++];
            }
            else
            {
                error = $"Unknown option --{name}.";
                return null;
            }
        }

        Arguments result = new(words);
        foreach ((string key, string value) in found)
        {
            result.options[key] = value;
        }

        return result;
    }

    /// <summary>Gets an option's value.</summary>
    /// <param name="name">The option name without dashes.</param>
    /// <returns>The value, or <see langword="null"/> when the option was not given.</returns>
    public string? Option(string name) => this.options.GetValueOrDefault(name);
}

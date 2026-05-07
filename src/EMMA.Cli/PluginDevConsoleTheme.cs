namespace EMMA.Cli;

internal static class PluginDevConsoleTheme
{
    private const string AnsiEscapePrefix = "\u001b[";

    public static void WriteDiagnostic(string indent, PluginDevDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        if (!SupportsColor())
        {
            Console.WriteLine($"{indent}[{NormalizeSeverity(diagnostic.Severity)}/{NormalizeType(diagnostic.Type)}] {diagnostic.Code}: {diagnostic.Message}");
            return;
        }

        Console.Write(indent);
        Console.Write("[");
        WriteColored(NormalizeSeverity(diagnostic.Severity), GetSeverityColor(diagnostic.Severity));
        WriteColored("/", ConsoleColor.DarkGray);
        WriteColored(NormalizeType(diagnostic.Type), ConsoleColor.DarkGray);
        Console.Write("] ");
        WriteColored(diagnostic.Code, ConsoleColor.Gray);
        Console.Write(": ");
        WriteColoredLine(diagnostic.Message, GetSeverityColor(diagnostic.Severity));
    }

    public static void WriteLogEntry(PluginDevLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var severity = NormalizeSeverity(entry.Level);
        if (!SupportsColor())
        {
            Console.WriteLine($"[{severity}] {entry.TimestampUtc:HH:mm:ss} {entry.Message}");
            return;
        }

        Console.Write("[");
        WriteColored(severity, GetSeverityColor(entry.Level));
        Console.Write("] ");
        WriteColored(entry.TimestampUtc.ToString("HH:mm:ss"), ConsoleColor.DarkGray);
        Console.Write(" ");
        if (ContainsAnsiEscape(entry.Message))
        {
            Console.WriteLine(entry.Message);
            return;
        }

        WriteColoredLine(entry.Message, GetSeverityColor(entry.Level));
    }

    private static bool SupportsColor()
        => !Console.IsOutputRedirected;

    private static string NormalizeSeverity(string? severity)
        => severity?.Trim().ToLowerInvariant() switch
        {
            PluginDevDiagnosticSeverity.Warning => PluginDevDiagnosticSeverity.Warning,
            PluginDevDiagnosticSeverity.Error => PluginDevDiagnosticSeverity.Error,
            _ => PluginDevDiagnosticSeverity.Info
        };

    private static string NormalizeType(string? type)
        => string.IsNullOrWhiteSpace(type) ? "general" : type.Trim().ToLowerInvariant();

    private static ConsoleColor GetSeverityColor(string? severity)
        => NormalizeSeverity(severity) switch
        {
            PluginDevDiagnosticSeverity.Warning => ConsoleColor.Yellow,
            PluginDevDiagnosticSeverity.Error => ConsoleColor.Red,
            _ => ConsoleColor.Cyan
        };

    private static void WriteColored(string value, ConsoleColor color)
    {
        var original = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(value);
        Console.ForegroundColor = original;
    }

    private static void WriteColoredLine(string value, ConsoleColor color)
    {
        var original = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(value);
        Console.ForegroundColor = original;
    }

    private static bool ContainsAnsiEscape(string? value)
        => !string.IsNullOrEmpty(value) && value.Contains(AnsiEscapePrefix, StringComparison.Ordinal);
}
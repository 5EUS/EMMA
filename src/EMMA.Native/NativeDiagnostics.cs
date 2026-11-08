namespace EMMA.Native;

/// <summary>
/// Reports failures that should never happen. The library cannot throw into its caller, so an unexpected exception
/// becomes a generic error envelope; the details go to standard error, where an embedding app's log already is.
/// </summary>
internal static class NativeDiagnostics
{
    /// <summary>Reports an unexpected failure.</summary>
    /// <param name="failure">The exception.</param>
    public static void Report(Exception failure) =>
        Console.Error.WriteLine($"emma_native: unexpected failure: {failure}");
}

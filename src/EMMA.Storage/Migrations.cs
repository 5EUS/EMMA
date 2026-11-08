namespace EMMA.Storage;

/// <summary>
/// The schema, in order. Each migration is a script under <c>Sql/</c>, embedded in the
/// assembly under a fixed name and registered here by number, so the order is explicit
/// (never inferred from file names) and nothing is discovered by reflection. Never edit a
/// released script: add the next number.
/// </summary>
/// <remarks>
/// Tables are STRICT (the engine rejects a value of the wrong type), identity columns are
/// the same (plugin id, local id) pair that <c>NodeRef</c> uses, and timestamps are integer
/// Unix milliseconds in UTC so they sort and index without formatting. There is no foreign
/// key from user state to <c>nodes</c>: nodes are a cache of what plugins said and may be
/// purged, while progress and library entries must survive that.
/// </remarks>
internal static class Migrations
{
    private const string ResourcePrefix = "EMMA.Storage.Sql.";

    private static readonly (int Version, string Name, string File)[] Scripts =
    [
        (1, "initial", "001_initial.sql"),
    ];

    /// <summary>Gets the highest registered version.</summary>
    public static int LatestVersion => Scripts[^1].Version;

    /// <summary>Gets the file names of the registered scripts, in version order.</summary>
    public static IReadOnlyList<string> RegisteredScriptNames { get; } = [.. Scripts.Select(script => script.File)];

    /// <summary>Gets the names of every embedded script, so a test can prove none is left unregistered.</summary>
    /// <returns>The file names of the embedded <c>.sql</c> scripts.</returns>
    public static IReadOnlyList<string> EmbeddedScriptNames() =>
    [
        .. typeof(Migrations).Assembly
            .GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.Ordinal))
            .Select(name => name[ResourcePrefix.Length..])
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>Reads every registered script from the assembly.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The migrations in version order.</returns>
    /// <exception cref="InvalidOperationException">A registered script is not embedded.</exception>
    public static async Task<IReadOnlyList<Migration>> LoadAsync(CancellationToken cancellationToken)
    {
        List<Migration> migrations = [];
        foreach ((int version, string name, string file) in Scripts)
        {
            Stream stream = typeof(Migrations).Assembly.GetManifestResourceStream(ResourcePrefix + file)
                ?? throw new InvalidOperationException($"The migration script '{file}' is not embedded in the assembly.");
            await using (stream.ConfigureAwait(false))
            {
                using StreamReader reader = new(stream);
                migrations.Add(new Migration(version, name, await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)));
            }
        }

        return migrations;
    }
}

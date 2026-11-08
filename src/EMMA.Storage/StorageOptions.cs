namespace EMMA.Storage;

/// <summary>Where the database lives. The host resolves configuration and passes it in; storage reads no environment.</summary>
/// <param name="DatabasePath">The path of the SQLite database file. Its directory is created on initialization.</param>
public sealed record StorageOptions(string DatabasePath);

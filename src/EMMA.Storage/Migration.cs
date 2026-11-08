namespace EMMA.Storage;

/// <summary>One schema change. Versions are applied in ascending order, each exactly once, and never edited after release.</summary>
/// <param name="Version">The version number; strictly increasing, starting at 1.</param>
/// <param name="Name">A short description, recorded with the version.</param>
/// <param name="Sql">The SQL script to run.</param>
internal sealed record Migration(int Version, string Name, string Sql);

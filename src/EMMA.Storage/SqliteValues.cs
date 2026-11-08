using Microsoft.Data.Sqlite;

namespace EMMA.Storage;

/// <summary>Conversions between domain values and what the database stores.</summary>
internal static class SqliteValues
{
    /// <summary>Converts a timestamp to integer Unix milliseconds, UTC.</summary>
    /// <param name="value">The timestamp.</param>
    /// <returns>Milliseconds since the Unix epoch.</returns>
    public static long ToStored(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    /// <summary>Converts an optional timestamp.</summary>
    /// <param name="value">The timestamp, or <see langword="null"/>.</param>
    /// <returns>Milliseconds since the Unix epoch, or <see cref="DBNull"/>.</returns>
    public static object ToStored(DateTimeOffset? value) => value is { } present ? present.ToUnixTimeMilliseconds() : DBNull.Value;

    /// <summary>Converts stored Unix milliseconds back to a UTC timestamp.</summary>
    /// <param name="value">Milliseconds since the Unix epoch.</param>
    /// <returns>The timestamp.</returns>
    public static DateTimeOffset FromStored(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    /// <summary>Adds a parameter, mapping <see langword="null"/> to a database null.</summary>
    /// <param name="parameters">The parameter collection.</param>
    /// <param name="name">The parameter name, including its <c>$</c> prefix.</param>
    /// <param name="value">The value, or <see langword="null"/>.</param>
    public static void Bind(this SqliteParameterCollection parameters, string name, object? value) =>
        parameters.AddWithValue(name, value ?? DBNull.Value);

    /// <summary>Reads a nullable text column.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="ordinal">The column index.</param>
    /// <returns>The text, or <see langword="null"/>.</returns>
    public static string? GetNullableString(this SqliteDataReader reader, int ordinal) =>
        reader.GetValue(ordinal) is DBNull ? null : reader.GetString(ordinal);

    /// <summary>Reads a nullable real column.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="ordinal">The column index.</param>
    /// <returns>The number, or <see langword="null"/>.</returns>
    public static double? GetNullableDouble(this SqliteDataReader reader, int ordinal) =>
        reader.GetValue(ordinal) is DBNull ? null : reader.GetDouble(ordinal);

    /// <summary>Reads a nullable timestamp column.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="ordinal">The column index.</param>
    /// <returns>The timestamp, or <see langword="null"/>.</returns>
    public static DateTimeOffset? GetNullableTimestamp(this SqliteDataReader reader, int ordinal) =>
        reader.GetValue(ordinal) is DBNull ? null : FromStored(reader.GetInt64(ordinal));
}

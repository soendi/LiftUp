using System.Globalization;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Data;

/// <summary>Kleine Helfer für robustes Lesen von SQLite-Werten.</summary>
internal static class Db
{
    public static string Text(this SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? string.Empty : reader.GetString(index);

    public static string? TextOrNull(this SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    public static long Int64(this SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? 0 : reader.GetInt64(index);

    public static int Int32(this SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? 0 : (int)reader.GetInt64(index);

    public static bool Bool(this SqliteDataReader reader, int index) =>
        !reader.IsDBNull(index) && reader.GetInt64(index) != 0;

    public static Guid Guid(this SqliteDataReader reader, int index) =>
        System.Guid.Parse(reader.GetString(index));

    public static DateTime Date(this SqliteDataReader reader, int index) =>
        ParseDate(reader.GetString(index));

    public static DateTime? DateOrNull(this SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : ParseDate(reader.GetString(index));

    public static string ToDb(this DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static object ToDb(this DateTime? value) =>
        value.HasValue ? value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value;

    private static DateTime ParseDate(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    public static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    public static SqliteCommand With(this SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Deduplicator.Core.Data;

/// <summary>Вспомогательные методы чтения значений из SQLite и работы с датами.</summary>
internal static class Db
{
    public static string IsoNow() => DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
    public static string Iso(DateTimeOffset dt) => dt.ToString("o", CultureInfo.InvariantCulture);
    public static string? IsoOrNull(DateTimeOffset? dt) => dt?.ToString("o", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseDate(string? s) =>
        string.IsNullOrEmpty(s)
            ? default
            : DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static DateTimeOffset? ParseDateOrNull(string? s) =>
        string.IsNullOrEmpty(s) ? null : ParseDate(s);

    public static string? Str(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static long Long(this SqliteDataReader r, int i) => r.IsDBNull(i) ? 0L : r.GetInt64(i);
    public static long? LongN(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    public static double Dbl(this SqliteDataReader r, int i) => r.IsDBNull(i) ? 0d : r.GetDouble(i);
    public static double? DblN(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    public static bool Bool(this SqliteDataReader r, int i) => !r.IsDBNull(i) && r.GetInt64(i) != 0;

    public static byte[]? Blob(this SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : (byte[])r.GetValue(i);

    public static void Add(this SqliteCommand cmd, string name, object? value) =>
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
}

using System.Globalization;

namespace Deduplicator.App.Infrastructure;

/// <summary>Форматирование величин для интерфейса.</summary>
public static class Format
{
    private static readonly string[] Units = { "Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ" };

    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "—";
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < Units.Length - 1) { size /= 1024; unit++; }
        return unit == 0
            ? $"{size:0} {Units[unit]}"
            : $"{size.ToString("0.##", CultureInfo.CurrentCulture)} {Units[unit]}";
    }

    public static string Percent(double fraction) =>
        $"{Math.Round(fraction * 100)}%";

    public static string DateShort(DateTimeOffset dt) =>
        dt == default ? "—" : dt.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);

    /// <summary>Русское склонение существительного по числу: one — «1 файл», few — «2 файла», many — «5 файлов».</summary>
    public static string Plural(long n, string one, string few, string many)
    {
        long a = Math.Abs(n) % 100;
        if (a is >= 11 and <= 14) return many;
        return (a % 10) switch
        {
            1 => one,
            >= 2 and <= 4 => few,
            _ => many
        };
    }

    /// <summary>«5 файлов» — число + правильно склонённое слово.</summary>
    public static string Count(long n, string one, string few, string many) =>
        $"{n:N0} {Plural(n, one, few, many)}";
}

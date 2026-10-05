namespace Deduplicator.App.Infrastructure;

/// <summary>Не показывает пользователю технические пути, SQL и внутренние сообщения исключений.</summary>
public static class UserFacingError
{
    public static string Describe(Exception exception, string fallback)
    {
        var error = Unwrap(exception);
        return error switch
        {
            UnauthorizedAccessException => "Недостаточно прав для этой папки. Выберите доступную папку или измените разрешения.",
            DriveNotFoundException => "Диск больше не подключён. Подключите его и повторите действие.",
            DirectoryNotFoundException => "Папка больше недоступна. Проверьте подключение диска.",
            FileNotFoundException => "Файл был перемещён или удалён другим приложением.",
            IOException => "Не удалось прочитать или изменить файл. Возможно, он занят другой программой или диск отключён.",
            _ when error.GetType().Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) =>
                "Не удалось обновить локальный каталог. Перезапустите приложение; файлы на диске не изменены.",
            _ => fallback
        };
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
            exception = aggregate.InnerExceptions[0];
        return exception;
    }
}

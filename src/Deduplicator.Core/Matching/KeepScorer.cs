using System.Text.RegularExpressions;

namespace Deduplicator.Core.Matching;

/// <summary>
/// Лёгкий слепок кандидата «какую копию оставить» — без привязки к БД и <c>FileRecord</c>,
/// чтобы скоринг можно было проверять чистыми юнит-тестами.
/// </summary>
public readonly record struct KeepCandidate(
    string RelPath,
    string Name,
    DateTimeOffset Created,
    DateTimeOffset Mtime,
    long Size,
    bool IsInReferenceFolder);

/// <summary>
/// Умный выбор эталона группы (этап 2.10): вместо одномерных правил (старейший/короткий путь) —
/// сумма эвристик «этот файл похож на оригинал, а не на разбросанную копию». СТРОГО предвыбор:
/// политика удаления (CanAutoSelect только exact, Confidence 1.0, RevalidateBeforeDelete)
/// этим НЕ расширяется — скоринг лишь решает, какую копию предложить ОСТАВИТЬ.
/// </summary>
public static class KeepScorer
{
    // --- Веса эвристик. Важен порядок величин, а не точные числа: ---
    //  • ReferenceBonus доминирует над всем: папка-эталон — явная воля пользователя, никакая
    //    комбинация штрафов (|JunkFolderPenalty| + |CopyNamePenalty| = 650 < 1000) её не перебивает;
    //  • |JunkFolderPenalty| > |CopyNamePenalty|: «лежит в Downloads/Temp» — более сильный сигнал
    //    «это скачанная/временная копия», чем суффикс в имени;
    //  • MeaningfulNameBonus умеренный: осмысленное имя не перевешивает мусорную папку;
    //  • CreatedBonusMax — мягкий сигнал (оригинал обычно старше): ломает близкие случаи,
    //    но не спорит с именами и папками.
    public const double ReferenceBonus = 1000;
    public const double JunkFolderPenalty = -400;
    public const double CopyNamePenalty = -250;
    public const double MeaningfulNameBonus = 60;
    public const double CreatedBonusMax = 25;

    // Причины выбора (короткий текст для тултипа бейджа «Эталон»; в БД не хранятся).
    public const string ReasonReference = "в папке-эталоне";
    public const string ReasonNotACopy = "не похоже на копию";
    public const string ReasonMeaningfulName = "осмысленное имя";
    public const string ReasonOldest = "старейший";
    public const string ReasonShortestPath = "кратчайший путь";

    // «Мусорные» сегменты пути: сравнение ПО СЕГМЕНТАМ и без учёта регистра, не подстрокой —
    // «Downloads» в «MyDownloadsArchive» не срабатывает. Последний сегмент (имя файла) не папка.
    private static readonly HashSet<string> JunkSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "Downloads", "Загрузки", "Temp", "Tmp", "Cache", "Кэш", "Корзина", ".trash"
    };

    // Имя «выглядит как копия» (проверяется имя БЕЗ расширения):
    //   « - копия», « — копия» (дефис/en/em dash, опционально с номером « (2)») — русский Проводник;
    //   « copy» / «_copy» / «-copy» (опционально « (2)») — английский Проводник и ручные копии;
    //   « (1)»…« (99)» — ТОЛЬКО цифры в скобках: «Глава (важная)» копией не считается;
    //   «_1»…«_99» — дубль-суффикс перед расширением («x_1.jpg»); «IMG_1234» не задевает
    //   (4 цифры после подчёркивания — это камерный номер, а не дубль-суффикс).
    // CultureInvariant + IgnoreCase: кириллица сравнивается предсказуемо, без сюрпризов локали.
    private static readonly Regex CopyNameRx = new(
        @"(?:\s[-—–]\s?копия(?:\s\(\d+\))?$)|(?:[\s_-]copy(?:\s\(\d+\))?$)|(?:\s\(\d{1,2}\)$)|(?:_\d{1,2}$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Камерные/автогенерированные имена: короткий буквенный префикс + длинный номер
    // (IMG_1234, DSC_5678, PXL_20230101). Неосмысленны независимо от длины.
    private static readonly Regex CameraNameRx = new(
        @"^\p{L}{1,4}[_-]?\d{3,}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Имя выглядит как копия («x - копия.jpg», «x (2).png», «x_1.jpg», «x copy.jpg»).</summary>
    public static bool LooksLikeCopyName(string name) =>
        CopyNameRx.IsMatch(Stem(name));

    /// <summary>
    /// Имя осмысленное («Отпуск 2024.jpg») против автогенерированного («IMG_1234.jpg», «фото (3).jpg»):
    /// ядро имени (без расширения и суффикса копии) длиннее 8 символов, не «цифры и разделители»
    /// и не камерный шаблон.
    /// </summary>
    public static bool IsMeaningfulName(string name)
    {
        var core = CopyNameRx.Replace(Stem(name), "");
        if (core.Length <= 8) return false;
        if (core.All(ch => char.IsDigit(ch) || ch is ' ' or '_' or '-' or '.' or '(' or ')')) return false;
        return !CameraNameRx.IsMatch(core);
    }

    /// <summary>Путь содержит «мусорный» сегмент-папку (Downloads/Загрузки/Temp/Cache/Корзина/.trash…).</summary>
    public static bool IsInJunkFolder(string relPath)
    {
        if (string.IsNullOrEmpty(relPath)) return false;
        var segments = relPath.Split('\\', '/');
        for (int i = 0; i < segments.Length - 1; i++) // последний сегмент — имя файла, не папка
            if (JunkSegments.Contains(segments[i])) return true;
        return false;
    }

    /// <summary>Вклад эвристик одного кандидата (без вклада Created — тот нормализуется по группе).</summary>
    public static double Score(in KeepCandidate c)
    {
        double score = 0;
        if (c.IsInReferenceFolder) score += ReferenceBonus;
        if (IsInJunkFolder(c.RelPath)) score += JunkFolderPenalty;
        if (LooksLikeCopyName(c.Name)) score += CopyNamePenalty;
        if (IsMeaningfulName(c.Name)) score += MeaningfulNameBonus;
        return score;
    }

    /// <summary>
    /// Счета всех кандидатов группы: <see cref="Score"/> + бонус за более раннюю Created,
    /// нормализованный по группе (самый ранний получает <see cref="CreatedBonusMax"/>, самый
    /// поздний — 0). Кандидаты с неизвестной Created (default) бонуса не получают и в
    /// нормализацию не входят.
    /// </summary>
    public static double[] ScoreGroup(IReadOnlyList<KeepCandidate> group)
    {
        var scores = new double[group.Count];
        for (int i = 0; i < group.Count; i++) scores[i] = Score(group[i]);

        long min = long.MaxValue, max = long.MinValue;
        foreach (var c in group)
        {
            if (c.Created == default) continue;
            long t = c.Created.UtcTicks;
            if (t < min) min = t;
            if (t > max) max = t;
        }
        if (min < max)
        {
            for (int i = 0; i < group.Count; i++)
            {
                if (group[i].Created == default) continue;
                double frac = (double)(max - group[i].Created.UtcTicks) / (max - min);
                scores[i] += CreatedBonusMax * frac;
            }
        }
        return scores;
    }

    /// <summary>
    /// Индекс лучшего кандидата «оставить». Детерминизм при равном счёте: старейший по Mtime →
    /// самый короткий RelPath → порядковое сравнение RelPath. Полная упорядоченность —
    /// результат не зависит от порядка кандидатов на входе.
    /// </summary>
    public static int PickIndex(IReadOnlyList<KeepCandidate> group)
    {
        if (group.Count == 0) throw new ArgumentException("Пустая группа", nameof(group));
        var scores = ScoreGroup(group);
        int best = 0;
        for (int i = 1; i < group.Count; i++)
            if (Better(group[i], scores[i], group[best], scores[best])) best = i;
        return best;
    }

    /// <summary>
    /// Короткая причина «почему выбран» для UI (тултип бейджа «Эталон»). Проверки — по силе
    /// сигнала; вычисляется на лету при построении карточки и в БД не хранится.
    /// </summary>
    public static string Explain(IReadOnlyList<KeepCandidate> group, int chosen)
    {
        var c = group[chosen];
        if (c.IsInReferenceFolder) return ReasonReference;

        bool chosenClean = !LooksLikeCopyName(c.Name) && !IsInJunkFolder(c.RelPath);
        bool othersDirty = false, othersMeaningless = false;
        long minMtime = long.MaxValue, minCreated = long.MaxValue;
        for (int i = 0; i < group.Count; i++)
        {
            minMtime = Math.Min(minMtime, group[i].Mtime.UtcTicks);
            if (group[i].Created != default) minCreated = Math.Min(minCreated, group[i].Created.UtcTicks);
            if (i == chosen) continue;
            if (LooksLikeCopyName(group[i].Name) || IsInJunkFolder(group[i].RelPath)) othersDirty = true;
            if (!IsMeaningfulName(group[i].Name)) othersMeaningless = true;
        }
        if (chosenClean && othersDirty) return ReasonNotACopy;
        if (IsMeaningfulName(c.Name) && othersMeaningless) return ReasonMeaningfulName;
        if (c.Mtime.UtcTicks == minMtime ||
            (c.Created != default && c.Created.UtcTicks == minCreated)) return ReasonOldest;
        return ReasonShortestPath;
    }

    private static bool Better(in KeepCandidate a, double sa, in KeepCandidate b, double sb)
    {
        if (sa != sb) return sa > sb;
        if (a.Mtime != b.Mtime) return a.Mtime < b.Mtime;
        if (a.RelPath.Length != b.RelPath.Length) return a.RelPath.Length < b.RelPath.Length;
        return string.CompareOrdinal(a.RelPath, b.RelPath) < 0;
    }

    private static string Stem(string name) => Path.GetFileNameWithoutExtension(name);
}

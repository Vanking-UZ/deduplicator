namespace Deduplicator.App.Localization;

/// <summary>
/// Единый ресурсный файл строк интерфейса (NFR-12, только русский).
/// Вынесено в одно место на случай будущей локализации.
/// </summary>
public static class Strings
{
    public const string AppName = "Deduplicator";
    public const string AppTagline = "Простой помощник по дискам";

    // Экран загрузки
    public const string SplashStarting = "Запуск…";
    public const string SplashDb = "Подготовка сохранённых данных…";
    public const string SplashVolumes = "Ищем подключённые диски…";
    public const string SplashAlmost = "Почти готово…";

    // Навигация
    public const string NavVolumes = "Мои диски";
    public const string NavExplorer = "Проводник";
    public const string NavScan = "Найти копии";
    public const string NavResults = "Найденные дубли";
    public const string NavStatistics = "Итоги";
    public const string NavConsolidation = "Чистый архив";
    public const string NavJournal = "История";
    public const string NavSettings = "Настройки";

    // Общее
    public const string Refresh = "Обновить";
    public const string Cancel = "Отмена";
    public const string Pause = "Пауза";
    public const string Resume = "Продолжить";
    public const string Start = "Запустить";
    public const string Apply = "Применить";
    public const string Save = "Сохранить";
    public const string Close = "Закрыть";
    public const string Delete = "Удалить";
    public const string Restore = "Восстановить";
    public const string HelpButton = "Как пользоваться";
    public const string Browse = "Обзор…";
    public const string Connected = "Подключён";
    public const string Disconnected = "Не подключён";
    public const string Loading = "Загрузка…";
    public const string Nothing = "Нет данных";
    public const string NeverScanned = "Ещё не проверялся";
    public const string LastScan = "Последняя проверка";
    public const string Files = "файлов";
    public const string Of = "из";

    // Носители
    public const string VolumesTitle = "Архив дисков";
    public const string VolumesSubtitle = "Запомненные диски и файлы остаются видны даже после отключения";
    public const string ScanThisVolume = "Проверить";
    public const string OpenInExplorer = "Открыть в проводнике";
    public const string VolumeSerial = "Серийный номер";
    public const string VolumeFs = "Файловая система";
    public const string VolumeCapacity = "Объём";
    public const string IndexedFiles = "Запомнено файлов";
    public const string SummaryTitle = "Общая сводка";
    public const string TotalVolumes = "Дисков";
    public const string TotalFiles = "Всего файлов";

    // Проводник
    public const string ExplorerTitle = "Файлы на диске";
    public const string ExplorerHint = "Для подключённых дисков показаны реальные файлы, для отключённых — сохранённый список";
    public const string ExplorerLive = "Сейчас с диска";
    public const string ExplorerIndexed = "Сохранённый список файлов (диск отключён)";
    public const string ExplorerNeedScan = "Диск отключён и ещё не проверялся — сохранённого списка файлов нет.";
    public const string SelectVolume = "Выберите диск";

    // Проводник: панель содержимого, виды и операции
    public const string ExplorerViewIcons = "Крупные значки";
    public const string ExplorerViewList = "Список";
    public const string ExplorerViewColumns = "Столбцы";
    public const string ExplorerCopyTo = "Копировать на…";
    public const string ExplorerMoveTo = "Переместить на…";
    public const string ExplorerVolumeOffline = "Диск не подключён — операции с файлами недоступны";
    public const string ExplorerEmptyFolder = "Папка пуста";
    public const string ExplorerOpenFile = "Открыть";
    public const string ExplorerPickDestination = "Куда положить файлы";
    public const string ExplorerCancelAfterFile = "Останавливаем после текущего файла…";
    public const string ExplorerMoveWarning = "Файлы будут удалены с исходного диска только после успешного копирования и проверки.";
    public const string ExplorerRescanHint = "Файлы на проверенном диске изменились. Запустите проверку, чтобы обновить сохранённый список файлов.";
    public const string ColumnName = "Имя";
    public const string ColumnSize = "Размер";
    public const string ColumnModified = "Изменён";
    public const string ColumnCreated = "Создан";
    public const string ColumnCategory = "Тип";
    public const string ColumnPath = "Путь";
    public const string ColumnVolume = "Диск";

    // Сверка
    public const string ScanTitle = "Найти копии";
    public const string ScanSelectVolumes = "Выберите диски для проверки";
    public const string ScanSelectAll = "Выбрать все";
    public const string ScanMultiHint = "Можно выбрать несколько дисков сразу. Программа найдёт копии внутри дисков и между ними.";
    public const string ScanDepth = "Что искать";
    public const string DepthExact = "Одинаковые файлы";
    public const string DepthImages = "Одинаковые файлы и похожие фото";
    public const string DepthAudio = "Одинаковые файлы, похожие фото и музыка";
    public const string ScanSelectAllOff = "Снять все";
    public const string ScanStart = "Начать поиск";
    public const string ScanFailed = "Поиск не удался";

    // P0-A «Что искать» — выбор категорий файлов
    public const string ScanCategories = "Какие файлы проверять";
    public const string CatImages = "Фото и картинки";
    public const string CatVideo = "Видео";
    public const string CatAudio = "Музыка и аудио";
    public const string CatDocuments = "Документы";
    public const string CatOther = "Остальные файлы";
    public const string PickAtLeastOneCategory = "Выберите хотя бы один тип файлов для поиска.";

    // P1.5 строгость поиска похожих
    public const string SimilarityStrictness = "Насколько строго искать похожие";
    public const string StrictnessStrict = "Строго — только очень похожие";
    public const string StrictnessNormal = "Обычно — рекомендуемый режим";
    public const string StrictnessWide = "Шире — больше вариантов, проверяйте внимательно";
    public const string ScanVolumeUnavailable = "Выбранные диски недоступны (отключены). Подключите их и обновите список.";
    public const string ScanRunning = "Идёт поиск";
    public const string ScanStage = "Этап";
    public const string ScanCurrentFile = "Текущий файл";
    public const string ScanFilesSeen = "Просмотрено";
    public const string ScanFilesHashed = "Обработано";
    public const string ScanSkipped = "Пропущено";
    public const string ScanGroups = "Найдено групп";
    public const string ScanDone = "Поиск завершён";
    public const string ScanResume = "Продолжить прерванную проверку";
    public const string ScanNoVolumes = "Нет подключённых дисков. Подключите диск и обновите список.";
    public const string ViewResults = "Показать найденные дубли";

    // Результаты
    public const string ResultsTitle = "Найденные дубли";
    public const string TabExact = "Одинаковые файлы";
    public const string TabSimilarImages = "Похожие фото";
    public const string TabSimilarAudio = "Похожая музыка";
    public const string TabExactVideo = "Точные видео";
    public const string GroupOf = "Файлов в группе";
    public const string Reclaimable = "Можно освободить";
    public const string Representative = "Эталон";
    // 2.10: тултип бейджа «Эталон» — короткая причина, почему выбрана именно эта копия.
    public const string RepresentativeReasonFmt = "Почему эталон: {0}";
    // 3.7: бейдж «лучший кадр серии» — самый резкий кадр в группе похожих фото (только подсказка).
    public const string SharpestBadge = "Лучший кадр";
    public const string SharpestTooltip = "Самый резкий кадр в этой группе похожих фото — его стоит оставить";
    public const string SharpnessHigher = "Резкость: выше";
    public const string SharpnessLower = "Резкость: ниже";
    public const string SharpnessSame = "Резкость: примерно одинаковая";
    public const string KeepThis = "Оставить";
    public const string MarkForDeletion = "К удалению";
    public const string SelectAllButRepresentative = "Отметить лишние копии";
    public const string DeleteSelected = "Убрать отмеченные в корзину";
    public const string Similarity = "Схожесть";
    public const string Threshold = "Порог схожести";
    public const string SimilarManualHint = "Похожие файлы удаляются только вручную: 95% может быть нужной версией.";
    public const string NoGroups = "Копии не найдены. Запустите поиск.";
    public const string ConfirmDeleteTitle = "Подтверждение удаления";
    public const string ConfirmDeleteBody = "Файлы не удаляются насовсем: их можно будет вернуть. В каждой группе останется хотя бы одна копия. Продолжить?";
    public const string KeepAtLeastOne = "В группе должна остаться хотя бы одна копия.";
    public const string AutoSelect = "Отметить лишнее автоматически";
    public const string Export = "Экспорт CSV";
    public const string OpenFolder = "Показать в папке";
    public const string Protected = "Защищён";
    public const string HardlinkAction = "Заменить ссылками";
    public const string HardlinkTitle = "Замена копий ссылками";
    public const string HardlinkWarning = "Лишние копии станут ссылками на эталон — место освободится, а файлы продолжат открываться как раньше. Важно: содержимое станет общим, изменение файла в одном месте изменит его и в остальных. Работает только для копий на одном диске. Продолжить?";

    // Статистика
    public const string StatisticsTitle = "Итоги проверок";
    public const string StatExactGroups = "Одинаковые файлы";
    public const string StatSimilarImages = "Похожие фото";
    public const string StatSimilarAudio = "Похожая музыка";
    public const string StatTotalReclaimable = "Всего можно освободить";
    public const string StatDuplicateFiles = "Лишних копий";
    public const string StatByCategory = "Какие файлы занимают место";
    public const string StatIndexed = "Запомнено в архиве";

    // Консолидация
    public const string ConsolidationTitle = "Чистый архив";
    public const string ConsolidationSubtitle = "Скопировать по одной версии каждого файла в одну папку. Исходные диски не меняются.";
    public const string ConsolidationSources = "Откуда брать файлы";
    public const string ConsolidationTarget = "Куда сложить архив";
    public const string ConsolidationRule = "Как выбрать лучшую копию";
    public const string RuleSmart = "Умно (рекомендуется)";
    public const string RuleOldest = "Самый старый файл";
    public const string RuleNewest = "Самый новый файл";
    public const string RuleShortestPath = "Кратчайший путь";
    public const string RuleLongestName = "Самое длинное имя";
    public const string BuildPlan = "Посчитать перед копированием";
    public const string ExecutePlan = "Начать копирование";
    public const string PlanUnique = "Будет скопировано";
    public const string PlanSkipped = "Дублей пропущено";
    public const string PlanBytesCopy = "Нужно места";
    public const string PlanBytesSaved = "Сэкономится";
    public const string PreserveStructure = "Сохранить папки как были";
    // 3.5: схлопывание почти одинаковых фото при консолидации.
    public const string CollapseSimilarPhotos = "Объединять почти одинаковые фото (оставлять самый чёткий кадр)";
    public const string PlanCollapsed = "Похожих объединено";
    public const string CollapsedBadge = "Похожее";
    public const string CollapsedKeptPrefix = "оставлен";
    public const string ConsolidationDone = "Чистый архив готов";
    public const string ConsolidationCancelled = "Копирование остановлено";
    public const string ConsolidationNeedInput = "Выберите хотя бы один диск и папку, куда сложить архив.";
    public const string Copied = "Скопировано";
    public const string FailedItems = "Ошибок";

    // Журнал
    public const string JournalTitle = "История действий";
    public const string JournalHint = "Здесь видно, что было убрано, и можно восстановить файл, если он ещё доступен";
    public const string OpType = "Операция";
    public const string OpDeleteRecycle = "В корзину";
    public const string OpQuarantine = "В карантин";
    public const string OpRestore = "Восстановление";
    public const string OpHardlink = "Замена ссылкой";
    public const string When = "Когда";
    public const string OpRestored = "Восстановлен";
    public const string RestoreFailed = "Не удалось восстановить: исходный путь занят или файл в карантине недоступен.";

    // Настройки
    public const string SettingsTitle = "Настройки";
    public const string SetImageThreshold = "Насколько строго сравнивать фото";
    public const string SetAudioThreshold = "Насколько строго сравнивать музыку";
    public const string SetQuarantine = "Папка для безопасного хранения удалённых файлов";
    public const string SetUseRecycle = "Убирать файлы в Корзину Windows (если выключено — в папку карантина)";
    public const string SetParallelism = "Скорость проверки";
    public const string SetPixelHash = "Проверять фото точнее, но медленнее";
    public const string SetRule = "Какую копию оставлять главной";
    public const string SetTheme = "Внешний вид";
    public const string SetFpcalc = "Файл для сравнения музыки (fpcalc.exe)";
    public const string ThemeLight = "Светлая";
    public const string ThemeDark = "Тёмная";
    public const string ThemeSystem = "Системная";
    public const string FpcalcAvailable = "Музыку можно сравнивать по звучанию";
    public const string FpcalcMissing = "Поиск похожей музыки выключен: не найден файл fpcalc.exe";
    public const string SetRotation = "Учитывать повороты и отражения фото";
    public const string SetMinSize = "Не искать файлы меньше, МБ";
    public const string SetSkipHidden = "Не трогать скрытые и системные файлы";
    public const string SetExcludedExt = "Какие расширения пропустить";
    public const string SetReferenceFolders = "Защищённые папки: файлы внутри не удаляются";
    public const string AddFolder = "Добавить папку";
    public const string Remove = "Убрать";
    public const string SetScanFilters = "Что пропускать при поиске";
    public const string SetUpdates = "Обновления программы";
    public const string SetCheckUpdates = "Проверять обновления на GitHub (требует интернет)";
    public const string SetUpdateRepo = "Адрес обновлений на GitHub";
    public const string CheckNow = "Проверить сейчас";
    public const string UpdateUpToDate = "Установлена последняя версия";
    public const string UpdateAvailable = "Доступна новая версия";
    public const string UpdateOpen = "Открыть страницу релиза";
    public const string UpdateError = "Не удалось проверить обновления";
    public const string UpdateChecking = "Проверка…";
    public const string CurrentVersionLabel = "Текущая версия";

    // Диагностика (ТЗ MVP): чтобы пользователь мог скопировать данные о версии/режимах для поддержки.
    public const string SetDiagnostics = "Диагностика";
    public const string CopyDiagnostics = "Скопировать диагностику";
    public const string DiagnosticsCopied = "Скопировано в буфер обмена";
    public const string OpenLogFolder = "Открыть папку логов";

    // Постраничный показ результатов
    public const string ShowMore = "Показать ещё";

    // P0-B Групповое переименование
    public const string RenameSelected = "Переименовать выбранные…";
    public const string RenameTitle = "Переименовать выбранные файлы";
    public const string RenameBaseLabel = "Общее имя";
    public const string RenameStartLabel = "Начать с номера";
    public const string RenamePreviewLabel = "Предпросмотр";
    public const string RenameApply = "Переименовать";

    // P1.6 Распределение результатов
    public const string DistributeSelected = "Разложить выбранное…";
    public const string DistributeTitle = "Разложить выбранные файлы по папкам";
    public const string DistributeReportMode = "Только отчёт CSV (ничего не двигать)";
    public const string DistributeCopyMode = "Скопировать в папки и проверить (оригиналы остаются)";
    public const string DistributeChooseFolder = "Выбрать папку…";
    public const string DistributeRun = "Выполнить";

    // Память дисков (кросс-диск)
    public const string NavCrossDisk = "Между дисками";
    public const string CrossDiskTitle = "Дубли между дисками";
    public const string CrossDiskSubtitle = "Одинаковые файлы на разных дисках";
    public const string CrossDiskNothing = "Совпадений между дисками не найдено. Проверьте несколько дисков.";
    public const string CrossDiskAcross = "дисков";
    public const string RemoveFromCatalog = "Убрать из архива";
    public const string ConfirmRemoveVolume = "Убрать диск из архива вместе с сохранённым списком файлов?";

    // Очистка диска (мусор)
    public const string NavCleanup = "Очистка";
    public const string CleanupTitle = "Ненужные файлы";
    public const string CleanupSubtitle = "Пустые папки, временные файлы и битые ярлыки";
    public const string CleanupEmptyFolders = "Пустые папки";
    public const string CleanupZeroByte = "Файлы 0 байт";
    public const string CleanupTemp = "Временные файлы";
    public const string CleanupBroken = "Битые ярлыки";
    public const string CleanupLarge = "Большие файлы";
    public const string CleanupLargeThreshold = "Порог большого файла, МБ";
    public const string CleanupScan = "Найти ненужные файлы";
    public const string CleanupFound = "Найдено";
    public const string CleanupDeleteSelected = "Удалить отмеченное в корзину";
    public const string CleanupNothing = "Ненужные файлы не найдены.";
    public const string CleanupError = "Не удалось проверить ненужные файлы";
    public const string CleanupConfirmBody = "Отмеченные элементы будут перемещены в Корзину Windows. Продолжить?";
    public const string CleanupDeleteFailed = "Не удалось удалить часть элементов";
    public const string CleanupWhat = "Что искать";
    public const string JunkEmptyFolder = "Пустая папка";
    public const string JunkZeroByte = "Файл 0 байт";
    public const string JunkTemp = "Временный файл";
    public const string JunkBroken = "Битый ярлык";
    public const string JunkLarge = "Большой файл";

    // Метки файловых операций (часть переиспользуется экраном «Очистка»: FmSelectAll/FmClear/FmSelected).
    public const string FmCopyToOther = "Копировать в соседнюю";
    public const string FmMoveToOther = "Переместить в соседнюю";
    public const string FmDelete = "Удалить";
    public const string FmRename = "Переименовать";
    public const string FmNewFolder = "Новая папка";
    public const string FmUp = "Вверх";
    public const string FmSelectAll = "Выделить всё";
    public const string FmClear = "Снять выделение";
    public const string FmSelected = "Выбрано";
    public const string FmConfirmDeleteTitle = "Удаление файлов";
    public const string FmConfirmDeleteBody = "Выбранные элементы будут перемещены в Корзину Windows. Продолжить?";
    public const string FmNewFolderTitle = "Имя новой папки";
    public const string FmNewFolderDefault = "Новая папка";
    public const string FmRenameTitle = "Новое имя";
    public const string FmBusy = "Выполняется операция…";
    public const string FmAutoSort = "Сортировать";
    public const string FmSortBy = "Раскладка";
    public const string FmSortByType = "по типу";
    public const string FmSortByDate = "по дате";
    public const string FmSortByExt = "по расширению";
    public const string FmNoExt = "Без расширения";
    public const string FmDropHint = "Перетащите файлы из проводника на панель, чтобы скопировать";
    public const string FmSearch = "Поиск по имени…";
    public const string Ok = "ОК";

    // Здоровье дисков (SMART)
    public const string NavHealth = "Здоровье дисков";
    public const string HealthTitle = "Здоровье дисков";
    public const string HealthSubtitle = "Состояние дисков и предупреждения";
    public const string HealthHealthy = "Исправен";
    public const string HealthWarning = "Предупреждение";
    public const string HealthUnhealthy = "Неисправен";
    public const string HealthUnknown = "Неизвестно";
    public const string HealthTemp = "Температура";
    public const string HealthPowerOn = "Наработка";
    public const string HealthWear = "Износ";
    public const string HealthReadErrors = "Ошибки чтения";
    public const string HealthMediaType = "Тип";
    public const string HealthBus = "Подключение";
    public const string HealthSerial = "Серийный номер";
    public const string HealthSize = "Объём";
    public const string HealthRpm = "Обороты";
    public const string HealthHours = "ч";
    public const string HealthNoData = "Windows не предоставила часть показателей диска. Основные сведения и проверка файлов по-прежнему доступны.";
    public const string HealthPredict = "Диск может скоро выйти из строя — сделайте резервную копию важных файлов.";
    public const string HealthUnsupported = "Проверка здоровья дисков доступна только на Windows.";

    // Категории
    public static string Category(Core.Models.FileCategory c) => c switch
    {
        Core.Models.FileCategory.Image => "Изображения",
        Core.Models.FileCategory.Audio => "Музыка",
        Core.Models.FileCategory.Video => "Видео",
        Core.Models.FileCategory.Document => "Документы",
        _ => "Прочее"
    };
}

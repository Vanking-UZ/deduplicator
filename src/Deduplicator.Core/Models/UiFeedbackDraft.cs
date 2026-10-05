namespace Deduplicator.Core.Models;

/// <summary>
/// Локальный черновик замечаний к интерфейсу. Он не отправляется по сети и нужен
/// только для того, чтобы пользователь мог собрать несколько пометок за один сеанс.
/// </summary>
public sealed class UiFeedbackDraft
{
    /// <summary>Общий комментарий ко всему интерфейсу.</summary>
    public string GeneralComment { get; set; } = string.Empty;

    /// <summary>Пометки к отдельным элементам интерфейса.</summary>
    public List<UiFeedbackEntry> Entries { get; set; } = new();
}

/// <summary>Одна пометка, оставленная на выбранном элементе интерфейса.</summary>
public sealed class UiFeedbackEntry
{
    /// <summary>Экран, на котором расположен элемент.</summary>
    public string Page { get; set; } = string.Empty;

    /// <summary>Стабильный технический идентификатор выбранного элемента.</summary>
    public string ElementId { get; set; } = string.Empty;

    /// <summary>Безопасная стабильная метка элемента (без экранного текста и путей).</summary>
    public string ElementLabel { get; set; } = string.Empty;

    /// <summary>Комментарий пользователя к элементу.</summary>
    public string Comment { get; set; } = string.Empty;
}

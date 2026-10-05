using CommunityToolkit.Mvvm.ComponentModel;

namespace Deduplicator.App.Infrastructure;

/// <summary>Базовый класс для страниц-вью-моделей.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>Вызывается при переходе на страницу — место для (пере)загрузки данных.</summary>
    public virtual void OnNavigatedTo() { }

    /// <summary>Вызывается при уходе со страницы — место для очистки незавершённых диалогов/операций.</summary>
    public virtual void OnNavigatedFrom() { }

    // Единый модальный слой. Пока у активной страницы открыт
    // любой диалог-оверлей, MainWindow затемняет и блокирует сайдбар, а Esc закрывает диалог.
    // Страницы с диалогами переопределяют оба члена и извещают об изменении HasOpenDialog
    // из partial-методов OnShow*Changed.

    /// <summary>Истина, когда на странице открыт какой-либо диалог-оверлей.</summary>
    public virtual bool HasOpenDialog => false;

    /// <summary>Закрыть верхний открытый диалог (реакция на Esc). Подтверждать по Enter нельзя.</summary>
    public virtual void CloseDialog() { }
}

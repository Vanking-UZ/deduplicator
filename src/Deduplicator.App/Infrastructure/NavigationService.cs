using Microsoft.Extensions.DependencyInjection;

namespace Deduplicator.App.Infrastructure;

/// <summary>Простая навигация между страницами через композиционный корень.</summary>
public sealed class NavigationService
{
    private readonly IServiceProvider _sp;

    /// <summary>Устанавливается главной вью-моделью; выполняет фактическую смену страницы.</summary>
    public Action<PageViewModel>? Navigator { get; set; }

    public NavigationService(IServiceProvider sp) => _sp = sp;

    public T Get<T>() where T : notnull => _sp.GetRequiredService<T>();

    public void To<T>() where T : PageViewModel => Navigator?.Invoke(Get<T>());

    public void To(PageViewModel page) => Navigator?.Invoke(page);
}

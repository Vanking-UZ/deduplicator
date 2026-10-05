using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Deduplicator.App.Infrastructure;

namespace Deduplicator.App;

/// <summary>Сопоставляет вью-модель её представлению по соглашению об именах (XxxViewModel → XxxView).</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control Build(object? data)
    {
        if (data is null)
            return new TextBlock { Text = "null" };

        var vmType = data.GetType();
        var name = vmType.FullName!
            .Replace(".ViewModels.", ".Views.")
            .Replace("ViewModel", "View");

        var type = vmType.Assembly.GetType(name);
        if (type is not null)
            return (Control)Activator.CreateInstance(type)!;

        return new TextBlock { Text = "Не найдено представление: " + name };
    }

    public bool Match(object? data) => data is PageViewModel;
}

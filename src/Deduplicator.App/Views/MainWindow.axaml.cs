using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.ViewModels;

namespace Deduplicator.App.Views;

public partial class MainWindow : Window
{
    private const double Pad = 8;     // отступ рамки от элемента
    private const double Edge = 16;   // минимальный отступ от края окна
    private const double Gap = 18;    // зазор между элементом и карточкой
    private const double Radius = 12; // скругление «окошка»
    private const double LargeTargetMaxHeight = 220;
    private const double CompactWidth = 900;

    private MainWindowViewModel? _vm;
    private bool _queued;
    private bool _focusOnboarding;
    private Rect _lastRect = new(-1, -1, 0, 0);
    private Size _lastCardSize = new(-1, -1);
    private WeakReference<Control>? _hoveredFeedbackTarget;
    private readonly WindowNotificationManager _notifications;
    private readonly IPageTransition? _defaultPageTransition;

    public MainWindow()
    {
        InitializeComponent();
        _defaultPageTransition = PageHost?.PageTransition;
        MotionPreferences.Changed += ApplyMotionPreference;
        Closed += (_, _) =>
        {
            MotionPreferences.Changed -= ApplyMotionPreference;
            if (_vm?.Feedback.HasDraft == true)
                _vm.Feedback.SaveDraftCommand.Execute(null);
        };
        ApplyMotionPreference();
        // Ненавязчивые тосты поверх окна (сторож дубликатов, подсказка «носитель с фотографиями»).
        _notifications = new WindowNotificationManager(this)
        {
            Position = NotificationPosition.TopRight,
            MaxItems = 3
        };
        DataContextChanged += (_, _) => Attach();
        SizeChanged += (_, _) =>
        {
            UpdateResponsiveState();
            _lastRect = new(-1, -1, 0, 0);
            _lastCardSize = new(-1, -1);
            Schedule();
        };
        LayoutUpdated += (_, _) => Schedule();
        // Глобальный Esc закрывает диалог активной страницы. Туннель (паттерн
        // ResultsView) — иначе KeyDown не дошёл бы без фокуса внутри диалога. Enter глобальным
        // подтверждением сознательно НЕ делаем: для danger-диалогов это опасно.
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnFeedbackPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnFeedbackPointerMoved, RoutingStrategies.Tunnel);
        PointerExited += (_, _) => ClearFeedbackHover();
#if DEBUG
        this.AttachDevTools();
#endif
    }

    /// <summary>
    /// Информационный тост в правом верхнем углу окна. <paramref name="onClick"/> — необязательное
    /// действие по клику (например, перейти на экран сверки). Вызывать на UI-потоке.
    /// </summary>
    public void ShowToast(string title, string message, Action? onClick = null)
    {
        try
        {
            _notifications.Show(new Notification(title, message, NotificationType.Information,
                TimeSpan.FromSeconds(8), onClick));
        }
        catch (Exception ex) { Core.Util.AppLog.Error("ShowToast", ex); }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ClampToWorkingArea();
        UpdateResponsiveState();
        Attach();
        Schedule();
        // Самопроверка: при DEDUP_SMOKE=1 прогоняем навигацию по всем экранам и закрываемся.
        if (Environment.GetEnvironmentVariable("DEDUP_SMOKE") == "1")
            _ = RunSmokeAsync();
    }

    /// <summary>Esc закрывает верхний диалог активной страницы (включая просмотрщик
    /// Результатов — его локальный хендлер оставил себе только стрелки).</summary>
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm?.IsOnboardingVisible == true)
        {
            if (e.Key == Key.Escape)
            {
                _vm.CloseOnboarding();
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                _vm.NextOnboardingCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Left && _vm.CanGoBack)
            {
                _vm.PreviousOnboardingCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }

        if (_vm?.Feedback.IsPickerActive == true && e.Key == Key.Escape)
        {
            _vm.Feedback.StopPickerCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape) return;
        if (_vm?.CurrentPage is { HasOpenDialog: true } page)
        {
            page.CloseDialog();
            e.Handled = true;
        }
    }

    private void Attach()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.Feedback.PropertyChanged -= OnFeedbackChanged;
        }
        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
            _vm.Feedback.PropertyChanged += OnFeedbackChanged;
            _vm.Feedback.CopyToClipboardAsync = CopyFeedbackToClipboardAsync;
        }
        _lastRect = new(-1, -1, 0, 0);
        _lastCardSize = new(-1, -1);
        _focusOnboarding = _vm?.IsOnboardingVisible == true;
        Schedule();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsOnboardingVisible)
            or nameof(MainWindowViewModel.OnboardingIndex)
            or nameof(MainWindowViewModel.SelectedNav)
            or nameof(MainWindowViewModel.CurrentPage))
        {
            _lastRect = new(-1, -1, 0, 0); // новый шаг — форсируем пересчёт
            _lastCardSize = new(-1, -1);
            _focusOnboarding = _vm?.IsOnboardingVisible == true;
            Schedule();
        }
    }

    private void OnFeedbackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FeedbackPickerViewModel.IsPickerActive)
            or nameof(FeedbackPickerViewModel.IsPanelOpen))
        {
            if (_vm?.Feedback.IsPickerActive != true) ClearFeedbackHover();
            Schedule();
        }
    }

    private void Schedule()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            Place();
            PlaceFeedbackOutlines();
            if (_focusOnboarding && _vm?.IsOnboardingVisible == true)
            {
                _focusOnboarding = false;
                OnboardingNextButton?.Focus();
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>Замеряет реальный подсвечиваемый контрол и точно расставляет затемнение, рамку и карточку.</summary>
    private void Place()
    {
        if (_vm is null || !_vm.IsOnboardingVisible) return;
        if (OnboardingMask is null || OnboardingBlocker is null || OnboardingHighlight is null || OnboardingCard is null) return;

        double w = Math.Max(Bounds.Width, MinWidth);
        double h = Math.Max(Bounds.Height, MinHeight);
        OnboardingBlocker.Width = w;
        OnboardingBlocker.Height = h;

        // На небольшом экране карточка обучения остаётся целиком внутри окна, а её содержимое
        // прокручивается. На обычном размере сохраняется просторная ширина 480 DIP.
        OnboardingCard.Width = Math.Clamp(w - Edge * 2, 320, 480);
        OnboardingCard.MaxHeight = Math.Max(300, h - Edge * 2);

        var rect = MeasureTarget(_vm.OnboardingTargetName, w, h);

        var full = new RectangleGeometry(new Rect(0, 0, w, h));
        var cardSize = new Size(
            OnboardingCard.Bounds.Width > 1 ? OnboardingCard.Bounds.Width : 440,
            OnboardingCard.Bounds.Height > 1 ? OnboardingCard.Bounds.Height : 360);

        // Без мерцания: частый LayoutUpdated можно пропустить только когда не изменились И цель,
        // И фактический размер карточки. Это важно для welcome: первый замер идёт до layout, затем
        // карточка становится выше и должна заново безопасно центрироваться.
        var key = rect ?? new Rect(-2, -2, 0, 0);
        if (key == _lastRect && cardSize == _lastCardSize) return;
        _lastRect = key;
        _lastCardSize = cardSize;

        if (rect is { } r)
        {
            OnboardingMask.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, RoundedRect(r, Radius));
            OnboardingHighlight.IsVisible = true;
            Canvas.SetLeft(OnboardingHighlight, r.X);
            Canvas.SetTop(OnboardingHighlight, r.Y);
            OnboardingHighlight.Width = r.Width;
            OnboardingHighlight.Height = r.Height;

            var cp = PlaceCard(r, cardSize, w, h);
            Canvas.SetLeft(OnboardingCard, cp.X);
            Canvas.SetTop(OnboardingCard, cp.Y);
        }
        else
        {
            // Цель ещё не отрисована (или её нет) — сплошное затемнение, карточка по центру.
            OnboardingMask.Data = full;
            OnboardingHighlight.IsVisible = false;
            Canvas.SetLeft(OnboardingCard, Math.Round((w - cardSize.Width) / 2));
            Canvas.SetTop(OnboardingCard, Math.Round(Math.Max(Edge, (h - cardSize.Height) / 2)));
        }
    }

    private Rect? MeasureTarget(string name, double w, double h)
    {
        if (string.IsNullOrEmpty(name)) return null;

        var target = this.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => c.Name == name && c.IsVisible && c.Bounds.Width > 1 && c.Bounds.Height > 1);
        if (target is null) return null;

        var topLeft = target.TranslatePoint(new Point(0, 0), this);
        if (topLeft is not { } p) return null;

        // Spotlight обязан показывать реальный элемент. На низком окне кнопка старта может быть
        // ниже viewport; блокирующий overlay не даёт пользователю прокрутить страницу вручную.
        // Прокручиваем ближайший ScrollViewer программно и пересчитываем позицию после layout.
        double visibleTargetHeight = Math.Min(target.Bounds.Height, LargeTargetMaxHeight);
        bool outsideViewport = p.Y < Edge || p.Y + visibleTargetHeight > h - Edge;
        if (outsideViewport)
        {
            target.BringIntoView();
            Dispatcher.UIThread.Post(Schedule, DispatcherPriority.Background);
            return null;
        }

        double x = Math.Max(Edge, p.X - Pad);
        double y = Math.Max(Edge, p.Y - Pad);
        double right = Math.Min(w - Edge, p.X + target.Bounds.Width + Pad);
        double bottom = Math.Min(h - Edge, p.Y + target.Bounds.Height + Pad);
        if (right - x < 8 || bottom - y < 8) return null;

        var rect = new Rect(Math.Round(x), Math.Round(y), Math.Round(right - x), Math.Round(bottom - y));
        return NormalizeTarget(name, rect);
    }

    private static Point PlaceCard(Rect target, Size card, double w, double h)
    {
        double cw = Math.Min(card.Width, w - Edge * 2);
        double ch = Math.Min(card.Height, h - Edge * 2);

        bool wideTarget = target.Width > cw + Gap * 2;

        if (!wideTarget && target.Right + Gap + cw <= w - Edge) // справа от кнопки
            return Clamp(target.Right + Gap, target.Y, cw, ch, w, h);
        if (!wideTarget && target.X - Gap - cw >= Edge)         // слева от кнопки
            return Clamp(target.X - Gap - cw, target.Y, cw, ch, w, h);
        if (target.Bottom + Gap + ch <= h - Edge)               // под целью
            return Clamp(target.X, target.Bottom + Gap, cw, ch, w, h);
        if (target.Y - Gap - ch >= Edge)                        // над целью
            return Clamp(target.X, target.Y - Gap - ch, cw, ch, w, h);
        if (target.Right + Gap + cw <= w - Edge)                // справа
            return Clamp(target.Right + Gap, target.Y, cw, ch, w, h);
        if (target.X - Gap - cw >= Edge)                        // слева
            return Clamp(target.X - Gap - cw, target.Y, cw, ch, w, h);

        return Clamp(w - Edge - cw, target.Center.Y - ch / 2, cw, ch, w, h); // прижать к краю
    }

    private static Rect NormalizeTarget(string name, Rect rect)
    {
        if (name == "OnboardingMainNav" || rect.Height <= LargeTargetMaxHeight)
            return rect;

        return new Rect(rect.X, rect.Y, rect.Width, LargeTargetMaxHeight);
    }

    private static Point Clamp(double x, double y, double cw, double ch, double w, double h)
        => new(Math.Round(Math.Clamp(x, Edge, Math.Max(Edge, w - Edge - cw))),
               Math.Round(Math.Clamp(y, Edge, Math.Max(Edge, h - Edge - ch))));

    private static Geometry RoundedRect(Rect rect, double radius)
    {
        double r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(new Point(rect.X + r, rect.Y), true);
        c.LineTo(new Point(rect.Right - r, rect.Y));
        c.ArcTo(new Point(rect.Right, rect.Y + r), new Size(r, r), 0, false, SweepDirection.Clockwise);
        c.LineTo(new Point(rect.Right, rect.Bottom - r));
        c.ArcTo(new Point(rect.Right - r, rect.Bottom), new Size(r, r), 0, false, SweepDirection.Clockwise);
        c.LineTo(new Point(rect.X + r, rect.Bottom));
        c.ArcTo(new Point(rect.X, rect.Bottom - r), new Size(r, r), 0, false, SweepDirection.Clockwise);
        c.LineTo(new Point(rect.X, rect.Y + r));
        c.ArcTo(new Point(rect.X + r, rect.Y), new Size(r, r), 0, false, SweepDirection.Clockwise);
        c.EndFigure(true);
        return g;
    }

    private void ApplyMotionPreference()
    {
        if (MotionPreferences.AnimationsEnabled) Classes.Remove("reduceMotion");
        else if (!Classes.Contains("reduceMotion")) Classes.Add("reduceMotion");

        if (PageHost is not null)
            PageHost.PageTransition = MotionPreferences.AnimationsEnabled ? _defaultPageTransition : null;
    }

    private void UpdateResponsiveState()
    {
        bool compact = Bounds.Width > 0 && Bounds.Width < CompactWidth;
        PseudoClasses.Set(":compact", compact);
        if (ShellGrid?.ColumnDefinitions.Count > 0)
            ShellGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 78 : 232);
    }

    private void ClampToWorkingArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;

        double scale = Math.Max(0.1, screen.Scaling);
        double maxWidth = Math.Max(MinWidth, screen.WorkingArea.Width / scale * 0.96);
        double maxHeight = Math.Max(MinHeight, screen.WorkingArea.Height / scale * 0.96);
        double clampedWidth = Math.Clamp(Width, MinWidth, maxWidth);
        double clampedHeight = Math.Clamp(Height, MinHeight, maxHeight);
        bool resized = Math.Abs(clampedWidth - Width) > 0.5 || Math.Abs(clampedHeight - Height) > 0.5;
        Width = clampedWidth;
        Height = clampedHeight;

        if (resized)
        {
            int physicalWidth = (int)Math.Round(clampedWidth * scale);
            int physicalHeight = (int)Math.Round(clampedHeight * scale);
            Position = new PixelPoint(
                screen.WorkingArea.X + Math.Max(0, (screen.WorkingArea.Width - physicalWidth) / 2),
                screen.WorkingArea.Y + Math.Max(0, (screen.WorkingArea.Height - physicalHeight) / 2));
        }
    }

    private async Task CopyFeedbackToClipboardAsync(string text)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is null)
            throw new InvalidOperationException("Clipboard is unavailable.");

        await topLevel.Clipboard.SetTextAsync(text);
    }

    /// <summary>
    /// In feedback mode a primary click selects a control and is handled before
    /// the control can execute its ordinary command. The picker panel itself and
    /// its launcher remain interactive so the user can type, save and finish.
    /// </summary>
    private void OnFeedbackPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm?.Feedback.IsPickerActive != true) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var source = e.Source as Visual;
        if (source is null || IsInside(source, FeedbackPanel) || IsInside(source, FeedbackPickerButton)) return;

        var target = ResolveFeedbackTarget(source);
        if (target is null) return;

        _vm.Feedback.ToggleSelection(target, DescribeFeedbackTarget(target));
        e.Handled = true;
        Schedule();
    }

    private void OnFeedbackPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_vm?.Feedback.IsPickerActive != true)
        {
            ClearFeedbackHover();
            return;
        }

        var source = e.Source as Visual;
        if (source is null || IsInside(source, FeedbackPanel) || IsInside(source, FeedbackPickerButton))
        {
            ClearFeedbackHover();
            return;
        }

        var target = ResolveFeedbackTarget(source);
        if (_hoveredFeedbackTarget is { } weak && weak.TryGetTarget(out var previous) &&
            ReferenceEquals(previous, target))
            return;

        _hoveredFeedbackTarget = target is null ? null : new WeakReference<Control>(target);
        PlaceFeedbackHoverOutline();
    }

    private Control? ResolveFeedbackTarget(Visual source)
    {
        Control? visualCandidate = null;
        Control? surfaceCandidate = null;
        Control? containerCandidate = null;

        // Keep the nearest meaningful visual element, but let a parent control
        // win when a label/icon is part of an actionable control. This avoids
        // reporting invisible template chrome while still allowing independent
        // text, cards, badges, icons and progress bars to be marked.
        for (Visual? current = source; current is not null; current = current.GetVisualParent())
        {
            if (current is not Control control) continue;
            if (IsFeedbackBoundary(control)) break;
            if (IsExcludedFromFeedback(control)) return null;
            if (!control.IsVisible || control.Bounds.Width < 2 || control.Bounds.Height < 2)
                continue;

            if (IsFeedbackWholeControl(control)) return control;
            if (IsFeedbackStandaloneVisual(control)) visualCandidate ??= control;
            else if (control is Border) surfaceCandidate ??= control;
            else containerCandidate ??= control;
        }

        return visualCandidate ?? surfaceCandidate ?? containerCandidate;
    }

    private bool IsFeedbackBoundary(Control control) =>
        ReferenceEquals(control, this) || ReferenceEquals(control, PageHost) || ReferenceEquals(control, ShellGrid);

    private bool IsExcludedFromFeedback(Control control) =>
        IsInside(control, FeedbackPanel) ||
        IsInside(control, FeedbackPickerButton) || IsInside(control, OnboardingCard) ||
        IsInside(control, OnboardingHighlight);

    private static bool IsFeedbackWholeControl(Control control) => control is
        Button or ToggleButton or TextBox or ComboBox or CheckBox or RadioButton or
        ListBoxItem or TabItem or Slider or ScrollBar or ProgressBar;

    private static bool IsFeedbackStandaloneVisual(Control control) => control is
        TextBlock or PathIcon or Image;

    private FeedbackTargetDescriptor DescribeFeedbackTarget(Control target)
    {
        string page = IsInside(target, PageHost)
            ? _vm?.CurrentPage?.GetType().Name ?? "MainWindow"
            : "Navigation";
        string elementId = string.IsNullOrWhiteSpace(target.Name)
            ? BuildFeedbackElementId(target)
            : $"{target.GetType().Name}:{target.Name}";
        string label = $"{FeedbackElementKind(target)} · {elementId}";
        return new FeedbackTargetDescriptor(page, elementId, label);
    }

    private static string FeedbackElementKind(Control target) => target switch
    {
        TextBlock => "Текст",
        PathIcon => "Иконка",
        CheckBox => "Флажок",
        RadioButton => "Переключатель",
        ToggleButton => "Переключатель",
        TextBox => "Поле ввода",
        ComboBox => "Список",
        TabItem => "Вкладка",
        ListBoxItem => "Строка списка",
        Slider => "Ползунок",
        ScrollBar => "Полоса прокрутки",
        Button => "Кнопка",
        Border border when border.Classes.Contains("badge") => "Плашка",
        Border border when border.Classes.Contains("card") || border.Classes.Contains("cardFlat") || border.Classes.Contains("hero") => "Карточка",
        Border => "Блок",
        _ => "Элемент"
    };

    private string BuildFeedbackElementId(Control target)
    {
        var parts = new Stack<string>();
        Visual? current = target;
        int depth = 0;
        while (current is not null && !ReferenceEquals(current, this) && depth++ < 8)
        {
            if (current is Control control)
            {
                if (!string.IsNullOrWhiteSpace(control.Name))
                {
                    parts.Push($"{control.GetType().Name}:{control.Name}");
                    break;
                }

                var parent = current.GetVisualParent();
                int index = 0;
                if (parent is not null)
                {
                    foreach (var sibling in parent.GetVisualChildren())
                    {
                        if (sibling is not Control) continue;
                        if (ReferenceEquals(sibling, current)) break;
                        index++;
                    }
                }
                parts.Push($"{control.GetType().Name}[{index}]");
            }
            current = current.GetVisualParent();
        }

        return parts.Count == 0 ? target.GetType().Name : string.Join("/", parts);
    }

    private void PlaceFeedbackOutlines()
    {
        var feedback = _vm?.Feedback;
        if (feedback is null || !feedback.IsPickerActive)
        {
            ClearFeedbackHover();
            return;
        }

        foreach (var item in feedback.Items)
        {
            if (item.Target is not { } weak || !weak.TryGetTarget(out var target) ||
                !TryGetFeedbackOutlineRect(target, out var rect))
            {
                feedback.SetOutline(item, 0, 0, 0, 0, visible: false);
                continue;
            }

            feedback.SetOutline(item, rect.X, rect.Y, rect.Width, rect.Height, visible: true);
        }

        PlaceFeedbackHoverOutline();
    }

    private bool TryGetFeedbackOutlineRect(Control target, out Rect rect)
    {
        rect = default;
        if (!target.IsVisible || target.Bounds.Width < 2 || target.Bounds.Height < 2)
            return false;

        var position = target.TranslatePoint(new Point(0, 0), this);
        if (position is not { } point) return false;

        const double outlinePadding = 5;
        double windowWidth = Math.Max(Bounds.Width, MinWidth);
        double windowHeight = Math.Max(Bounds.Height, MinHeight);
        double left = Math.Max(0, point.X - outlinePadding);
        double top = Math.Max(0, point.Y - outlinePadding);
        double right = Math.Min(windowWidth, point.X + target.Bounds.Width + outlinePadding);
        double bottom = Math.Min(windowHeight, point.Y + target.Bounds.Height + outlinePadding);
        if (right - left < 4 || bottom - top < 4) return false;

        rect = new Rect(Math.Round(left), Math.Round(top),
            Math.Round(right - left), Math.Round(bottom - top));
        return true;
    }

    private void PlaceFeedbackHoverOutline()
    {
        if (FeedbackHoverHighlight is null || _vm?.Feedback.IsPickerActive != true ||
            _hoveredFeedbackTarget is not { } weak || !weak.TryGetTarget(out var target) ||
            !TryGetFeedbackOutlineRect(target, out var rect))
        {
            if (FeedbackHoverHighlight is not null) FeedbackHoverHighlight.IsVisible = false;
            return;
        }

        Canvas.SetLeft(FeedbackHoverHighlight, rect.X);
        Canvas.SetTop(FeedbackHoverHighlight, rect.Y);
        FeedbackHoverHighlight.Width = rect.Width;
        FeedbackHoverHighlight.Height = rect.Height;
        FeedbackHoverHighlight.IsVisible = true;
    }

    private void ClearFeedbackHover()
    {
        _hoveredFeedbackTarget = null;
        if (FeedbackHoverHighlight is not null) FeedbackHoverHighlight.IsVisible = false;
    }

    private static bool IsInside(Visual source, Visual? container)
    {
        for (Visual? current = source; current is not null; current = current.GetVisualParent())
            if (ReferenceEquals(current, container)) return true;
        return false;
    }

    private async Task RunSmokeAsync()
    {
        try
        {
            if (DataContext is MainWindowViewModel vm)
            {
                await RunFeedbackPickerSmokeAsync(vm);
                // Прогон в обеих темах — тема меняет словари ресурсов (тени, градиенты),
                // критерий «0 binding-ошибок» должен выполняться и в Light, и в Dark.
                foreach (var variant in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
                {
                    if (Application.Current is not null)
                        Application.Current.RequestedThemeVariant = variant;
                    foreach (var item in vm.NavItems)
                    {
                        vm.SelectedNav = item;
                        await Task.Delay(300);
                    }
                }
            }
        }
        finally
        {
            Close();
        }
    }

    /// <summary>
    /// The publish smoke intentionally realizes a real feedback item. A hidden
    /// panel with an empty list would not exercise the item template, its removal
    /// command binding, or the outline presentation used by the picker.
    /// </summary>
    private async Task RunFeedbackPickerSmokeAsync(MainWindowViewModel vm)
    {
        if (vm.IsOnboardingVisible) vm.CloseOnboarding();
        vm.SelectedNav = vm.NavItems[0];
        await Task.Delay(350);

        var button = this.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => control.Name == "HomeToolScan" && control.IsVisible);
        var text = this.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(control => control.IsVisible && IsInside(control, PageHost));
        var card = this.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(control => control.Name == "RecentDisksCard" && control.IsVisible);
        if (button is null || text is null || card is null ||
            !ReferenceEquals(ResolveFeedbackTarget(text), text) ||
            !ReferenceEquals(ResolveFeedbackTarget(card), card))
            throw new InvalidOperationException("Feedback picker smoke targets were not realized as visual elements.");

        vm.Feedback.TogglePickerCommand.Execute(null);
        vm.Feedback.ToggleSelection(button, DescribeFeedbackTarget(button));
        vm.Feedback.ToggleSelection(text, DescribeFeedbackTarget(text));
        vm.Feedback.ToggleSelection(card, DescribeFeedbackTarget(card));
        await Task.Delay(250);

        if (FeedbackPanel?.IsVisible != true ||
            FeedbackPanel.GetVisualDescendants().OfType<TextBox>().Count() < 5)
            throw new InvalidOperationException("Feedback picker panel item template was not realized.");

        vm.Feedback.ClosePanelCommand.Execute(null);
    }
}

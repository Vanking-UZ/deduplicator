using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Deduplicator.App.Infrastructure;

namespace Deduplicator.App.Controls;

/// <summary>
/// Фирменный анимированный логотип — «объёмная» стопка квадратов-дубликатов.
/// Техника: Rotate3DTransform (есть в Avalonia 11.2) + Animation.RunAsync по свойствам
/// трансформов из code-behind. Styles-анимации не подходят: KeyFrame-сеттеры не умеют
/// целиться в под-свойства RenderTransform, а строковый синтаксис TransformOperations
/// не знает rotate3d(...). Анимируются только композиторные свойства (transform/opacity);
/// «пульс тени» — слой-свечение GlowLayer с анимацией Opacity, без анимации BoxShadow.
/// Жизненный цикл: анимации стартуют в видимом дереве и останавливаются при
/// DetachedFromVisualTree / IsVisible=false. Скрытие родителя (оверлея) само по себе
/// не меняет IsVisible контрола, поэтому в оверлеях IsVisible логотипа привязывают
/// к тому же флагу, что и у оверлея (см. BusyOverlay/ExplorerView/ScanView).
/// </summary>
public partial class AnimatedLogo : UserControl
{
    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<AnimatedLogo, double>(nameof(Size), 96d);

    /// <summary>Сторона логотипа в пикселях (типовые: 48 / 64 / 96 / 160).</summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private readonly Border? _glow;
    private readonly Border? _front;
    private readonly Border? _back;
    private readonly Grid? _pulseRoot;
    private CancellationTokenSource? _cts;

    public AnimatedLogo()
    {
        AvaloniaXamlLoader.Load(this);
        _glow = this.FindControl<Border>("GlowLayer");
        _front = this.FindControl<Border>("FrontLayer");
        _back = this.FindControl<Border>("BackLayer");
        _pulseRoot = this.FindControl<Grid>("PulseRoot");
        // Depth задаёт перспективу (расстояние до наблюдателя в единицах холста 100×100):
        // меньше — драматичнее, больше — площе. 220 ≈ мягкий «объём» без искажений.
        if (_front is not null) _front.RenderTransform = new Rotate3DTransform { Depth = 220 };
        if (_back is not null) _back.RenderTransform = new Rotate3DTransform { Depth = 220 };
        if (_pulseRoot is not null) _pulseRoot.RenderTransform = new ScaleTransform();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MotionPreferences.Changed += OnMotionPreferencesChanged;
        UpdateAnimationState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        MotionPreferences.Changed -= OnMotionPreferencesChanged;
        base.OnDetachedFromVisualTree(e);
        StopAnimations();
    }

    private void OnMotionPreferencesChanged()
    {
        StopAnimations();
        UpdateAnimationState();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) UpdateAnimationState();
    }

    private void UpdateAnimationState()
    {
        if (IsVisible && VisualRoot is not null) StartAnimations();
        else StopAnimations();
    }

    private void StartAnimations()
    {
        if (_cts is not null) return; // уже крутится
        if (!MotionPreferences.AnimationsEnabled)
        {
            ApplyStaticPose();
            return;
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Верхний квадрат: полный оборот вокруг Y. Ease-in-out даёт «переворот карточки»:
        // медленный разгон, быстрый проход через ребро, мягкая остановка.
        // ВАЖНО: цель анимации — Visual, а не сам Transform: сеттеры на свойствах
        // Transform-типов обслуживает TransformAnimator, который кастует цель к Visual
        // и находит подходящий трансформ в его RenderTransform (иначе InvalidCastException).
        if (_front is not null)
            Run(new Animation
            {
                Duration = TimeSpan.FromSeconds(3.6),
                IterationCount = IterationCount.Infinite,
                Easing = new SineEaseInOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Rotate3DTransform.AngleYProperty, 0d) } },
                    new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Rotate3DTransform.AngleYProperty, 360d) } }
                }
            }, _front, ct);

        // Нижний квадрат: лёгкое противофазное покачивание (полупериод верхнего).
        if (_back is not null)
            Run(new Animation
            {
                Duration = TimeSpan.FromSeconds(1.8),
                IterationCount = IterationCount.Infinite,
                PlaybackDirection = PlaybackDirection.Alternate,
                Easing = new SineEaseInOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Rotate3DTransform.AngleYProperty, 14d) } },
                    new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Rotate3DTransform.AngleYProperty, -14d) } }
                }
            }, _back, ct);

        // Дыхание всей стопки: масштаб 0.96→1.04.
        if (_pulseRoot is not null)
            Run(new Animation
            {
                Duration = TimeSpan.FromSeconds(1.9),
                IterationCount = IterationCount.Infinite,
                PlaybackDirection = PlaybackDirection.Alternate,
                Easing = new SineEaseInOut(),
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0d),
                        Setters =
                        {
                            new Setter(ScaleTransform.ScaleXProperty, 0.96),
                            new Setter(ScaleTransform.ScaleYProperty, 0.96)
                        }
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1d),
                        Setters =
                        {
                            new Setter(ScaleTransform.ScaleXProperty, 1.04),
                            new Setter(ScaleTransform.ScaleYProperty, 1.04)
                        }
                    }
                }
            }, _pulseRoot, ct);

        // Свечение дышит синхронно с масштабом (эффект пульсирующей тени).
        if (_glow is not null)
            Run(new Animation
            {
                Duration = TimeSpan.FromSeconds(1.9),
                IterationCount = IterationCount.Infinite,
                PlaybackDirection = PlaybackDirection.Alternate,
                Easing = new SineEaseInOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(OpacityProperty, 0.45) } },
                    new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(OpacityProperty, 1d) } }
                }
            }, _glow, ct);
    }

    private void StopAnimations()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null; // при отмене Avalonia возвращает свойствам базовые значения
    }

    private void ApplyStaticPose()
    {
        if (_front?.RenderTransform is Rotate3DTransform front) front.AngleY = 0;
        if (_back?.RenderTransform is Rotate3DTransform back) back.AngleY = 0;
        if (_pulseRoot?.RenderTransform is ScaleTransform scale)
        {
            scale.ScaleX = 1;
            scale.ScaleY = 1;
        }
        if (_glow is not null) _glow.Opacity = 0.62;
    }

    /// <summary>Бесконечная анимация: задача завершается только отменой — глотаем и её, и сбои.
    /// Декоративная анимация не имеет права ронять приложение, поэтому и синхронный сбой Apply
    /// только логируется.</summary>
    private static void Run(Animation animation, Animatable target, CancellationToken ct)
    {
        try
        {
            _ = animation.RunAsync(target, ct)
                .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            Deduplicator.Core.Util.AppLog.Error("AnimatedLogo", ex);
        }
    }
}

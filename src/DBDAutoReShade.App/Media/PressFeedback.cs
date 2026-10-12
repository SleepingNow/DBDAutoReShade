using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DBDAutoReShade.App.Media;

/// <summary>
/// Press feedback: a pressable element scales to <c>Scale</c> while it is held (0.97 for buttons, 0.98 for wide
/// rows), 140 ms ease-out each way, about its centre. The focus visual is an adorner, so it is unaffected.
/// Honours <see cref="MotionPolicy"/>: no scale when animations are off or under reduced motion.
/// Usage (the Campfire styles set it): <c>media:PressFeedback.Scale="0.97"</c>.
/// </summary>
public static class PressFeedback
{
    /// <summary>The pressed scale; NaN (the default) disables the feedback.</summary>
    public static readonly DependencyProperty ScaleProperty = DependencyProperty.RegisterAttached(
        "Scale",
        typeof(double),
        typeof(PressFeedback),
        new PropertyMetadata(double.NaN, OnScaleChanged));

    private static readonly DependencyProperty TransformProperty = DependencyProperty.RegisterAttached(
        "Transform",
        typeof(ScaleTransform),
        typeof(PressFeedback),
        new PropertyMetadata(null));

    private static readonly Duration PressDuration = new(TimeSpan.FromMilliseconds(140));

    /// <summary>Gets the pressed scale.</summary>
    public static double GetScale(DependencyObject element) => (double)element.GetValue(ScaleProperty);

    /// <summary>Sets the pressed scale (NaN disables).</summary>
    public static void SetScale(DependencyObject element, double value) => element.SetValue(ScaleProperty, value);

    /// <summary>Presses <paramref name="element"/> down (true) or releases it (false), as the pointer does.</summary>
    public static void Animate(UIElement element, bool pressed)
    {
        double scale = GetScale(element);
        if (double.IsNaN(scale))
        {
            return;
        }

        ScaleTransform transform = EnsureTransform(element);
        MotionPolicy policy = MotionPolicy.For(element);
        double target = pressed ? scale : 1.0;
        if (!policy.ShouldAnimate || policy.ReducedMotion)
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            transform.ScaleX = 1;
            transform.ScaleY = 1;
            return;
        }

        double from = transform.ScaleX;
        transform.ScaleX = target;
        transform.ScaleY = target;
        var anim = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new SplineDoubleKeyFrame(target, KeyTime.FromTimeSpan(PressDuration.TimeSpan), Ease.Out));
        if (!pressed)
        {
            // Release: once back at rest, the clock is removed (nothing stays attached).
            anim.Completed += (_, _) =>
            {
                if (transform.ScaleX == 1.0)
                {
                    transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                }
            };
        }

        anim.Freeze();
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, anim, HandoffBehavior.SnapshotAndReplace);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private static ScaleTransform EnsureTransform(UIElement element)
    {
        if (element.GetValue(TransformProperty) is ScaleTransform existing)
        {
            return existing;
        }

        var scale = new ScaleTransform(1, 1);
        if (element.RenderTransform is null || element.RenderTransform == Transform.Identity)
        {
            element.RenderTransform = scale;
        }
        else
        {
            var group = new TransformGroup();
            group.Children.Add(element.RenderTransform);
            group.Children.Add(scale);
            element.RenderTransform = group;
        }

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.SetValue(TransformProperty, scale);
        return scale;
    }

    private static void OnScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        bool wasOn = e.OldValue is double o && !double.IsNaN(o);
        bool isOn = e.NewValue is double n && !double.IsNaN(n);
        if (wasOn == isOn)
        {
            return;
        }

        if (isOn)
        {
            element.PreviewMouseLeftButtonDown += OnDown;
            element.PreviewMouseLeftButtonUp += OnUp;
            element.MouseLeave += OnLeave;
            element.LostMouseCapture += OnLeave;
            element.PreviewKeyDown += OnKeyDown;
            element.PreviewKeyUp += OnKeyUp;
            element.LostKeyboardFocus += OnLostFocus;
        }
        else
        {
            element.PreviewMouseLeftButtonDown -= OnDown;
            element.PreviewMouseLeftButtonUp -= OnUp;
            element.MouseLeave -= OnLeave;
            element.LostMouseCapture -= OnLeave;
            element.PreviewKeyDown -= OnKeyDown;
            element.PreviewKeyUp -= OnKeyUp;
            element.LostKeyboardFocus -= OnLostFocus;
        }
    }

    private static bool IsPressable(UIElement element) => element.IsEnabled;

    private static void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement element && IsPressable(element))
        {
            Animate(element, pressed: true);
        }
    }

    private static void OnUp(object sender, MouseButtonEventArgs e) => Release(sender);

    private static void OnLeave(object sender, MouseEventArgs e) => Release(sender);

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && !e.IsRepeat && sender is UIElement element && IsPressable(element) && ReferenceEquals(e.OriginalSource, sender))
        {
            Animate(element, pressed: true);
        }
    }

    private static void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            Release(sender);
        }
    }

    private static void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e) => Release(sender);

    private static void Release(object sender)
    {
        if (sender is UIElement element && element.GetValue(TransformProperty) is ScaleTransform { ScaleX: not 1.0 })
        {
            Animate(element, pressed: false);
        }
    }
}

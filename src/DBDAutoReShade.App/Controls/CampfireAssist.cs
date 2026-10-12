using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DBDAutoReShade.App.Controls;

/// <summary>
/// Attached properties the Campfire control templates read (<c>Themes/Campfire.xaml</c>):
/// an icon geometry (nav items, title-bar glyphs, a field's leading icon), a text field's placeholder and
/// invalid state, and the red attention dot on a nav item. Two behaviours for assistive technology and the keyboard:
/// <see cref="LiveRegionProperty"/> (a text that is announced when it changes) and <see cref="FocusOnLeaveProperty"/>
/// (where the keyboard focus goes when the focused control hides or disables itself).
/// </summary>
public static class CampfireAssist
{
    /// <summary>A stroke icon (24 x 24 design units for nav items, 10 x 10 for title-bar glyphs, 16 x 16 for a field's leading icon).</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(CampfireAssist), new FrameworkPropertyMetadata(null));

    /// <summary>Placeholder text shown in an empty text box.</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(CampfireAssist), new FrameworkPropertyMetadata(string.Empty));

    /// <summary>Marks a field invalid (Entity-red border), e.g. a manifest URL that is not https.</summary>
    public static readonly DependencyProperty IsInvalidProperty = DependencyProperty.RegisterAttached(
        "IsInvalid", typeof(bool), typeof(CampfireAssist), new FrameworkPropertyMetadata(false));

    /// <summary>Shows the red attention dot after a nav item's label (restart needed).</summary>
    public static readonly DependencyProperty ShowAttentionProperty = DependencyProperty.RegisterAttached(
        "ShowAttention", typeof(bool), typeof(CampfireAssist), new FrameworkPropertyMetadata(false));

    /// <summary>
    /// Makes a text (a TextBlock, a <see cref="CrossfadeText"/>) a live region that speaks: it sets
    /// <c>AutomationProperties.LiveSetting</c> and announces the text (<see cref="CampfireAutomation.AnnounceLiveRegion"/>)
    /// whenever it changes or the element itself is shown, while it is visible and not empty. WPF never announces a live
    /// region by itself, so a note such as "Hotkey set to F7." or "Could not save settings: …" would otherwise appear
    /// silently. (Showing the page that holds it announces nothing: only the text's own changes do.)
    /// </summary>
    public static readonly DependencyProperty LiveRegionProperty = DependencyProperty.RegisterAttached(
        "LiveRegion", typeof(AutomationLiveSetting), typeof(CampfireAssist), new FrameworkPropertyMetadata(AutomationLiveSetting.Off, OnLiveRegionChanged));

    /// <summary>
    /// Where the keyboard focus goes when this control, holding it, is disabled or hidden by its own action (e.g.
    /// "Return to automatic" disables itself: the focus goes to the realm list). Without it WPF drops the focus on the
    /// window and a keyboard user has to Tab from the top again.
    /// </summary>
    public static readonly DependencyProperty FocusOnLeaveProperty = DependencyProperty.RegisterAttached(
        "FocusOnLeave", typeof(UIElement), typeof(CampfireAssist), new FrameworkPropertyMetadata(null, OnFocusOnLeaveChanged));

    private static readonly DependencyProperty LiveRegionWatchProperty = DependencyProperty.RegisterAttached(
        "LiveRegionWatch", typeof(LiveRegionWatch), typeof(CampfireAssist), new FrameworkPropertyMetadata(null));

    /// <summary>Gets the icon.</summary>
    public static Geometry? GetIcon(DependencyObject element) => (Geometry?)element.GetValue(IconProperty);

    /// <summary>Sets the icon.</summary>
    public static void SetIcon(DependencyObject element, Geometry? value) => element.SetValue(IconProperty, value);

    /// <summary>Gets the placeholder.</summary>
    public static string GetPlaceholder(DependencyObject element) => (string)element.GetValue(PlaceholderProperty);

    /// <summary>Sets the placeholder.</summary>
    public static void SetPlaceholder(DependencyObject element, string value) => element.SetValue(PlaceholderProperty, value);

    /// <summary>Gets the invalid flag.</summary>
    public static bool GetIsInvalid(DependencyObject element) => (bool)element.GetValue(IsInvalidProperty);

    /// <summary>Sets the invalid flag.</summary>
    public static void SetIsInvalid(DependencyObject element, bool value) => element.SetValue(IsInvalidProperty, value);

    /// <summary>Gets the attention flag.</summary>
    public static bool GetShowAttention(DependencyObject element) => (bool)element.GetValue(ShowAttentionProperty);

    /// <summary>Sets the attention flag.</summary>
    public static void SetShowAttention(DependencyObject element, bool value) => element.SetValue(ShowAttentionProperty, value);

    /// <summary>Gets the live region setting.</summary>
    public static AutomationLiveSetting GetLiveRegion(DependencyObject element) => (AutomationLiveSetting)element.GetValue(LiveRegionProperty);

    /// <summary>Sets the live region setting (Polite or Assertive; Off stops announcing).</summary>
    public static void SetLiveRegion(DependencyObject element, AutomationLiveSetting value) => element.SetValue(LiveRegionProperty, value);

    /// <summary>Gets the focus target.</summary>
    public static UIElement? GetFocusOnLeave(DependencyObject element) => (UIElement?)element.GetValue(FocusOnLeaveProperty);

    /// <summary>Sets the focus target.</summary>
    public static void SetFocusOnLeave(DependencyObject element, UIElement? value) => element.SetValue(FocusOnLeaveProperty, value);

    /// <summary>
    /// Moves the keyboard focus to <paramref name="target"/> when it can take it. In a window that is not active (the
    /// keyboard is elsewhere) its focus scope remembers it, so the focus is there when the window is activated again.
    /// Returns true when the target is (or will be) focused.
    /// </summary>
    public static bool MoveFocusTo(UIElement? target)
    {
        if (target is null || !target.IsVisible || !target.IsEnabled || !target.Focusable)
        {
            return false;
        }

        if (target.Focus())
        {
            return true;
        }

        DependencyObject? scope = FocusManager.GetFocusScope(target);
        if (scope is null)
        {
            return false;
        }

        FocusManager.SetFocusedElement(scope, target);
        return true;
    }

    /// <summary>The element holds the keyboard focus, or its focus scope's logical focus (a window that is not active).</summary>
    public static bool HoldsFocus(UIElement element)
    {
        if (element.IsKeyboardFocused)
        {
            return true;
        }

        DependencyObject? scope = FocusManager.GetFocusScope(element);
        return scope is not null && ReferenceEquals(FocusManager.GetFocusedElement(scope), element);
    }

    /// <summary>The keyboard focus, or the logical focus of <paramref name="element"/>'s focus scope, is inside it.</summary>
    public static bool HoldsFocusWithin(UIElement element)
    {
        if (element.IsKeyboardFocusWithin)
        {
            return true;
        }

        DependencyObject? scope = FocusManager.GetFocusScope(element);
        return scope is not null && FocusManager.GetFocusedElement(scope) is DependencyObject focused &&
               (ReferenceEquals(focused, element) || (focused is Visual or System.Windows.Media.Media3D.Visual3D && element.IsAncestorOf(focused)));
    }

    private static void OnFocusOnLeaveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.IsEnabledChanged -= OnLeaving;
        element.IsVisibleChanged -= OnLeaving;
        if (e.NewValue is not null)
        {
            element.IsEnabledChanged += OnLeaving;
            element.IsVisibleChanged += OnLeaving;
        }
    }

    // the control is disabled or hidden: if it held the focus, the focus goes to its target once layout has caught up
    private static void OnLeaving(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not UIElement element || e.NewValue is not false || !HoldsFocus(element))
        {
            return;
        }

        element.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!(element.IsVisible && element.IsEnabled))
            {
                MoveFocusTo(GetFocusOnLeave(element));
            }
        }));
    }

    private static void OnLiveRegionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        var setting = (AutomationLiveSetting)e.NewValue;
        AutomationProperties.SetLiveSetting(element, setting);
        var watch = (LiveRegionWatch?)element.GetValue(LiveRegionWatchProperty);
        if (setting == AutomationLiveSetting.Off)
        {
            watch?.Detach();
            element.ClearValue(LiveRegionWatchProperty);
        }
        else if (watch is null)
        {
            element.SetValue(LiveRegionWatchProperty, new LiveRegionWatch(element));
        }
    }

    /// <summary>
    /// Follows a live region's text and its own visibility (not its page's) while it is loaded, and announces it once
    /// the change has been laid out (several changes in one pass are one announcement).
    /// </summary>
    private sealed class LiveRegionWatch
    {
        private readonly FrameworkElement _element;
        private readonly DependencyPropertyDescriptor? _text;
        private readonly DependencyPropertyDescriptor _visibility;
        private bool _hooked;
        private bool _pending;

        public LiveRegionWatch(FrameworkElement element)
        {
            _element = element;
            DependencyProperty? text = element switch
            {
                TextBlock => TextBlock.TextProperty,
                CrossfadeText => CrossfadeText.TextProperty,
                TextBox => TextBox.TextProperty,
                _ => null,
            };
            _text = text is null ? null : DependencyPropertyDescriptor.FromProperty(text, element.GetType());
            _visibility = DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, element.GetType());
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            Hook(); // (a text that is never loaded is unhooked with it: the descriptors hold it only while hooked)
        }

        public void Detach()
        {
            _element.Loaded -= OnLoaded;
            _element.Unloaded -= OnUnloaded;
            Unhook();
        }

        private void OnLoaded(object sender, RoutedEventArgs e) => Hook();

        private void OnUnloaded(object sender, RoutedEventArgs e) => Unhook();

        private void Hook()
        {
            if (_hooked)
            {
                return;
            }

            _hooked = true;
            _text?.AddValueChanged(_element, OnChanged);
            _visibility.AddValueChanged(_element, OnChanged);
        }

        private void Unhook()
        {
            if (!_hooked)
            {
                return;
            }

            _hooked = false;
            _text?.RemoveValueChanged(_element, OnChanged);
            _visibility.RemoveValueChanged(_element, OnChanged);
        }

        private void OnChanged(object? sender, EventArgs e)
        {
            if (_pending)
            {
                return;
            }

            _pending = true;
            _element.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Announce));
        }

        private void Announce()
        {
            _pending = false;
            if (_element.IsVisible && !string.IsNullOrWhiteSpace(Text()))
            {
                CampfireAutomation.AnnounceLiveRegion(_element);
            }
        }

        private string? Text() => _element switch
        {
            TextBlock block => block.Text,
            CrossfadeText crossfade => crossfade.Text,
            TextBox box => box.Text,
            _ => AutomationProperties.GetName(_element),
        };
    }
}

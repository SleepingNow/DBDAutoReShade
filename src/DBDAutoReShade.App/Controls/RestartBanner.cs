using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.App.Controls;

/// <summary>
/// The restart-needed banner (Entity red): a hook icon, the message and an action button ("Show in Presets").
/// Setting <see cref="IsOpen"/> true drops it in (fade + 8 px fall, 300 ms ease-out); false collapses it.
/// Pair it with <c>EntityLegs.RimRun()</c> for the full moment. It is an assertive live region named by its
/// <see cref="Text"/>: opening it, its message changing while it is open, and <see cref="DropIn"/> announce the message
/// to assistive technology, once the change has been laid out (opening it, a new message and the window's drop-in in one
/// pass are one announcement).
/// </summary>
public sealed class RestartBanner : UserControl
{
    /// <summary>The message, e.g. "Restart Dead by Daylight so ReShade loads the new shortcut for Haddonfield."</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(RestartBanner), new PropertyMetadata(string.Empty, (d, e) => ((RestartBanner)d).OnTextChanged((string)e.NewValue)));

    /// <summary>The action button's label (default "Show in Presets"); empty hides the button.</summary>
    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
        nameof(ActionText), typeof(string), typeof(RestartBanner), new PropertyMetadata("Show in Presets", (d, _) => ((RestartBanner)d).UpdateAction()));

    /// <summary>The action button's command.</summary>
    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(
        nameof(ActionCommand), typeof(ICommand), typeof(RestartBanner), new PropertyMetadata(null, (d, e) => ((RestartBanner)d)._action.Command = (ICommand?)e.NewValue));

    /// <summary>Shown (drops in when it opens).</summary>
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(RestartBanner), new PropertyMetadata(false, (d, _) => ((RestartBanner)d).UpdateOpen(animate: ((RestartBanner)d).IsLoaded)));

    private readonly TextBlock _text;
    private readonly Button _action;
    private readonly Grid _root;
    private readonly TranslateTransform _drop = new();
    private readonly MotionPolicyWatch _watch;
    private MotionMoment? _moment;
    private bool _announcePending;

    /// <summary>Creates a closed banner.</summary>
    public RestartBanner()
    {
        Focusable = false;
        IsTabStop = false;
        // box-shadow 0 0 26px rgba(176,24,45,.22): a blurred red outline behind the banner
        var glow = new Border
        {
            BorderBrush = CampfirePalette.Brush(0.55, CampfirePalette.EntityColor),
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(2),
            IsHitTestVisible = false,
            Effect = Frozen(new BlurEffect { Radius = 26 }),
        };
        var chrome = new Border
        {
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(4, 1, 1, 1),
            BorderBrush = CampfirePalette.Brush(0.9, CampfirePalette.EntityColor),
            Background = CampfirePalette.Linear(new Point(0, 0), new Point(1, 0),
                (0, CampfirePalette.Argb(0.44, CampfirePalette.EntityColor)), (1, CampfirePalette.Argb(0.14, CampfirePalette.EntityColor))),
            Padding = new Thickness(12, 10, 12, 10),
            SnapsToDevicePixels = true,
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Viewbox
        {
            Width = 24,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Canvas
            {
                Width = 24,
                Height = 24,
                Children =
                {
                    new Path
                    {
                        Data = SkillCheckMath.Frozen("M12,1.5 V5.5 M10.4,7.1 A1.6,1.6 0 1 1 13.6,7.1 A1.6,1.6 0 1 1 10.4,7.1 Z M12,8.7 V14.8 A4.2,4.2 0 0 1 3.6,14.8 V12 L6,14"),
                        Stroke = CampfirePalette.Bone,
                        StrokeThickness = 1.8,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                    },
                },
            },
        };
        _text = new TextBlock
        {
            Margin = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = CampfireFonts.Body,
            FontWeight = FontWeights.Medium,
            FontSize = 15,
            Foreground = CampfirePalette.Bone,
            TextWrapping = TextWrapping.Wrap,
        };
        _action = new Button
        {
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = 30,
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = 13,
            BorderBrush = CampfirePalette.Brush(0.5, CampfirePalette.BoneColor),
        };
        _action.SetResourceReference(StyleProperty, "CfButtonSecondarySmall");
        _action.Click += (_, _) => ActionClick?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(_text, 1);
        Grid.SetColumn(_action, 2);
        row.Children.Add(icon);
        row.Children.Add(_text);
        row.Children.Add(_action);
        chrome.Child = row;
        _root = new Grid { RenderTransform = _drop };
        _root.Children.Add(glow);
        _root.Children.Add(chrome);
        Content = _root;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Assertive);
        _watch = new MotionPolicyWatch(this, () => { });
        UpdateAction();
        UpdateOpen(animate: false);
    }

    /// <summary>Raised when the action button is clicked (in addition to <see cref="ActionCommand"/>).</summary>
    public event EventHandler? ActionClick;

    /// <summary>The message.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The action button's label; empty hides it.</summary>
    public string ActionText
    {
        get => (string)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    /// <summary>The action button's command.</summary>
    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    /// <summary>Shown.</summary>
    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>Plays the drop-in again (the banner is already open) and announces the message again.</summary>
    public void DropIn()
    {
        _moment?.Stop();
        _moment = null;
        if (!IsOpen)
        {
            return;
        }

        AnnounceSoon();
        MotionPolicy policy = _watch.Policy;
        if (!policy.ShouldAnimate)
        {
            return;
        }

        var moment = new MotionMoment(this, "RestartBanner.drop");
        moment.Add(_root, OpacityProperty, Anim.FromTo(0, 1, policy.ReducedMotion ? 200 : 300));
        if (!policy.ReducedMotion)
        {
            moment.Add(_drop, TranslateTransform.YProperty, Anim.FromTo(-8, 0, 300));
        }

        _moment = moment;
        moment.Begin();
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private void UpdateAction()
    {
        _action.Content = ActionText;
        _action.Visibility = string.IsNullOrEmpty(ActionText) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnTextChanged(string text)
    {
        _text.Text = text;
        AutomationProperties.SetName(this, text);
        if (IsOpen)
        {
            AnnounceSoon(); // (a new problem in a banner already open is told too)
        }
    }

    /// <summary>Announces the message once the current changes have been laid out (one announcement per pass), if still open.</summary>
    private void AnnounceSoon()
    {
        if (_announcePending)
        {
            return;
        }

        _announcePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _announcePending = false;
            if (IsOpen && !string.IsNullOrEmpty(Text))
            {
                CampfireAutomation.AnnounceLiveRegion(this);
            }
        }));
    }

    private void UpdateOpen(bool animate)
    {
        Visibility = IsOpen ? Visibility.Visible : Visibility.Collapsed;
        if (IsOpen && animate)
        {
            DropIn();
        }
        else if (IsOpen)
        {
            AnnounceSoon();
        }
        else
        {
            _moment?.Stop();
            _moment = null;
        }
    }
}

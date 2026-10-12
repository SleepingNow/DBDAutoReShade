using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.ViewModels;

namespace DBDAutoReShade.App.Views;

/// <summary>
/// The Settings screen. The volume slider's label follows every tick, but the volume is saved only when the slider
/// settles (the thumb or a click released, a key released, focus lost), not on every tick. The groups are the
/// prototype's 800 px wide (narrower only when the window is); under 1180 px the heading column narrows (200 → 170 px)
/// and the paddings tighten, as in the prototype. The inline reset confirmation keeps keyboard focus: opening it
/// focuses its Reset button, closing it returns focus to "Reset settings".
/// </summary>
public partial class SettingsView : UserControl
{
    /// <summary>The prototype's group width (200 + 40 + 560).</summary>
    public const double GroupsWidth = 800;

    private MainViewModel? _viewModel;

    /// <summary>Creates the view (the window sets its data context to the main view model).</summary>
    public SettingsView()
    {
        CampfireTheme.EnsureFor(this);
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutMetrics();
        Loaded += (_, _) => UpdateLayoutMetrics();
        Scroller.SizeChanged += (_, _) => UpdateLayoutMetrics();
        Scroller.ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0)
            {
                UpdateLayoutMetrics(); // the scroll bar came or went
            }
        };
        DataContextChanged += (_, e) => Attach(e.NewValue as MainViewModel);

        // the volume is saved when the slider settles
        VolumeSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => CommitVolume()));
        VolumeSlider.PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(new Action(CommitVolume));
        VolumeSlider.KeyUp += (_, _) => CommitVolume();
        VolumeSlider.LostKeyboardFocus += (_, _) => CommitVolume();
    }

    /// <summary>Saves the slider's volume if it changed (also called when the window closes).</summary>
    public void CommitVolume() => (DataContext as MainViewModel)?.CommitSoundVolume();

    /// <summary>
    /// Puts the keyboard focus on the page's first control, the detection hotkey's "Change hotkey" (the Global preset
    /// hotkey's when only that one has a problem: the hotkey banner's "Open Settings" lands on the key to change).
    /// </summary>
    public void FocusEntry() =>
        CampfireAssist.MoveFocusTo(_viewModel is { DetectionHotkeyProblem: null, GlobalHotkeyProblem: not null } ? GlobalHotkeyButton : HotkeyButton);

    private void Attach(MainViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelChanged;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsResetConfirming) || _viewModel is null)
        {
            return;
        }

        if (_viewModel.IsResetConfirming)
        {
            // the confirmation replaces the button that opened it: its primary button takes the focus
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ConfirmResetButton.Focus()));
        }
        else if (ResetConfirmBox.IsKeyboardFocusWithin)
        {
            // Reset or Cancel was pressed and their box closes: focus goes back to "Reset settings", not to the window
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ResetButton.Focus()));
        }
    }

    private void UpdateLayoutMetrics()
    {
        Size viewport = CampfireType.GetViewport(this);
        if (viewport.Width <= 0)
        {
            return;
        }

        bool small = viewport.Width < 1180;
        Resources["LabelColumnWidth"] = new GridLength(small ? 170 : 200);
        Resources["GapColumnWidth"] = new GridLength(small ? 28 : 40);
        Groups.Margin = small ? new Thickness(32, 10, 32, 40) : new Thickness(44, 14, 48, 48);

        // the groups are 800 px wide, so the rules and path boxes span the design width whatever their content
        double available = (Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : ActualWidth) - Groups.Margin.Left - Groups.Margin.Right;
        if (available > 0)
        {
            double width = Math.Min(GroupsWidth, Math.Floor(available));
            if (double.IsNaN(Groups.Width) || Math.Abs(Groups.Width - width) > 0.5)
            {
                Groups.Width = width;
            }
        }
    }
}

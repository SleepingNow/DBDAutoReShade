using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DBDAutoReShade.App.Infrastructure;
using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.ViewModels;

namespace DBDAutoReShade.App;

/// <summary>
/// First-run setup wizard (Campfire design). Realm presets stay optional. Opened centred, it is fitted to its
/// monitor's work area (its steps scroll), so the title bar and the Finish bar are always on the screen.
/// </summary>
public partial class SetupWizardWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IDisposable _policyAttachment;

    /// <param name="viewModel">The main view model.</param>
    /// <param name="policy">
    /// The motion policy for the wizard's atmosphere (default: the process-wide one). It follows this window while the
    /// wizard is open: minimized, behind another window (the game, a file dialog) or hidden, its fog stops drifting.
    /// </param>
    public SetupWizardWindow(MainViewModel viewModel, MotionPolicy? policy = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        CampfireTheme.EnsureFor(this);
        InitializeComponent();
        if (policy is not null)
        {
            MotionPolicy.SetPolicy(this, policy);
        }

        Policy = policy ?? MotionPolicy.Current;
        _policyAttachment = Policy.AttachWindow(this); // (released when the wizard closes; the main window attaches next)
        DataContext = _viewModel;
        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += (_, _) => WindowPlacement.FitCenteredWindow(this);

        // Capture mode pauses both global hotkeys: never leave them paused once the
        // window can't receive the key any more.
        Deactivated += (_, _) => _viewModel.CancelHotkeyCapture();
        Closed += (_, _) =>
        {
            _viewModel.CancelHotkeyCapture();
            _policyAttachment.Dispose();
        };
        Loaded += (_, _) => FirstBrowseButton.Focus(); // the first step takes the keyboard (not the window buttons)
    }

    /// <summary>The motion policy the wizard follows (and that follows it while it is open).</summary>
    public MotionPolicy Policy { get; }

    /// <summary>While a hotkey is captured, keys go to the view model (Tab still moves focus; see <see cref="MainViewModel.HandleCaptureKey"/>).</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool onCaptureButton = Keyboard.FocusedElement is ButtonBase button && _viewModel.IsCaptureCommand(button.Command);
        if (_viewModel.HandleCaptureKey(key, Keyboard.Modifiers, onCaptureButton))
        {
            e.Handled = true;
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnFinish(object sender, RoutedEventArgs e)
    {
        try
        {
            DialogResult = true; // closes a dialog
        }
        catch (InvalidOperationException)
        {
            // not shown as a dialog (tools, tests)
        }

        Close();
    }
}

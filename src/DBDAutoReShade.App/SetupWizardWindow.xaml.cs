using System.Windows;
using System.Windows.Input;
using DBDAutoReShade.App.ViewModels;

namespace DBDAutoReShade.App;

/// <summary>First-run setup wizard. Realm presets stay optional.</summary>
public partial class SetupWizardWindow : Window
{
    private readonly MainViewModel _viewModel;

    public SetupWizardWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
        PreviewKeyDown += OnPreviewKeyDown;

        // Capture mode pauses the global hotkey: never leave it paused once the
        // window can't receive the key any more.
        Deactivated += (_, _) => _viewModel.CancelHotkeyCapture();
        Closed += (_, _) => _viewModel.CancelHotkeyCapture();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.HotkeyCaptureMode)
        {
            return;
        }

        e.Handled = true; // Esc included: the view model treats it as "cancel"
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        _viewModel.CaptureHotkey(key, Keyboard.Modifiers);
    }

    private void OnFinish(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}

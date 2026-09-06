using System.Windows;
using System.Windows.Input;
using DBDAutoReShade.App.ViewModels;

namespace DBDAutoReShade.App;

/// <summary>Main window: Status / Presets / Settings. Closing it exits the app (no tray).</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.HotkeyCaptureMode)
        {
            return;
        }

        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        // Ignore bare modifier presses.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        _viewModel.CaptureHotkey(key, Keyboard.Modifiers);
    }
}

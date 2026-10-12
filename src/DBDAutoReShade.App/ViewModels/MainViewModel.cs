using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DBDAutoReShade.App.Infrastructure;
using DBDAutoReShade.App.Services;
using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.App.ViewModels;

/// <summary>
/// Binds the AppController to the WPF views. All controller events are
/// marshaled to the UI thread here; the controller itself stays UI-agnostic.
/// The view model is split by screen: Status (the state, the plate, the
/// override), Presets (the realm list and the preset panel) and Settings
/// (hotkeys, paths, behaviour, sound, the map database, the ReShade check).
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>The Settings checkbox that restores the Global preset at the match end (its label, quoted by help texts).</summary>
    public const string AutoRestoreLabel = "Restore the Global preset when a match ends";

    /// <summary>Shown when the captured key can't be a detection hotkey.</summary>
    public const string UnsupportedHotkeyMessage =
        "Use F1–F24 (alone or with Ctrl, Alt or Shift), or a letter, a number or a NumPad key " +
        "together with Ctrl, Alt or Shift, for the detection hotkey.";

    /// <summary>Shown when the captured key can't be the Global preset hotkey.</summary>
    public const string UnsupportedGlobalHotkeyMessage =
        "Use F1–F24 (alone or with Ctrl, Alt or Shift), or a letter, a number or a NumPad key " +
        "together with Ctrl, Alt or Shift, for the Global preset hotkey.";

    /// <summary>The help line while a hotkey is being captured.</summary>
    public const string CaptureHelpText =
        "Press the new hotkey: F1–F24, or Ctrl/Alt/Shift + a letter, number or NumPad key. Esc cancels. " +
        "(Both hotkeys are paused meanwhile.)";

    private readonly AppController _controller;
    private readonly Action<string> _showWarning;
    private readonly Dispatcher? _dispatcher;

    private string _dbdStatus = "Not running";
    private bool _dbdRunning;
    private string _lastSwitchTime = "—";
    private bool _lastSwitchFailed;
    private int _refreshQueued;
    private bool _showRestartNotice;
    private string _presetsTabStatus = string.Empty;
    private string _restartNoticeText = string.Empty;
    private string? _bannerError;
    private AppTab _selectedTab = AppTab.Status;

    /// <param name="controller">The application controller.</param>
    /// <param name="showWarning">Shows a warning to the user (default: a message box; tests record it).</param>
    /// <param name="dispatcher">
    /// The UI thread's dispatcher that controller events are marshaled to (default: the Application's, looked up
    /// per event; without either, refreshes run on the thread that raised the event, as tests expect).
    /// </param>
    public MainViewModel(AppController controller, Action<string>? showWarning = null, Dispatcher? dispatcher = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _showWarning = showWarning ?? ShowMessageBox;
        _dispatcher = dispatcher;
        _controller.Changed += (_, _) => MarshalRefresh();
        _controller.MapDetected += (_, e) => OnMapDetected(e);
        Great = new GreatCoordinator(_controller.PlayGreatSound, dispatcher is null ? null : new DispatcherDelayScheduler(dispatcher));

        RealmRows = new ObservableCollection<RealmRow>();
        InitializeStatus();
        InitializePresets();
        InitializeSettings();
    }

    /// <summary>The Great skill check and its sound when a map is found (the main window is its stage).</summary>
    public GreatCoordinator Great { get; }

    /// <summary>The controller (views read static facts such as the database from it).</summary>
    public AppController Controller => _controller;

    /// <summary>The section shown.</summary>
    public AppTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                RaisePropertyChanged(nameof(BackdropMap));
            }
        }
    }

    // ---- Rail ----

    /// <summary>"Running" / "Not running".</summary>
    public string DbdStatus { get => _dbdStatus; private set => SetProperty(ref _dbdStatus, value); }

    /// <summary>Dead by Daylight is running (the rail's ember dot).</summary>
    public bool DbdRunning { get => _dbdRunning; private set => SetProperty(ref _dbdRunning, value); }

    /// <summary>The last switch's time (HH:mm:ss; "HH:mm:ss, failed" when that attempt failed), or "—".</summary>
    public string LastSwitchTime { get => _lastSwitchTime; private set => SetProperty(ref _lastSwitchTime, value); }

    /// <summary>The last switch attempt failed (the rail shows its time in Entity red).</summary>
    public bool LastSwitchFailed { get => _lastSwitchFailed; private set => SetProperty(ref _lastSwitchFailed, value); }

    /// <summary>
    /// The rail hint under the detection hotkey's keycap: how to scan, or that the hotkey is unavailable (it is not
    /// registered: pressing it can't reach the app; the Status banner and Settings say why).
    /// </summary>
    public string RailScanHint => HotkeyUnavailable ? "Scan hotkey unavailable" : $"Press {HotkeyText} on the loading screen to scan";

    // ---- Status ----

    /// <summary>True while some assigned presets can't be switched to (the running ReShade hasn't loaded their shortcuts).</summary>
    public bool ShowRestartNotice { get => _showRestartNotice; private set => SetProperty(ref _showRestartNotice, value); }

    /// <summary>
    /// What the Presets tab's red dot says to assistive technology: "Restart needed" (a DBD restart loads the
    /// shortcuts), "ReShade.ini needed" (it is not set or was not found: a restart alone can't help); empty when off.
    /// </summary>
    public string PresetsTabStatus { get => _presetsTabStatus; private set => SetProperty(ref _presetsTabStatus, value); }

    /// <summary>
    /// The banner: which presets can't be switched to yet and what fixes it (a
    /// DBD restart, or first setting the right ReShade.ini when it is missing).
    /// </summary>
    public string RestartNoticeText { get => _restartNoticeText; private set => SetProperty(ref _restartNoticeText, value); }

    /// <summary>A hotkey problem (the detection hotkey's, then the Global preset hotkey's); null when none.</summary>
    public string? BannerError { get => _bannerError; private set => SetProperty(ref _bannerError, value); }

    /// <summary>How to scan, with the configured hotkey.</summary>
    public string ScanHelpText =>
        $"Press {HotkeyText} on the loading screen to read the map; press it again to stop. " +
        "Scanning stops by itself once the map is read.";

    /// <summary>
    /// When a manual override ends (truthful: the match end clears it only while the match is tracked); it quotes the
    /// Settings checkbox (<see cref="AutoRestoreLabel"/>).
    /// </summary>
    public string OverrideHelpText =>
        $"An override stays until Return to automatic, a new scan ({HotkeyText}) or DBD exit. " +
        $"It also clears at the match end, but only when the app is tracking the match (after a detection with {HotkeyText}) " +
        $"and \"{AutoRestoreLabel}\" is on. \"{GlobalPresetRow.Label}\" forces nothing: it switches to the Global preset now, " +
        "stops a running scan and keeps automatic switching on.";

    /// <summary>The map behind the fog: the Status screen's (detected or forced realm), the Presets hero map, or none.</summary>
    public string? BackdropMap => SelectedTab switch
    {
        AppTab.Status => StatusScreen.BackdropMap,
        AppTab.Presets => PresetsBackdropMap,
        _ => null,
    };

    // ---- Actions ----

    /// <summary>
    /// Pulls the controller's state into the bound properties. Cheap and
    /// churn-free: it runs on every controller event (every scan sample), so
    /// the realm rows and the override list are rebuilt only when the realm
    /// list changes; assignments update the existing rows in place, and a
    /// property is raised only when its value changed. Events that arrive while
    /// a refresh is already queued share it.
    /// </summary>
    public void Refresh()
    {
        var c = _controller;
        DbdRunning = c.DbdRunning;
        DbdStatus = c.DbdRunning ? "Running" : "Not running";
        ActivationAttempt? last = c.LastActivation;
        string lastTime = last is null ? "—" : last.At.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        LastSwitchTime = last is { Success: false } ? lastTime + ", failed" : lastTime;
        LastSwitchFailed = last is { Success: false };
        HotkeyText = c.Config.DetectionHotkey.ToString();
        BannerError = CombineErrors(c.HotkeyError, c.GlobalHotkeyError is null ? null : $"Global preset hotkey: {c.GlobalHotkeyError}");

        BlockedPresetShortcuts blocked = c.BlockedPresetShortcuts; // worked out once per refresh
        ShowRestartNotice = blocked.Notice is not null;
        RestartNoticeText = blocked.Notice ?? string.Empty;
        PresetsTabStatus = blocked.WaitingForReshadeIni.Count > 0 ? "ReShade.ini needed"
            : blocked.NeedingDbdRestart.Count > 0 ? "Restart needed"
            : string.Empty;

        RefreshSettings();
        RefreshPresets(blocked);
        RefreshStatus();
    }

    /// <summary>Shows the Presets tab with the first realm whose shortcut needs a DBD restart (the banner's action).</summary>
    public void ShowRestartInPresets()
    {
        string? first = _controller.NamesNeedingDbdRestart.Concat(_controller.NamesWaitingForReshadeIni).FirstOrDefault();
        if (first is not null)
        {
            PresetRowBase? row = FindRowByDisplayName(first);
            if (row is not null)
            {
                SelectRow(row);
            }
        }

        SelectedTab = AppTab.Presets;
    }

    /// <summary>
    /// Maps a captured WPF key to a hotkey key name understood by
    /// <c>HotkeyParser</c> (F1–F24, A–Z, 0–9, NumPad0–NumPad9); null when unsupported.
    /// </summary>
    public static string? KeyToName(Key key)
    {
        if (key >= Key.F1 && key <= Key.F24)
        {
            return $"F{key - Key.F1 + 1}";
        }

        if (key >= Key.A && key <= Key.Z)
        {
            return key.ToString();
        }

        if (key >= Key.D0 && key <= Key.D9)
        {
            return ((char)('0' + (key - Key.D0))).ToString();
        }

        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            // Distinct from the top-row digits: registers VK_NUMPAD0..9, not VK 0x30..0x39.
            return $"NumPad{key - Key.NumPad0}";
        }

        return null;
    }

    private static string? CombineErrors(string? first, string? second) =>
        first is null ? second : second is null ? first : first + " " + second;

    private static string? PickPresetFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select ReShade preset",
            Filter = "ReShade preset (*.ini)|*.ini"
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private Dispatcher? UiDispatcher => _dispatcher ?? Application.Current?.Dispatcher;

    /// <summary>
    /// A controller event: refresh on the UI thread. From another thread at most one refresh is queued at a time (a
    /// scan raises many events; one refresh after them reads the newest state); one already on the UI thread runs now.
    /// </summary>
    private void MarshalRefresh()
    {
        try
        {
            Dispatcher? dispatcher = UiDispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
                {
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        Volatile.Write(ref _refreshQueued, 0); // events from now on queue the next one
                        Refresh();
                    }));
                }
            }
            else
            {
                Refresh();
            }
        }
        catch
        {
            // UI refresh must never break the controller.
            Volatile.Write(ref _refreshQueued, 0);
        }
    }

    /// <summary>
    /// A confident detection (raised on the scan's thread): the Great, exactly once. On the UI thread at Send
    /// priority, ahead of the refresh the state change queued, so the skill check still spins when it lands.
    /// </summary>
    private void OnMapDetected(MapDetectedEventArgs e)
    {
        try
        {
            Dispatcher? dispatcher = UiDispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => Great.OnMapFound(e.Realm, e.Map)));
            }
            else
            {
                Great.OnMapFound(e.Realm, e.Map);
            }
        }
        catch
        {
            // Never break the controller's scan thread.
        }
    }

    private void ShowWarning(string message)
    {
        try
        {
            _showWarning(message);
        }
        catch
        {
            // Best effort.
        }
    }

    private static void ShowMessageBox(string message) =>
        MessageBox.Show(message, "DBD Auto ReShade", MessageBoxButton.OK, MessageBoxImage.Warning);
}

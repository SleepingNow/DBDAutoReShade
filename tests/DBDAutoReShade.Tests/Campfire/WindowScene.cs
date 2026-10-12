using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DBDAutoReShade.App;
using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.Services;
using DBDAutoReShade.App.ViewModels;
using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Tests.Campfire;

/// <summary>
/// The REAL main window over the real controller (<see cref="ControllerHarness"/>: temp config folder, fake DBD
/// monitor, OCR, capture, keys, hotkeys and sound), driven into one of the prototype's states, shown far off screen
/// (never activated, not in the taskbar). Run it on an STA thread (<see cref="CampfireTestHost.RunSta"/>); the view
/// model marshals the controller's events to that thread's dispatcher, which <see cref="PumpUntil"/> pumps.
/// </summary>
internal sealed class WindowScene : IDisposable
{
    /// <summary>The prototype's states (data.js), in its order (its "matchEnded" is gone: 1.2.1 removed match-end detection).</summary>
    public static readonly string[] States = { "waiting", "ready", "scanning", "waitingFocus", "active", "paused", "restartNeeded" };

    /// <summary>The owner's realm presets (data.js), Haddonfield without one.</summary>
    private static readonly (string Realm, string File)[] Presets =
    {
        ("Autohaven Wreckers", "01.STX.Autohaven.ini"),
        ("Backwater Swamp", "02.STX.Backwater.ini"),
        ("Coldwind Farm", "03.STX.Coldwind Farm.ini"),
        ("Crotus Prenn Asylum", "04.STX.Crotus Prenn.ini"),
        ("Dvarka Deepwood", "22.STX.Toba Landing.ini"),
        ("Forsaken Boneyard", "05.STX.Eyrie of Crows.ini"),
        ("Gideon Meat Plant", "21.STX.The Game.ini"),
        ("Grave of Glenvale", "18.STX.Saloon.ini"),
        ("Hawkins National Laboratory", "09.STX.Hawkins.ini"),
        ("Léry's Memorial Institute", "10.STX.Léry's.ini"),
        ("MacMillan Estate", "11.STX.Macmillan.ini"),
        ("Ormond", "14.STX.Ormond.ini"),
        ("Raccoon City", "16.STX.Raccoon City.ini"),
        ("Red Forest", "17.STX.Red Forest.ini"),
        ("Silent Hill", "12.STX.Midwich.ini"),
        ("Springwood", "20.STX.Springwood.ini"),
        ("The Decimated Borgo", "03.STX.Borgo.ini"),
        ("Withered Isle", "09.STX.Greenville.ini"),
        ("Yamaoka Estate", "23.STX.Yamaoka.ini"),
    };

    private WindowScene(ControllerHarness harness, MainViewModel viewModel, MainWindow window, MotionPolicy policy, string folder, List<string> warnings)
    {
        Warnings = warnings;
        Harness = harness;
        ViewModel = viewModel;
        Window = window;
        Policy = policy;
        PresetFolder = folder;
    }

    public ControllerHarness Harness { get; }

    public MainViewModel ViewModel { get; }

    public MainWindow Window { get; }

    public MotionPolicy Policy { get; }

    /// <summary>Warnings the view model showed (a message box in the app).</summary>
    public List<string> Warnings { get; }

    /// <summary>The preset files' folder.</summary>
    public string PresetFolder { get; }

    /// <summary>A preset file of the owner's setup.</summary>
    public string Preset(string file) => Path.Combine(PresetFolder, file);

    /// <summary>
    /// Builds the controller in <paramref name="state"/>, then the main window (<paramref name="width"/> x
    /// <paramref name="height"/>) over it, shown off screen unless <paramref name="show"/> is false.
    /// </summary>
    public static WindowScene Create(string dir, string state, double width = 1280, double height = 800, bool show = true, MotionPolicy? policy = null)
    {
        Directory.CreateDirectory(dir);
        string folder = Path.Combine(dir, "STEAXS FILTERS PACK - CHARLIE");
        Directory.CreateDirectory(folder);
        string Write(string file)
        {
            string path = Path.Combine(folder, file);
            File.WriteAllText(path, "[General]\nTechniques=A\nTechniqueSorting=A\n");
            return path;
        }

        string universal = Write("00.STX.Universal.ini");
        var realmFiles = Presets.ToDictionary(p => p.Realm, p => Write(p.File), StringComparer.OrdinalIgnoreCase);
        ControllerHarness harness = ControllerHarness.Create(dir, new ControllerHarnessOptions
        {
            VerifyDelayMs = 400,
            DbdRunningAtStart = state != "waiting",
            ProcessId = state == "waiting" ? null : 4242,
            Configure = c =>
            {
                c.DetectionHotkey = new DetectionHotkeyConfig { Key = "F8" };
                c.GlobalPresetPath = universal;
                c.RealmPresets.Clear();
                foreach (KeyValuePair<string, string> preset in realmFiles)
                {
                    c.RealmPresets[preset.Key] = preset.Value;
                }

                File.WriteAllText(c.ReshadeIniPath!, "[GENERAL]\nPresetPath=" + universal + "\n");
            },
        });

        policy ??= new MotionPolicy();
        var warnings = new List<string>();
        var viewModel = new MainViewModel(harness.Controller, warnings.Add, Dispatcher.CurrentDispatcher);
        viewModel.Refresh();
        var window = new MainWindow(viewModel, policy)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -20000,
            Top = -20000,
            Width = width,
            Height = height,
        };
        var scene = new WindowScene(harness, viewModel, window, policy, folder, warnings);
        if (show)
        {
            window.Show();
            CampfireTestHost.Pump();
        }

        scene.Drive(state, realmFiles["MacMillan Estate"]);
        return scene;
    }

    /// <summary>
    /// Tells the window it became (<paramref name="active"/>) or stopped being the foreground window: the WM_ACTIVATE
    /// message WPF reacts to (WA_ACTIVE = 1, WA_INACTIVE = 0), sent to this test's own off-screen window only. Nothing is
    /// focused and no input reaches Windows; the window's motion policy follows Activated / Deactivated as in the app.
    /// </summary>
    public void Activate(bool active) => Activate(Window, active);

    /// <summary>
    /// <see cref="Activate(bool)"/> for another of this test's own off-screen windows (the setup wizard): the
    /// WM_ACTIVATE message only, nothing is focused and no input reaches Windows.
    /// </summary>
    public static void Activate(Window window, bool active)
    {
        SendMessage(new WindowInteropHelper(window).Handle, 0x0006, (IntPtr)(active ? 1 : 0), IntPtr.Zero);
        CampfireTestHost.Pump();
    }

    /// <summary>Pumps the dispatcher until <paramref name="condition"/> holds.</summary>
    public static void PumpUntil(Func<bool> condition, string what, int timeoutMs = 15_000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            CampfireTestHost.Wait(TimeSpan.FromMilliseconds(15));
        }

        CampfireTestHost.Pump();
    }

    /// <summary>Lets every pending moment end (frozen at their end) so the screen shows the state at rest.</summary>
    public void Settle()
    {
        CampfireTestHost.Pump();
        CampfireTestHost.Wait(TimeSpan.FromMilliseconds(60));
        MotionDiagnostics.FreezeAllAt(TimeSpan.FromSeconds(30));
        Window.UpdateLayout();
        CampfireTestHost.Pump();
    }

    public void Dispose()
    {
        try
        {
            Window.Close();
        }
        catch
        {
            // Best effort.
        }

        Harness.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void Drive(string state, string macmillan)
    {
        ControllerHarness h = Harness;
        AppController c = h.Controller;
        switch (state)
        {
            case "waiting":
            case "ready":
                break;

            case "scanning":
                h.Ocr.Text = string.Empty; // nothing readable: the scan keeps reading
                _ = Task.Run(() => c.TriggerDetectionAsync());
                PumpUntil(() => c.DetectionText.Contains("last read", StringComparison.Ordinal) && ViewModel.StatusScreen.State == ScreenState.Scanning, "scanning");
                break;

            case "paused":
                h.Focused = false;
                h.Uncovered = false;
                _ = Task.Run(() => c.TriggerDetectionAsync());
                PumpUntil(() => c.IsScanPaused && ViewModel.StatusScreen.State == ScreenState.Paused, "a paused scan");
                break;

            case "waitingFocus":
                h.Ocr.Text = "COAL TOWER\nMACMILLAN ESTATE";
                h.UnfocusRightAfterTheRead();
                _ = Task.Run(() => c.TriggerDetectionAsync());
                PumpUntil(() => ViewModel.StatusScreen.State == ScreenState.WaitingFocus, "waiting for focus");
                break;

            case "active":
                h.Ocr.Text = "COAL TOWER\nMACMILLAN ESTATE";
                _ = Task.Run(() => c.TriggerDetectionAsync());
                PumpUntil(() => h.States.State == AppState.PresetActive, "the preset switched");
                h.ReshadeSaves(macmillan); // ReShade saves its new preset: the read-back confirms it
                PumpUntil(() => c.IsActivePresetConfirmed && ViewModel.StatusScreen.State == ScreenState.Active, "confirmed by ReShade");
                break;

            case "restartNeeded":
            {
                string file = Path.Combine(PresetFolder, "13.STX.Haddonfield.ini");
                File.WriteAllText(file, "[General]\nTechniques=H\nTechniqueSorting=H\n");
                Assert.Null(c.SetRealmPreset("Haddonfield", file));
                PumpUntil(() => ViewModel.ShowRestartNotice, "the restart notice");
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown state.");
        }

        CampfireTestHost.Pump();
    }
}

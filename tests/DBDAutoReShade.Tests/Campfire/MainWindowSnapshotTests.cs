using System.IO;
using System.Windows;
using DBDAutoReShade.App;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.Services;
using DBDAutoReShade.App.ViewModels;
using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Tests.Campfire;

/// <summary>
/// Opt-in window-level snapshots (set <c>DBD_SNAPSHOTS=&lt;dir&gt;</c>): the REAL main window over the real controller
/// (<see cref="WindowScene"/>) in each of the prototype's states (waiting, ready, scanning, waitingFocus, active,
/// paused, matchEnded, restartNeeded), on each tab, at the design size (1280 x 800) and the minimum (1024 x 680),
/// rendered off screen with RenderTargetBitmap (the window is never activated and sits far off screen), plus the
/// setup wizard. Moments are settled (frozen at their end); the scan's skill check is put 2.4 s into its spin.
/// Compare with the prototype's reference renders. Never runs in a normal test pass.
/// </summary>
public sealed class MainWindowSnapshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-snap-" + Guid.NewGuid().ToString("N"));

    private static string OutDir => Environment.GetEnvironmentVariable("DBD_SNAPSHOTS") ?? Path.GetTempPath();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    [SnapshotFact]
    public void MainWindow_EveryState_EveryTab_1280x800() => RenderAll(1280, 800);

    [SnapshotFact]
    public void MainWindow_EveryState_EveryTab_1024x680() => RenderAll(1024, 680);

    [SnapshotFact]
    public void SetupWizard() => CampfireTestHost.RunSta(() =>
    {
        WindowScene scene = WindowScene.Create(Path.Combine(_dir, "wizard"), "ready", show: false);
        try
        {
            var wizard = new SetupWizardWindow(scene.ViewModel, new MotionPolicy())
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -20000,
                Top = -20000,
            };
            wizard.Show();
            scene.Settle();
            CampfireTestHost.SavePng((FrameworkElement)wizard.Content, Path.Combine(OutDir, "window-wizard.png"));
            wizard.Close();
        }
        finally
        {
            scene.Dispose();
        }
    });

    /// <summary>
    /// What the screens tell by themselves: the Settings page with the outcome of its actions when they had problems
    /// (a ReShade.ini that can't be written, settings that can't be saved, a reset with problems, the report as
    /// selectable text when the clipboard is busy), and the Status page while an override waits for the game's focus,
    /// with Global / Menu chosen in the override list.
    /// </summary>
    [SnapshotFact]
    public void MainWindow_SettingsOutcomes_AndAPendingOverride() => CampfireTestHost.RunSta(() =>
    {
        WindowScene scene = WindowScene.Create(Path.Combine(_dir, "outcomes"), "active");
        try
        {
            MainViewModel vm = scene.ViewModel;
            AppController c = scene.Harness.Controller;

            // the override to a realm without its own preset, while the player is not in the game
            scene.Harness.Focused = false;
            vm.SelectedOverrideRealm = "Haddonfield";
            vm.ApplyOverrideCommand.Execute(null);
            vm.SelectedOverrideRealm = DBDAutoReShade.App.ViewModels.GlobalPresetRow.Label;
            scene.Settle();
            CampfireTestHost.SavePng((FrameworkElement)scene.Window.Content, Path.Combine(OutDir, "window-override-pending-status-1280x800.png"));

            // Settings: a ReShade.ini that can't be written, then settings that can't be saved
            vm.SelectedTab = AppTab.Settings;
            string locked = Path.Combine(_dir, "outcomes", "locked-ReShade.ini");
            File.WriteAllText(locked, "[GENERAL]\n");
            File.SetAttributes(locked, FileAttributes.ReadOnly);
            try
            {
                vm.ApplyReshadeIni(locked);
                File.Delete(scene.Harness.ConfigPath);
                Directory.CreateDirectory(scene.Harness.ConfigPath); // the config can no longer be written
                vm.DebugMode = true;
                vm.BeginHotkeyCapture();
                vm.CaptureHotkey(System.Windows.Input.Key.F7, System.Windows.Input.ModifierKeys.None);
                scene.Settle();
                CampfireTestHost.SavePng((FrameworkElement)scene.Window.Content, Path.Combine(OutDir, "window-settings-outcomes-1280x800.png"));

                vm.ClipboardWriter = _ => throw new InvalidOperationException("The clipboard is busy.");
                vm.RunDiagnostics();
                vm.CopyDiagnostics();
                vm.ResetSettings();
                vm.ConfirmResetSettings();
                var settings = (DBDAutoReShade.App.Views.SettingsView)scene.Window.FindName("SettingsPage");
                var scroller = (System.Windows.Controls.ScrollViewer)settings.FindName("Scroller");
                scene.Settle();
                scroller.ScrollToEnd();
                scene.Settle();
                CampfireTestHost.SavePng((FrameworkElement)scene.Window.Content, Path.Combine(OutDir, "window-settings-outcomes-end-1280x800.png"));
            }
            finally
            {
                File.SetAttributes(locked, FileAttributes.Normal);
            }
        }
        finally
        {
            scene.Dispose();
        }
    }, timeoutSeconds: 180);

    /// <summary>
    /// The real window telling a match: the map found while scanning in front (real time, frames at about the given
    /// times: the skill check lands, the Great, the dial lifts, the fog clears off the map), then the preset confirmed
    /// (the ember runs round the frame) and the match end (the map sinks, the legs yank up), each frozen at the given
    /// times from the change.
    /// </summary>
    [SnapshotFact]
    public void MainWindow_MatchStory_Frames() => CampfireTestHost.RunSta(() =>
    {
        WindowScene scene = WindowScene.Create(Path.Combine(_dir, "story"), "scanning");
        try
        {
            scene.Policy.WindowActive = true; // the player looks at the app
            CampfireTestHost.Wait(TimeSpan.FromMilliseconds(1600));
            scene.Harness.UnfocusRightAfterTheRead(); // found, then waiting for the click into the game
            scene.Harness.Ocr.Text = "COAL TOWER\nMACMILLAN ESTATE";
            WindowScene.PumpUntil(() => scene.Window.IsLanding, "the landing");
            Frames(scene, "found", 0, 120, 250, 380, 560, 800, 1100, 1500, 2200);

            scene.Harness.Focused = true; // the click into the game: switched, then ReShade confirms
            Assert.True(scene.Harness.Controller.RetryPendingActivation());
            FrozenFrames(scene, "confirm", 0, 200, 450, 700, 950, 1400);

            scene.Harness.Lifecycle.PollOnce(); // the loading screen (armed while DBD was not in front: this frame is the reference)
            scene.Harness.Capture.Level = 200; // gameplay
            for (int i = 0; i < 12 && scene.Harness.Lifecycle.Phase != DBDAutoReShade.Desktop.Services.LifecyclePhase.InTrial; i++)
            {
                scene.Harness.Lifecycle.PollOnce();
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(20));
            }

            scene.Harness.DriveToMenus(); // the match ends: Global is restored
            FrozenFrames(scene, "matchend", 0, 250, 500, 750, 1100, 2100);
        }
        finally
        {
            scene.Dispose();
        }
    }, timeoutSeconds: 180);

    /// <summary>Frames of the moments just started, each frozen at the given time from its start (deterministic; the
    /// window's state moment poses its parts at their own offsets).</summary>
    private static void FrozenFrames(WindowScene scene, string name, params int[] times)
    {
        CampfireTestHost.Pump();
        foreach (int ms in times)
        {
            MotionDiagnostics.FreezeAllAt(TimeSpan.FromMilliseconds(ms), scene.Window.CurrentMoment);
            scene.Window.UpdateLayout();
            CampfireTestHost.SavePng((FrameworkElement)scene.Window.Content, Path.Combine(OutDir, $"story-{name}-{ms:0000}.png"), 0.5);
        }

        MotionDiagnostics.FreezeAllAt(TimeSpan.FromSeconds(30));
    }

    private static void Frames(WindowScene scene, string name, params int[] times)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (int ms in times)
        {
            while (clock.ElapsedMilliseconds < ms)
            {
                CampfireTestHost.Wait(TimeSpan.FromMilliseconds(10));
            }

            long at = clock.ElapsedMilliseconds;
            scene.Window.UpdateLayout();
            CampfireTestHost.SavePng((FrameworkElement)scene.Window.Content, Path.Combine(OutDir, $"story-{name}-{at:0000}.png"), 0.5);
        }
    }

    /// <summary>
    /// States the per-state renders don't reach: ReShade.ini not set while presets are assigned (Presets asks for it,
    /// never for a DBD restart) with a Global preset hotkey another program holds (the rail dims it and says it is
    /// unavailable); a realm-only read (the plate shows the realm in the campfire clearing, not a map the scan never
    /// read); and wide windows (the Status plate stops at 1.8 x the pictures by width and height, the Presets thumbnails
    /// at the pictures' own width).
    /// </summary>
    [SnapshotFact]
    public void MainWindow_EdgeStates_AndWideWindows() => CampfireTestHost.RunSta(() =>
    {
        WindowScene scene = WindowScene.Create(Path.Combine(_dir, "edge"), "ready");
        try
        {
            MainViewModel vm = scene.ViewModel;
            scene.Harness.Controller.SetReshadeIni(null);
            var f9 = new DetectionHotkeyConfig { Key = "F9" };
            scene.Harness.GlobalPresetHotkey.HoldElsewhere(f9);
            vm.BeginGlobalHotkeyCapture();
            vm.CancelHotkeyCapture(); // registered again: F9 is taken now
            vm.SelectedTab = AppTab.Presets;
            vm.ShowRestartInPresets();
            scene.Settle();
            CampfireTestHost.SavePng((FrameworkElement)scene.Window.Content, Path.Combine(OutDir, "window-reshadeini-unset-presets-1280x800.png"));
            vm.SelectedTab = AppTab.Status;
            scene.Settle();
            CampfireTestHost.SavePng((FrameworkElement)scene.Window.Content, Path.Combine(OutDir, "window-reshadeini-unset-status-1280x800.png"));
        }
        finally
        {
            scene.Dispose();
        }

        scene = WindowScene.Create(Path.Combine(_dir, "realm-only"), "ready");
        try
        {
            scene.Harness.Ocr.Text = "MACMILLAN ESTATE"; // only the realm line is legible
            _ = Task.Run(() => scene.Harness.Controller.TriggerDetectionAsync());
            WindowScene.PumpUntil(() => scene.ViewModel.StatusScreen.DetectedRealm is not null && scene.ViewModel.StatusScreen.State == ScreenState.Active, "the realm-only read");
            scene.Harness.ReshadeSaves(scene.Preset("11.STX.Macmillan.ini"));
            WindowScene.PumpUntil(() => scene.Harness.Controller.IsActivePresetConfirmed, "confirmed by ReShade");
            scene.Settle();
            CampfireTestHost.SavePng((FrameworkElement)scene.Window.Content, Path.Combine(OutDir, "window-realm-only-status-1280x800.png"));
        }
        finally
        {
            scene.Dispose();
        }

        scene = WindowScene.Create(Path.Combine(_dir, "wide"), "active", 1920, 1080);
        try
        {
            scene.Settle();
            CampfireTestHost.SavePng((FrameworkElement)scene.Window.Content, Path.Combine(OutDir, "window-active-status-1920x1080.png"), 0.75);

            // the Presets page alone at widths a window reaches on wide or 4K screens
            foreach (double width in new[] { 2340.0, 3620.0 })
            {
                var view = new DBDAutoReShade.App.Views.PresetsView { DataContext = scene.ViewModel };
                MotionPolicy.SetPolicy(view, new MotionPolicy { AnimationsEnabled = false });
                System.Windows.Controls.Grid frame = CampfireTestHost.Frame(view, width, 900);
                CampfireTestHost.Layout(frame, width, 900);
                CampfireTestHost.Layout(frame, width, 900);
                CampfireTestHost.SavePng(frame, Path.Combine(OutDir, $"view-active-presets-{width:0}x900.png"), 0.5);
            }
        }
        finally
        {
            scene.Dispose();
        }
    }, timeoutSeconds: 240);

    private void RenderAll(double width, double height)
    {
        foreach (string state in WindowScene.States)
        {
            CampfireTestHost.RunSta(() => RenderState(state, width, height), timeoutSeconds: 180);
        }
    }

    private void RenderState(string state, double width, double height)
    {
        WindowScene scene = WindowScene.Create(Path.Combine(_dir, $"{state}-{width}"), state, width, height);
        try
        {
            foreach (AppTab tab in new[] { AppTab.Status, AppTab.Presets, AppTab.Settings })
            {
                scene.ViewModel.SelectedTab = tab;
                scene.Settle();
                if (state is "scanning" or "paused")
                {
                    SkillCheck skill = FindStatus(scene.Window).MapPlate.SkillCheck;
                    if (state == "scanning")
                    {
                        skill.SeekSpin(TimeSpan.FromMilliseconds(2400));
                    }
                }

                scene.Window.UpdateLayout();
                string name = $"window-{state}-{tab.ToString().ToLowerInvariant()}-{width}x{height}.png";
                CampfireTestHost.SavePng((FrameworkElement)scene.Window.Content, Path.Combine(OutDir, name));
                if (tab == AppTab.Settings && state == "ready")
                {
                    // the rest of the Settings page (it scrolls)
                    var settings = (DBDAutoReShade.App.Views.SettingsView)scene.Window.FindName("SettingsPage");
                    var scroller = (System.Windows.Controls.ScrollViewer)settings.FindName("Scroller");
                    scroller.ScrollToEnd();
                    scene.Settle();
                    CampfireTestHost.SavePng((FrameworkElement)scene.Window.Content, Path.Combine(OutDir, $"window-ready-settings-end-{width}x{height}.png"));
                    scroller.ScrollToHome();
                }
            }
        }
        finally
        {
            scene.Dispose();
        }
    }

    private static DBDAutoReShade.App.Views.StatusView FindStatus(MainWindow window) => (DBDAutoReShade.App.Views.StatusView)window.FindName("StatusPage");
}

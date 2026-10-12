using System.Windows.Input;
using DBDAutoReShade.App.Services;
using DBDAutoReShade.App.ViewModels;
using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Tests;

/// <summary>
/// The Campfire screens' view model over the real controller, for what the screens must tell by themselves: every
/// Settings action shows its own outcome (the status line is on the Status screen only), the Test countdown follows
/// the controller's real focus wait (a new press restarts it, a row shown again resumes it), the override facts are
/// true while the forced file is still waiting, the override list offers Global / Menu, the volume slider follows the
/// saved setting, the report can be copied by hand when the clipboard is busy, Enter / Space on the capture button
/// cancel silently, rows say everything they show, and an unchanged refresh raises nothing.
/// Part of <see cref="ControllerActivationTests"/> (sequential).
/// </summary>
public sealed partial class ControllerActivationTests
{
    private const string Lerys = "Léry's Memorial Institute";

    // ---------- Settings: every action tells its outcome on the Settings screen ----------

    [Fact]
    public void Settings_APathChange_ThatCannotUpdateReshadeIni_SaysSoOnTheSettingsScreen()
    {
        var (h, vm, _) = ViewModel();
        string other = Path.Combine(h.Dir, "other-ReShade.ini");
        File.WriteAllText(other, "[GENERAL]\n");
        File.SetAttributes(other, FileAttributes.ReadOnly); // its shortcuts can't be written
        try
        {
            vm.ApplyReshadeIni(other);

            Assert.Equal("ReShade.ini path updated.", vm.PathsNote);
            Assert.NotNull(vm.PathsProblem);
            Assert.Contains("Could not update the preset shortcuts in ReShade.ini", vm.PathsProblem);
        }
        finally
        {
            File.SetAttributes(other, FileAttributes.Normal);
        }

        vm.ApplyReshadeIni(h.ReshadeIni); // back to a writable one: nothing more to say
        Assert.Equal("ReShade.ini path updated.", vm.PathsNote);
        Assert.Null(vm.PathsProblem);
    }

    [Fact]
    public void Settings_ChangesThatCannotBeSaved_SaySoNextToTheirGroup()
    {
        var (h, vm, _) = ViewModel();
        BreakConfigSaving(h);

        vm.ApplyDbdPath(Path.Combine(h.Dir, "DeadByDaylight-Win64-Shipping.exe"));
        Assert.Equal("Dead by Daylight path updated.", vm.PathsNote);
        Assert.StartsWith("Could not save settings", vm.PathsProblem);

        vm.DebugMode = true;
        Assert.StartsWith("Could not save settings", vm.BehaviourProblem);
        vm.AnimationsEnabled = false;
        Assert.StartsWith("Could not save settings", vm.BehaviourProblem);
        vm.SoundEnabled = false;
        Assert.StartsWith("Could not save settings", vm.BehaviourProblem);
        vm.SoundVolumePercent = 70;
        vm.CommitSoundVolume();
        Assert.StartsWith("Could not save settings", vm.BehaviourProblem);

        vm.ManifestUrl = "https://example.com/manifest.json";
        Assert.Null(vm.ManifestUrlError);
        Assert.StartsWith("Could not save settings", vm.DatabaseProblem);

        RepairConfigSaving(h);
        vm.AutoRestore = false; // saved again: the problem is gone
        Assert.Null(vm.BehaviourProblem);
    }

    [Fact]
    public void Settings_AHotkeyChange_ThatCannotBeSaved_OrLeavesTheOtherHotkeyUnregistered_SaysSo()
    {
        var (h, vm, warnings) = ViewModel();
        BreakConfigSaving(h);
        vm.BeginHotkeyCapture();
        Assert.True(vm.CaptureHotkey(Key.F7, ModifierKeys.None));
        Assert.Equal("Hotkey set to F7.", vm.HotkeyNote);
        Assert.StartsWith("Could not save settings", vm.HotkeyNoteProblem);

        vm.ClearGlobalHotkey();
        Assert.Equal("Global preset hotkey cleared.", vm.GlobalHotkeyNote);
        Assert.StartsWith("Could not save settings", vm.GlobalHotkeyNoteProblem);
        Assert.Empty(warnings);

        // another program holds F9: moving the detection hotkey re-registers the Global one, which fails
        var (h2, vm2, _) = ViewModel(new ControllerHarnessOptions
        {
            Configure = c => c.DetectionHotkey = Combo("F20"),
            HotkeysHeldElsewhere = new[] { new DetectionHotkeyConfig { Key = "F9" } },
        });
        vm2.BeginHotkeyCapture();
        Assert.True(vm2.CaptureHotkey(Key.F7, ModifierKeys.None));
        Assert.Equal("Hotkey set to F7.", vm2.HotkeyNote);
        Assert.NotNull(vm2.HotkeyNoteProblem);
        Assert.Contains("F9", vm2.HotkeyNoteProblem);
        Assert.Equal("F7", h2.Hotkey.RegisteredHotkey?.Key);

        vm2.BeginHotkeyCapture(); // a new capture starts clean
        Assert.Null(vm2.HotkeyNoteProblem);
        vm2.CancelHotkeyCapture();
    }

    [Fact]
    public void ResetSettings_ThatHadProblems_SaysSo_InsteadOfClaimingSuccess()
    {
        var (h, vm, _) = ViewModel();
        File.SetAttributes(h.ReshadeIni, FileAttributes.ReadOnly); // this app's shortcuts can't be removed
        try
        {
            vm.ResetSettingsCommand.Execute(null);
            vm.ConfirmResetCommand.Execute(null);

            Assert.Equal("Settings reset, with problems. Your preset files were not touched.", vm.ResetNote);
            Assert.NotNull(vm.ResetProblem);
            Assert.Contains("Could not remove this app's shortcuts from ReShade.ini", vm.ResetProblem);
        }
        finally
        {
            File.SetAttributes(h.ReshadeIni, FileAttributes.Normal);
        }

        // the settings can't be saved, and another program holds the default hotkey
        var (h2, vm2, _) = ViewModel();
        h2.Hotkey.HoldElsewhere(new DetectionHotkeyConfig { Key = "F8" });
        BreakConfigSaving(h2);
        vm2.ResetSettings();
        vm2.ConfirmResetSettings();
        Assert.StartsWith("Settings reset, with problems.", vm2.ResetNote);
        Assert.Contains("Could not save settings", vm2.ResetProblem);
        Assert.Contains("Hotkey 'F8' unavailable", vm2.ResetProblem);

        // asking again clears the old outcome
        vm2.ResetSettings();
        Assert.Null(vm2.ResetProblem);
        Assert.Equal(string.Empty, vm2.ResetNote);
    }

    [Fact]
    public void CopyReport_WhenTheClipboardIsBusy_ShowsTheReportAsSelectableText()
    {
        var (_, vm, _) = ViewModel();
        var copied = new List<string>();
        vm.ClipboardWriter = copied.Add; // never the real clipboard in a test
        vm.RunDiagnostics();

        vm.CopyDiagnosticsCommand.Execute(null);
        Assert.Equal(new[] { vm.DiagnosticsReport }, copied);
        Assert.Equal("Report copied.", vm.CopyNote);
        Assert.False(vm.ShowReportText);

        vm.ClipboardWriter = _ => throw new System.Runtime.InteropServices.COMException("OpenClipboard failed", unchecked((int)0x800401D0));
        vm.CopyDiagnostics();
        Assert.True(vm.ShowReportText); // the plain text is shown to select and copy by hand
        Assert.Contains("Select the report text below and press Ctrl+C", vm.CopyNote);

        vm.RunDiagnostics(); // a new check hides it again
        Assert.False(vm.ShowReportText);
    }

    // ---------- the volume slider follows the saved setting ----------

    [Fact]
    public void SoundVolume_StoredValue_Loads_SnapsToTheSavedStep_AndResetBringsBack50()
    {
        var (h, vm, _) = ViewModel(new ControllerHarnessOptions
        {
            Configure = c =>
            {
                c.DetectionHotkey = Combo("F20");
                c.SoundVolume = 0.3;
            },
        });
        Assert.Equal(30, vm.SoundVolumePercent);
        Assert.Equal("30 %", vm.SoundVolumeText);

        vm.SoundVolumePercent = 63.4; // a drag ends between steps
        vm.CommitSoundVolume();
        Assert.Equal(0.63, h.Controller.SoundVolume, 6);
        Assert.Equal(63, vm.SoundVolumePercent); // the thumb snaps to what was saved

        vm.SoundVolumePercent = 80;
        vm.CommitSoundVolume();
        vm.ResetSettings();
        vm.ConfirmResetSettings();
        Assert.Equal(0.5, h.Controller.SoundVolume);
        Assert.Equal(50, vm.SoundVolumePercent); // back to the first-run default, not dirty
        Assert.Equal("50 %", vm.SoundVolumeText);
    }

    [Fact]
    public void SoundProblem_IsAskedOnce()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { Configure = c => c.DetectionHotkey = Combo("F20") });
        h.Sounds.Problem = "The Great sound is missing from this build.";
        var vm = new MainViewModel(h.Controller, _ => { });
        vm.Refresh();
        Assert.Equal("The Great sound is missing from this build.", vm.SoundProblem);

        h.Sounds.Problem = null; // it does not change after loading: not asked on every refresh
        vm.Refresh();
        Assert.Equal("The Great sound is missing from this build.", vm.SoundProblem);
    }

    // ---------- the Test countdown follows the controller's focus wait ----------

    [Fact]
    public void Presets_Test_APressAgain_RestartsTheRing_AndARowShownAgain_ResumesIt()
    {
        var (h, vm, _) = ViewModel();
        RealmRow coldwind = vm.RealmRows.Single(r => r.RealmName == "Coldwind Farm");
        vm.SelectRow(coldwind);
        h.Focused = false;

        vm.TestSelectedCommand.Execute(null);
        PresetFeedback first = vm.Feedback;
        Assert.Equal(FeedbackKind.Test, first.Kind);
        Assert.Equal(TimeSpan.Zero, first.Elapsed);

        h.Advance(TimeSpan.FromSeconds(6));
        vm.Refresh();
        Assert.Same(first, vm.Feedback); // the running ring is left alone

        // another row, then back: the ring starts where the controller's wait really is
        vm.SelectRow(vm.GlobalRow);
        vm.SelectRow(coldwind);
        Assert.Equal(FeedbackKind.Test, vm.Feedback.Kind);
        Assert.Equal(TimeSpan.FromSeconds(6), vm.Feedback.Elapsed);
        Assert.Equal(first.Press, vm.Feedback.Press);

        // a second press restarts the controller's 15 s wait: the ring restarts too
        int raised = 0;
        vm.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(MainViewModel.Feedback) ? 1 : 0;
        vm.TestSelectedCommand.Execute(null);
        Assert.Equal(AppController.TestFocusWindow, h.Controller.PendingTest!.Remaining);
        Assert.Equal(1, raised);
        Assert.Equal(TimeSpan.Zero, vm.Feedback.Elapsed);
        Assert.NotEqual(first.Press, vm.Feedback.Press);
    }

    // ---------- the Status screen: the override ----------

    [Fact]
    public async Task Override_WaitingForFocus_ShowsWhatComesNext_NotAForcedFileThatIsNotOn()
    {
        var (h, vm, _) = ViewModel();
        await h.Controller.TriggerDetectionAsync(); // Coldwind Farm is active
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);

        h.Focused = false;
        vm.SelectedOverrideRealm = Lerys; // no preset of its own: the Global preset is forced
        vm.ApplyOverrideCommand.Execute(null);

        StatusScreen waiting = vm.StatusScreen;
        Assert.Equal(ScreenState.Override, waiting.State);
        Assert.Equal(Path.GetFileName(h.ColdwindIni), waiting.ActiveFile);
        Assert.Equal(PresetTail.Pending, waiting.Tail); // "Next: Global.ini when the game has focus", no Override pill
        Assert.Equal(Path.GetFileName(h.GlobalIni), waiting.TailSubject);
        Assert.Equal(h.GlobalIni, h.Controller.OverridePresetPath);

        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation());
        StatusScreen forced = vm.StatusScreen;
        Assert.Equal(Path.GetFileName(h.GlobalIni), forced.ActiveFile);
        Assert.Equal(PresetTail.Override, forced.Tail); // now it is forced for the realm
        Assert.Equal(Lerys, forced.TailSubject);
    }

    [Fact]
    public void OverrideList_OffersGlobalMenu_WhichSwitchesToTheGlobalPresetNow()
    {
        var (h, vm, _) = ViewModel();
        Assert.Equal(GlobalPresetRow.Label, vm.OverrideChoices[0]);
        Assert.Equal(vm.AvailableRealms, vm.OverrideChoices.Skip(1));
        Assert.Equal("Apply override", vm.ApplyOverrideText);

        vm.SelectedOverrideRealm = "Coldwind Farm";
        vm.ApplyOverrideCommand.Execute(null);
        Assert.True(vm.IsManualOverride);
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);

        vm.SelectedOverrideRealm = GlobalPresetRow.Label;
        Assert.Equal("Switch to Global", vm.ApplyOverrideText);
        Assert.True(vm.ApplyOverrideCommand.CanExecute(null));
        vm.ApplyOverrideCommand.Execute(null);

        Assert.False(vm.IsManualOverride); // automatic switching stays on
        Assert.Equal(h.GlobalIni, h.Controller.ActivePresetPath);
        Assert.StartsWith("Switched to the Global preset", h.Controller.StatusMessage);
        Assert.Equal(ScreenState.Ready, vm.StatusScreen.State);
    }

    [Fact]
    public void OverrideHelp_QuotesTheSettingsCheckbox()
    {
        var (_, vm, _) = ViewModel();
        Assert.Contains("\"" + MainViewModel.AutoRestoreLabel + "\"", vm.OverrideHelpText);
        Assert.Equal("Restore the Global preset when a match ends", MainViewModel.AutoRestoreLabel);
    }

    [Fact]
    public void LastSwitch_InTheRail_SaysWhenTheLastAttemptFailed()
    {
        var (h, vm, _) = ViewModel(new ControllerHarnessOptions { MaxAttempts = 1, Configure = c => c.DetectionHotkey = Combo("F20") });
        Assert.Equal("—", vm.LastSwitchTime);
        Assert.False(vm.LastSwitchFailed);

        h.Keys.NextResult = false; // ReShade does not take the keys
        Assert.NotNull(h.Controller.TestGlobalPreset());
        Assert.True(vm.LastSwitchFailed);
        Assert.EndsWith(", failed", vm.LastSwitchTime);

        h.Keys.NextResult = true;
        Assert.Null(h.Controller.TestGlobalPreset());
        Assert.False(vm.LastSwitchFailed);
        Assert.DoesNotContain("failed", vm.LastSwitchTime);
    }

    // ---------- hotkey capture by keyboard ----------

    [Fact]
    public void HotkeyCapture_EnterOrSpaceOnItsCancelButton_CancelsSilently_AndTheButtonNameFollows()
    {
        var (h, vm, warnings) = ViewModel();
        Assert.Equal("Change hotkey for detection", vm.HotkeyButtonName);
        Assert.Equal("Change hotkey for the Global preset", vm.GlobalHotkeyButtonName);
        Assert.True(vm.IsCaptureCommand(vm.BeginHotkeyCaptureCommand));
        Assert.True(vm.IsCaptureCommand(vm.BeginGlobalHotkeyCaptureCommand));
        Assert.False(vm.IsCaptureCommand(vm.ClearGlobalHotkeyCommand));
        Assert.False(vm.HandleCaptureKey(Key.F7, ModifierKeys.None, onCaptureButton: false)); // not capturing

        vm.BeginHotkeyCaptureCommand.Execute(null);
        Assert.Equal("Cancel", vm.HotkeyButtonText);
        Assert.Equal("Cancel detection hotkey change", vm.HotkeyButtonName); // the name says what the button does now
        Assert.True(vm.HandleCaptureKey(Key.LeftCtrl, ModifierKeys.Control, onCaptureButton: true)); // waits for its key
        Assert.True(vm.IsCapturingHotkey);
        Assert.True(vm.HandleCaptureKey(Key.Enter, ModifierKeys.None, onCaptureButton: true));
        Assert.False(vm.IsCapturingHotkey);
        Assert.Equal("Hotkey unchanged.", vm.HotkeyNote);
        Assert.Equal("Change hotkey for detection", vm.HotkeyButtonName);
        Assert.True(h.Hotkey.IsRegistered);

        vm.BeginGlobalHotkeyCaptureCommand.Execute(null);
        Assert.Equal("Cancel Global preset hotkey change", vm.GlobalHotkeyButtonName);
        Assert.True(vm.HandleCaptureKey(Key.Space, ModifierKeys.None, onCaptureButton: true));
        Assert.False(vm.IsCapturingHotkey);
        Assert.Empty(warnings); // no "Use F1–F24 …" dialog for a cancel

        vm.BeginHotkeyCapture(); // Tab ends the capture and moves on (no keyboard trap)
        Assert.False(vm.HandleCaptureKey(Key.Tab, ModifierKeys.None, onCaptureButton: true));
        Assert.False(vm.IsCapturingHotkey);

        vm.BeginHotkeyCapture(); // Space elsewhere is a key that can't be a hotkey: said so
        Assert.True(vm.HandleCaptureKey(Key.Space, ModifierKeys.None, onCaptureButton: false));
        Assert.Equal(MainViewModel.UnsupportedHotkeyMessage, Assert.Single(warnings));

        vm.BeginHotkeyCapture();
        Assert.True(vm.HandleCaptureKey(Key.F7, ModifierKeys.None, onCaptureButton: true)); // a real key is captured
        Assert.Equal("F7", h.Controller.Config.DetectionHotkey.Key);
    }

    // ---------- rows say everything they show; an unchanged refresh raises nothing ----------

    [Fact]
    public async Task Rows_AccessibleName_HasTheirMarkAndMetaLine()
    {
        var (h, vm, _) = ViewModel();
        await h.Controller.TriggerDetectionAsync(); // Coldwind Farm is the active realm
        RealmRow coldwind = vm.RealmRows.Single(r => r.RealmName == "Coldwind Farm");
        string file = Path.GetFileName(h.ColdwindIni);
        Assert.Equal($"Coldwind Farm, {file}, 5 maps, active realm", coldwind.AccessibleName);

        int raised = 0;
        coldwind.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(RealmRow.AccessibleName) ? 1 : 0;
        vm.FilterText = "cowshed";
        Assert.Equal($"Coldwind Farm, {file}, Map: Fractured Cowshed, active realm", coldwind.AccessibleName);
        Assert.True(raised > 0);

        Assert.Equal($"Global / Menu, {Path.GetFileName(h.GlobalIni)}", vm.GlobalRow.AccessibleName);
        Assert.Equal(string.Empty, vm.PresetsTabStatus);
        Assert.Null(h.Controller.SetRealmPreset(Autohaven, NewPresetFile(h, "Autohaven")));
        RealmRow autohaven = vm.RealmRows.Single(r => r.RealmName == Autohaven);
        Assert.EndsWith(", restart needed", autohaven.AccessibleName);
        Assert.Equal("Restart needed", vm.PresetsTabStatus); // the red dot on the Presets tab, for assistive technology
    }

    [Fact]
    public async Task Refresh_WithNothingChanged_RaisesNothing()
    {
        var (h, vm, _) = ViewModel();
        await h.Controller.TriggerDetectionAsync(); // a detection, an active realm, marks, thumbnails
        vm.SelectedTab = AppTab.Presets;
        vm.Refresh();

        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add("vm." + e.PropertyName);
        vm.GlobalRow.PropertyChanged += (_, e) => raised.Add("Global." + e.PropertyName);
        foreach (RealmRow row in vm.RealmRows)
        {
            row.PropertyChanged += (_, e) => raised.Add(row.RealmName + "." + e.PropertyName);
        }

        foreach (MapThumb thumb in vm.Thumbs)
        {
            thumb.PropertyChanged += (_, e) => raised.Add(thumb.Name + "." + e.PropertyName);
        }

        int canExecute = 0;
        vm.TestSelectedCommand.CanExecuteChanged += (_, _) => canExecute++;
        vm.ClearSelectedCommand.CanExecuteChanged += (_, _) => canExecute++;

        h.Controller.SetDebug(h.Controller.Config.DebugMode); // a controller event that changes nothing on screen
        vm.Refresh();

        Assert.Empty(raised);
        Assert.Equal(0, canExecute);
    }

    [Fact]
    public void ControllerEvents_FromAnotherThread_QueueOneRefreshAtATime()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { Configure = c => c.DetectionHotkey = Combo("F20") });
        DBDAutoReShade.Tests.Campfire.CampfireTestHost.RunSta(() =>
        {
            System.Windows.Threading.Dispatcher ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var vm = new MainViewModel(h.Controller, _ => { }, ui);
            vm.Refresh();
            int posted = 0;
            ui.Hooks.OperationPosted += (_, _) => Interlocked.Increment(ref posted);

            // a burst of events (a scan raises one per sample) while the UI thread is busy: one refresh is queued
            Task.Run(() =>
            {
                for (int i = 0; i < 25; i++)
                {
                    h.Controller.SetDebug(i % 2 == 0);
                }
            }).Wait();
            Assert.Equal(1, Volatile.Read(ref posted));
            DBDAutoReShade.Tests.Campfire.CampfireTestHost.Pump();
            Assert.Equal(h.Controller.Config.DebugMode, vm.DebugMode); // it read the newest state

            // once it ran, the next event queues the next refresh
            Volatile.Write(ref posted, 0); // (the pump posted its own)
            Task.Run(() => h.Controller.SetDebug(!h.Controller.Config.DebugMode)).Wait();
            Assert.Equal(1, Volatile.Read(ref posted));
            DBDAutoReShade.Tests.Campfire.CampfireTestHost.Pump();
            Assert.Equal(h.Controller.Config.DebugMode, vm.DebugMode);
        });
    }

    private static void BreakConfigSaving(ControllerHarness h)
    {
        File.Delete(h.ConfigPath);
        Directory.CreateDirectory(h.ConfigPath); // the config can no longer be written
    }

    private static void RepairConfigSaving(ControllerHarness h) => Directory.Delete(h.ConfigPath, recursive: true);
}

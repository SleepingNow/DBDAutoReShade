using System.Windows.Input;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Services;
using DBDAutoReShade.App.ViewModels;
using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// The Campfire view model over the real controller: the new settings (animations, the Great sound and its volume,
/// saved through the controller; the slider saves when it settles), the Global preset hotkey capture, the Status
/// screen picture per state, and the Presets list and preset panel (filter, the detected realm, the Test countdown
/// tied to the controller's focus wait, Clear). Part of <see cref="ControllerActivationTests"/> (sequential).
/// </summary>
public sealed partial class ControllerActivationTests
{
    // ---------- settings ----------

    [Fact]
    public void Animations_AndSound_AreSavedThroughTheController_AndFollowItBack()
    {
        var (h, vm, _) = ViewModel();
        Assert.True(vm.AnimationsEnabled);
        Assert.True(vm.SoundEnabled);
        Assert.Equal(50, vm.SoundVolumePercent);
        Assert.Equal("50 %", vm.SoundVolumeText);

        vm.AnimationsEnabled = false;
        vm.SoundEnabled = false;
        Assert.False(h.Controller.Config.AnimationsEnabled);
        Assert.False(h.Controller.Config.SoundEnabled);
        AppConfig saved = Reload(h);
        Assert.False(saved.AnimationsEnabled);
        Assert.False(saved.SoundEnabled);
        Assert.False(vm.PlaySoundCommand.CanExecute(null)); // the preview is off with the sound

        h.Controller.SetAnimationsEnabled(true); // e.g. a reset
        h.Controller.SetSoundEnabled(true);
        Assert.True(vm.AnimationsEnabled);
        Assert.True(vm.SoundEnabled);
        Assert.True(vm.PlaySoundCommand.CanExecute(null));
    }

    [Fact]
    public void SoundVolume_LabelFollowsEveryTick_ButIsSavedOnlyWhenTheSliderSettles()
    {
        var (h, vm, _) = ViewModel();
        int changes = 0;
        h.Controller.Changed += (_, _) => changes++;

        foreach (double tick in new[] { 51.0, 57.4, 63, 70, 81.6, 90 })
        {
            vm.SoundVolumePercent = tick; // a drag
        }

        Assert.Equal("90 %", vm.SoundVolumeText);
        Assert.Equal(0.5, h.Controller.SoundVolume); // nothing saved yet
        Assert.Equal(0, changes);
        vm.Refresh(); // a controller event meanwhile keeps the drag
        Assert.Equal(90, vm.SoundVolumePercent);

        vm.CommitSoundVolume(); // the thumb is released
        Assert.Equal(0.9, h.Controller.SoundVolume, 6);
        Assert.Equal(0.9, Reload(h).SoundVolume, 6);
        Assert.Equal(1, changes);

        vm.CommitSoundVolume(); // nothing changed since
        Assert.Equal(1, changes);

        vm.SoundVolumePercent = 140; // clamped
        Assert.Equal(100, vm.SoundVolumePercent);
        vm.CommitSoundVolume();
        Assert.Equal(1.0, h.Controller.SoundVolume);
    }

    [Fact]
    public void PlaySound_PlaysAtTheSlidersVolume_OnlyWhileTheSoundIsOn_AndSilentAtZero()
    {
        var (h, vm, _) = ViewModel();
        vm.SoundVolumePercent = 30; // not saved yet: the preview still uses it
        vm.PlaySoundCommand.Execute(null);
        Assert.Equal(new[] { 0.3 }, h.Sounds.Played.ToArray());

        vm.SoundVolumePercent = 0;
        Assert.False(vm.PlaySoundPreview());
        Assert.Single(h.Sounds.Played);

        vm.SoundVolumePercent = 80;
        vm.SoundEnabled = false;
        Assert.False(vm.PlaySoundCommand.CanExecute(null));
        Assert.False(vm.PlaySoundPreview());
        Assert.Single(h.Sounds.Played);
    }

    [Fact]
    public void SoundProblem_IsShownNextToTheSoundSettings()
    {
        var (_, vm, _) = ViewModel();
        Assert.Null(vm.SoundProblem); // the sound can play (see SoundProblem_IsAskedOnce for a missing one)
    }

    // ---------- the Global preset hotkey ----------

    [Fact]
    public void GlobalHotkeyCapture_PausesBothHotkeys_ThenRegistersTheNewKey()
    {
        var (h, vm, warnings) = ViewModel();
        Assert.Equal("F9", vm.GlobalHotkeyText);
        Assert.True(vm.HasGlobalHotkey);
        Assert.Equal("Global preset", vm.RailGlobalHint);

        vm.BeginGlobalHotkeyCaptureCommand.Execute(null);
        Assert.True(vm.GlobalHotkeyCaptureMode);
        Assert.True(vm.IsCapturingHotkey);
        Assert.False(vm.HotkeyCaptureMode);
        Assert.Equal("Cancel", vm.GlobalHotkeyButtonText);
        Assert.False(h.Hotkey.IsRegistered); // both are released: Windows would swallow either key
        Assert.False(h.GlobalPresetHotkey.IsRegistered);

        Assert.True(vm.CaptureHotkey(Key.F7, ModifierKeys.None));

        Assert.False(vm.IsCapturingHotkey);
        Assert.Equal("F7", h.GlobalPresetHotkey.RegisteredHotkey?.Key);
        Assert.Equal("F7", h.Controller.Config.GlobalHotkey?.Key);
        Assert.Equal("F7", vm.GlobalHotkeyText);
        Assert.Equal("Hotkey set to F7.", vm.GlobalHotkeyNote);
        Assert.Equal("F20", h.Hotkey.RegisteredHotkey?.Key); // the detection hotkey is back
        Assert.Empty(warnings);
    }

    [Fact]
    public void GlobalHotkeyCapture_EscCancels_AndTheDetectionCombo_IsRefused()
    {
        var (h, vm, warnings) = ViewModel();
        vm.BeginGlobalHotkeyCapture();
        Assert.False(vm.CaptureHotkey(Key.Escape, ModifierKeys.None));
        Assert.False(vm.IsCapturingHotkey);
        Assert.Equal("Hotkey unchanged.", vm.GlobalHotkeyNote);
        Assert.Equal("F9", h.GlobalPresetHotkey.RegisteredHotkey?.Key);
        Assert.Equal("F20", h.Hotkey.RegisteredHotkey?.Key);

        vm.BeginGlobalHotkeyCapture();
        Assert.False(vm.CaptureHotkey(Key.F20, ModifierKeys.Control | ModifierKeys.Alt)); // the detection hotkey

        string warning = Assert.Single(warnings);
        Assert.Contains("can't be the Global preset hotkey", warning);
        Assert.Contains("already the detection hotkey", warning);
        Assert.Contains("still F9", warning);
        Assert.Equal("F9", h.GlobalPresetHotkey.RegisteredHotkey?.Key);
        Assert.Equal("F9", h.Controller.Config.GlobalHotkey?.Key);

        vm.BeginGlobalHotkeyCapture();
        Assert.False(vm.CaptureHotkey(Key.Space, ModifierKeys.None));
        Assert.Equal(MainViewModel.UnsupportedGlobalHotkeyMessage, warnings[^1]);
    }

    [Fact]
    public void OnlyOneHotkeyIsCapturedAtATime_AndTheWindowCancelsBoth()
    {
        var (h, vm, _) = ViewModel();
        vm.BeginHotkeyCapture();
        vm.BeginGlobalHotkeyCapture(); // ignored while the detection hotkey is captured
        Assert.True(vm.HotkeyCaptureMode);
        Assert.False(vm.GlobalHotkeyCaptureMode);

        vm.CancelHotkeyCapture(); // the window lost focus
        Assert.False(vm.IsCapturingHotkey);
        Assert.Equal("Hotkey unchanged.", vm.HotkeyNote);
        Assert.True(h.Hotkey.IsRegistered);
        Assert.True(h.GlobalPresetHotkey.IsRegistered);
    }

    [Fact]
    public void ClearGlobalHotkey_DisablesIt_AndTheRailSaysSo()
    {
        var (h, vm, _) = ViewModel();
        vm.ClearGlobalHotkeyCommand.Execute(null);

        Assert.Null(h.Controller.Config.GlobalHotkey);
        Assert.False(h.GlobalPresetHotkey.IsRegistered);
        Assert.False(vm.HasGlobalHotkey);
        Assert.Equal("Not set", vm.GlobalHotkeyText);
        Assert.Equal("Global preset hotkey: not set", vm.RailGlobalHint);
        Assert.False(vm.ClearGlobalHotkeyCommand.CanExecute(null));
        Assert.Equal("Global preset hotkey cleared.", vm.GlobalHotkeyNote);
    }

    [Fact]
    public void AGlobalHotkeyThatCannotBeRegistered_IsShownAsABannerAndInSettings()
    {
        var (h, vm, _) = ViewModel(new ControllerHarnessOptions
        {
            Configure = c => c.DetectionHotkey = Combo("F20"),
            HotkeysHeldElsewhere = new[] { new DetectionHotkeyConfig { Key = "F9" } },
        });

        Assert.Equal("F9", vm.GlobalHotkeyText); // still the setting
        Assert.NotNull(vm.GlobalHotkeyProblem);
        Assert.Contains(vm.GlobalHotkeyProblem!, vm.BannerError);
        Assert.StartsWith("Global preset hotkey:", vm.BannerError);

        // the rail shows it unavailable, not as a working key
        Assert.True(vm.GlobalHotkeyUnavailable);
        Assert.Equal("Global preset hotkey unavailable", vm.RailGlobalHint);
        Assert.Equal("Global preset hotkey F9, unavailable", vm.RailGlobalHotkeyName);
        Assert.False(vm.HotkeyUnavailable); // the detection hotkey works
        Assert.Equal("Press Ctrl+Alt+F20 on the loading screen to scan", vm.RailScanHint);
        Assert.Equal("Detection hotkey Ctrl+Alt+F20", vm.RailHotkeyName);

        // the other program lets F9 go: once it is registered again the rail presents it as working
        h.GlobalPresetHotkey.ReleaseElsewhere(F9);
        vm.BeginGlobalHotkeyCapture();
        vm.CancelHotkeyCapture();
        Assert.True(h.GlobalPresetHotkey.Holds(F9));
        Assert.False(vm.GlobalHotkeyUnavailable);
        Assert.Equal("Global preset", vm.RailGlobalHint);
        Assert.Equal("Global preset hotkey F9", vm.RailGlobalHotkeyName);
    }

    [Fact]
    public void AGlobalHotkeyOnTheDetectionKey_TheRailShowsItUnavailable_NotAsASecondWorkingF9()
    {
        // e.g. a config saved by 1.1 (no Global preset hotkey yet) with F9 for detection: the Global default F9 is
        // kept but not registered (ConfigSavedBy11_WithF9AsTheDetectionHotkey_…)
        var (h, vm, _) = ViewModel(new ControllerHarnessOptions { Configure = c => c.DetectionHotkey = new DetectionHotkeyConfig { Key = "F9" } });
        Assert.True(h.Hotkey.Holds(F9));
        Assert.False(h.GlobalPresetHotkey.IsRegistered);

        Assert.Equal("F9", vm.HotkeyText);
        Assert.Equal("Press F9 on the loading screen to scan", vm.RailScanHint); // F9 scans
        Assert.True(vm.HasGlobalHotkey);
        Assert.Equal("F9", vm.GlobalHotkeyText);
        Assert.True(vm.GlobalHotkeyUnavailable);
        Assert.Equal("Global preset hotkey unavailable", vm.RailGlobalHint);
        Assert.Equal("Global preset hotkey F9, unavailable", vm.RailGlobalHotkeyName);

        // moving detection off F9 frees it for the Global preset: the rail presents it as working
        vm.BeginHotkeyCapture();
        Assert.True(vm.CaptureHotkey(Key.F7, ModifierKeys.None));
        Assert.True(h.GlobalPresetHotkey.Holds(F9));
        Assert.False(vm.GlobalHotkeyUnavailable);
        Assert.Equal("Global preset", vm.RailGlobalHint);
        Assert.Equal("Press F7 on the loading screen to scan", vm.RailScanHint);
    }

    [Fact]
    public void ADetectionHotkeyThatCannotBeRegistered_IsShownUnavailableInTheRail()
    {
        var (h, vm, _) = ViewModel(new ControllerHarnessOptions
        {
            Configure = c => c.DetectionHotkey = Combo("F20"),
            HotkeysHeldElsewhere = new[] { Combo("F20") },
        });
        Assert.False(h.Hotkey.IsRegistered);

        Assert.True(vm.HotkeyUnavailable);
        Assert.Equal("Scan hotkey unavailable", vm.RailScanHint); // never "Press Ctrl+Alt+F20 …": it can't reach the app
        Assert.Equal("Detection hotkey Ctrl+Alt+F20, unavailable", vm.RailHotkeyName);
        Assert.False(vm.GlobalHotkeyUnavailable);
        Assert.Equal("Global preset", vm.RailGlobalHint);
    }

    public static TheoryData<string, bool> ResetDuringCaptures => new()
    {
        { "detection", false }, // capture, then Reset settings → Reset settings
        { "global", false },
        { "detection", true }, // Reset settings, then a capture while the confirmation is open, then confirm
        { "global", true },
    };

    [Theory]
    [MemberData(nameof(ResetDuringCaptures))]
    public void ResetSettings_ClickedDuringAHotkeyCapture_EndsTheCapture_AndBothDefaultHotkeysWork(string which, bool captureWhileConfirming)
    {
        var (h, vm, _) = ViewModel();
        void BeginCapture()
        {
            if (which == "global")
            {
                vm.BeginGlobalHotkeyCaptureCommand.Execute(null);
            }
            else
            {
                vm.BeginHotkeyCaptureCommand.Execute(null);
            }

            Assert.True(vm.IsCapturingHotkey);
            Assert.False(h.Hotkey.IsRegistered); // both are paused for the capture
            Assert.False(h.GlobalPresetHotkey.IsRegistered);
        }

        if (!captureWhileConfirming)
        {
            BeginCapture();
        }

        vm.ResetSettingsCommand.Execute(null); // (clicked: the window keeps the focus, nothing cancels the capture for it)
        Assert.True(vm.IsResetConfirming);
        if (captureWhileConfirming)
        {
            BeginCapture();
        }

        vm.ConfirmResetCommand.Execute(null);

        // the reset registered both default hotkeys: no capture may still claim the keys (F8 / F9 now reach Windows'
        // hotkey, never the capture)
        Assert.True(h.Hotkey.Holds(F8));
        Assert.True(h.GlobalPresetHotkey.Holds(F9));
        Assert.False(vm.IsCapturingHotkey);
        Assert.False(vm.HotkeyCaptureMode);
        Assert.False(vm.GlobalHotkeyCaptureMode);
        Assert.Equal("Change hotkey", vm.HotkeyButtonText);
        Assert.Equal("Change hotkey", vm.GlobalHotkeyButtonText);
        Assert.False(vm.HandleCaptureKey(Key.F7, ModifierKeys.None, onCaptureButton: false)); // a key is just a key again
        Assert.Equal("F8", h.Controller.Config.DetectionHotkey.ToString());
        Assert.Equal("F9", h.Controller.Config.GlobalHotkey?.ToString());
    }

    // ---------- the Status screen ----------

    [Fact]
    public async Task ARealmOnlyRead_ThePlateShowsTheRealm_NeverAMapTheScanDidNotRead()
    {
        var (h, vm, _) = ViewModel();
        h.Ocr.Text = "COLDWIND FARM"; // only the realm line is legible

        await h.Controller.TriggerDetectionAsync();

        Assert.Equal("Coldwind Farm", h.Controller.LastDetection?.RealmName);
        Assert.Null(h.Controller.LastDetection?.MapName);
        StatusScreen screen = vm.StatusScreen;
        Assert.Contains(screen.State, new[] { ScreenState.Active, ScreenState.WaitingFocus });
        Assert.False(screen.Plate.IsMap); // no picture passed off as the detected map
        Assert.Equal(PlateScene.Lobby(FireState.Burning, "Coldwind Farm", MainViewModel.RealmOnlyPlateLine), screen.Plate);
        Assert.Equal("Coldwind Farm", screen.DetectedRealm);
        Assert.Null(screen.BackdropMap);
        Assert.Null(vm.BackdropMap);

        // Presets selects the realm; its hero is a map to browse, never presented as the detected one
        Assert.Equal("Coldwind Farm", vm.SelectedRealmRow?.RealmName);
        Assert.False(vm.HeroIsDetected);
        Assert.All(vm.Thumbs, thumb => Assert.False(thumb.IsDetected, thumb.Name));

        // a read with the map names it
        h.Ocr.Text = "FRACTURED COWSHED\nCOLDWIND FARM";
        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(PlateScene.Map("Coldwind Farm", "Fractured Cowshed"), vm.StatusScreen.Plate);
        Assert.Equal("Fractured Cowshed", vm.StatusScreen.BackdropMap);
        Assert.Equal("Coldwind Farm", vm.StatusScreen.DetectedRealm);
    }

    [Fact]
    public void BeforeAnyRead_ThePlateNamesNoDetectedRealm()
    {
        var (_, vm, _) = ViewModel();
        Assert.Null(vm.StatusScreen.DetectedRealm);
        Assert.False(vm.StatusScreen.Plate.IsMap);
    }

    [Fact]
    public async Task StatusScreen_TellsTheStates_WithTheAppsRealTexts()
    {
        var (h, vm, _) = ViewModel();
        StatusScreen ready = vm.StatusScreen;
        Assert.Equal(ScreenState.Ready, ready.State);
        Assert.Equal("Ready", ready.Hero);
        Assert.Equal(HeroSize.XL, ready.HeroSize);
        Assert.False(ready.Plate.IsMap);
        Assert.Equal(FireState.Burning, ready.Plate.Fire);
        Assert.Equal(SkillCheckState.Hidden, ready.SkillCheck);
        Assert.Equal("Ctrl+Alt+F20", ready.HotkeyText);
        Assert.Equal(0.5, ready.EmberLevel);
        Assert.Null(vm.BackdropMap);

        // the map is read, but the player left the game: waiting for focus
        h.UnfocusRightAfterTheRead();
        await h.Controller.TriggerDetectionAsync();
        StatusScreen waiting = vm.StatusScreen;
        Assert.Equal(ScreenState.WaitingFocus, waiting.State);
        Assert.Equal("Map found — waiting for you to click into the game", waiting.Hero);
        Assert.Equal(HeroSize.M, waiting.HeroSize);
        Assert.Equal(PlateScene.Map("Coldwind Farm", "Fractured Cowshed"), waiting.Plate);
        Assert.Equal(PlateTone.Waiting, waiting.Tone);
        Assert.Equal(PlateTag.WaitingForFocus, waiting.Tag);
        Assert.Equal(PresetTail.Pending, waiting.Tail);
        Assert.Equal(Path.GetFileName(h.ColdwindIni), waiting.TailSubject);
        Assert.True(waiting.Leaning);
        Assert.Equal("Fractured Cowshed", vm.BackdropMap);

        // back in the game: switched
        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation());
        StatusScreen active = vm.StatusScreen;
        Assert.Equal(ScreenState.Active, active.State);
        Assert.Equal("Preset active", active.Hero);
        Assert.Equal(PlateTone.Live, active.Tone);
        Assert.Equal(Path.GetFileName(h.ColdwindIni), active.ActiveFile);
        Assert.Equal(1.0, active.EmberLevel);
        Assert.NotEqual(string.Empty, active.LastSwitchTime);
        Assert.Contains("Coldwind Farm", active.LastSwitchText);
        Assert.False(active.Leaning);

        // the Presets tab shows the realm's hero picture behind the fog; Settings shows none
        vm.SelectedTab = AppTab.Presets;
        Assert.Equal("Fractured Cowshed", vm.BackdropMap);
        vm.SelectedTab = AppTab.Settings;
        Assert.Null(vm.BackdropMap);
    }

    [Fact]
    public async Task StatusScreen_ScanningAndPaused_ShowTheSkillCheck()
    {
        var (h, vm, _) = ViewModel();
        h.Focused = false; // the scan pauses at once
        Task scan = h.Controller.TriggerDetectionAsync();
        await ControllerHarness.WaitUntilAsync(() => h.Controller.IsScanPaused, "the pause");
        vm.Refresh();
        StatusScreen paused = vm.StatusScreen;
        Assert.Equal(ScreenState.Paused, paused.State);
        Assert.Equal("Scan paused", paused.Hero);
        Assert.Equal(SkillCheckState.Paused, paused.SkillCheck);
        Assert.Equal(DetectionDot.Ring, paused.DetectionDot);
        Assert.Equal(FireState.Embers, paused.Plate.Fire);

        h.Ocr.Text = string.Empty; // back in the game, nothing readable yet: scanning
        h.Focused = true;
        await ControllerHarness.WaitUntilAsync(() => !h.Controller.IsScanPaused && h.Controller.DetectionText.Contains("last read", StringComparison.Ordinal), "reading");
        vm.Refresh();
        StatusScreen scanning = vm.StatusScreen;
        Assert.Equal(ScreenState.Scanning, scanning.State);
        Assert.Equal("Reading the loading screen", scanning.Hero);
        Assert.Equal(SkillCheckState.Scanning, scanning.SkillCheck);
        Assert.Equal(DetectionDot.Ember, scanning.DetectionDot);
        Assert.True(scanning.Leaning);

        await h.Controller.TriggerDetectionAsync(); // stop
        await scan.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SkillCheckState.Hidden, vm.StatusScreen.SkillCheck);
    }

    [Fact]
    public void StatusScreen_WaitingForTheGame_IsCold()
    {
        var (_, vm, _) = ViewModel(new ControllerHarnessOptions { DbdRunningAtStart = false, Configure = c => c.DetectionHotkey = Combo("F20") });
        StatusScreen waiting = vm.StatusScreen;
        Assert.Equal(ScreenState.Waiting, waiting.State);
        Assert.Equal("Waiting for Dead by Daylight", waiting.Hero);
        Assert.Equal(FireState.Out, waiting.Plate.Fire);
        Assert.Equal(0, waiting.EmberLevel);
        Assert.Equal("Not running", vm.DbdStatus);
        Assert.False(vm.DbdRunning);
    }

    [Fact]
    public void ReturnToAutomatic_IsOfferedOnlyDuringAnOverride()
    {
        var (h, vm, _) = ViewModel();
        Assert.False(vm.ReturnToAutoCommand.CanExecute(null));
        vm.SelectedOverrideRealm = "Coldwind Farm";
        vm.ApplyOverrideCommand.Execute(null);
        Assert.True(vm.IsManualOverride);
        Assert.True(vm.ReturnToAutoCommand.CanExecute(null));
        Assert.Equal(ScreenState.Override, vm.StatusScreen.State);
        Assert.Equal(PlateTone.Override, vm.StatusScreen.Tone);
        Assert.Equal(PlateTag.Override, vm.StatusScreen.Tag);
        Assert.Equal(PresetTail.Override, vm.StatusScreen.Tail);
        Assert.Equal("Coldwind Farm", vm.StatusScreen.TailSubject);

        vm.ReturnToAutoCommand.Execute(null);
        Assert.False(vm.IsManualOverride);
        Assert.False(vm.ReturnToAutoCommand.CanExecute(null));
    }

    // ---------- the Presets screen ----------

    [Fact]
    public async Task Presets_ADetection_SelectsItsRealm_AndShowsItsMap()
    {
        var (h, vm, _) = ViewModel();
        Assert.Same(vm.RealmRows[0], vm.SelectedRow); // the first realm until something is detected
        Assert.True(vm.RealmRows[0].IsSelected);

        await h.Controller.TriggerDetectionAsync(); // Fractured Cowshed → Coldwind Farm, switched

        RealmRow coldwind = vm.RealmRows.Single(r => r.RealmName == "Coldwind Farm");
        Assert.Same(coldwind, vm.SelectedRow);
        Assert.Equal("Coldwind Farm", vm.DetailTitle);
        Assert.Equal("5 maps", vm.DetailCountText);
        Assert.Equal((RowMark.Ember, "Active realm"), (vm.DetailTagKind, vm.DetailTagText));
        Assert.Equal(RowMark.Ember, coldwind.Mark);
        Assert.True(vm.DetailIsLive);
        Assert.Equal("Fractured Cowshed", vm.HeroMap);
        Assert.True(vm.HeroIsDetected);
        Assert.Equal(5, vm.Thumbs.Count);
        Assert.Equal(5, vm.ThumbColumns);
        Assert.False(vm.ThumbsArePair);
        Assert.Single(vm.Thumbs, t => t.IsDetected);
        Assert.Single(vm.Thumbs, t => t.IsSelected);
        Assert.Equal(Path.GetFileName(h.ColdwindIni), vm.DetailPresetText);
        Assert.Equal("Shortcut", vm.DetailShortcutPrefix);
        Assert.StartsWith("Ctrl+", vm.DetailShortcutValue);

        // another map as the hero picture (and behind the fog)
        MapThumb other = vm.Thumbs.First(t => !t.IsSelected);
        other.SelectCommand.Execute(null);
        Assert.Equal(other.Name, vm.HeroMap);
        Assert.False(vm.HeroIsDetected);
        vm.SelectedTab = AppTab.Presets;
        Assert.Equal(other.Name, vm.BackdropMap);

        // Global / Menu
        vm.SelectRowCommand.Execute(vm.GlobalRow);
        Assert.True(vm.DetailIsGlobal);
        Assert.Null(vm.HeroMap);
        Assert.Empty(vm.Thumbs);
        Assert.Null(vm.BackdropMap);
        Assert.Equal("Global / Menu", vm.DetailTitle);
        Assert.False(coldwind.IsSelected);
        Assert.True(vm.GlobalRow.IsSelected);
    }

    [Fact]
    public void Presets_Filter_MatchesRealmsAndMaps_IgnoringCaseAndAccents()
    {
        var (_, vm, _) = ViewModel();
        vm.FilterText = "cowshed";
        RealmRow coldwind = Assert.Single(vm.RealmRows, r => r.IsVisible);
        Assert.Equal("Coldwind Farm", coldwind.RealmName);
        Assert.True(coldwind.IsMetaHit);
        Assert.Equal("Map: Fractured Cowshed", coldwind.MetaText);

        vm.FilterText = "lerys"; // Léry's Memorial Institute
        Assert.Equal("Léry's Memorial Institute", Assert.Single(vm.RealmRows, r => r.IsVisible).RealmName);

        vm.FilterText = "zzz";
        Assert.False(vm.HasFilterMatches);
        Assert.Equal("No realm or map matches “zzz”.", vm.FilterEmptyText);

        vm.ClearFilterCommand.Execute(null);
        Assert.True(vm.HasFilterMatches);
        Assert.All(vm.RealmRows, r => Assert.True(r.IsVisible));
        Assert.Equal("5 maps", coldwind.MetaText);
    }

    [Fact]
    public void Presets_Test_ShowsTheCountdown_WhileTheControllerWaitsForFocus()
    {
        var (h, vm, _) = ViewModel();
        vm.SelectRow(vm.RealmRows.Single(r => r.RealmName == "Coldwind Farm"));
        h.Focused = false; // the user clicked Test in the app: DBD is not focused

        vm.TestSelectedCommand.Execute(null);

        PendingTest? pending = h.Controller.PendingTest;
        Assert.NotNull(pending);
        Assert.Equal("Coldwind Farm", pending!.Name);
        Assert.Equal(FeedbackKind.Test, vm.Feedback.Kind);
        Assert.Equal(Path.GetFileName(h.ColdwindIni), vm.Feedback.File);
        Assert.True(vm.Feedback.Elapsed < TimeSpan.FromSeconds(1));

        h.Advance(TimeSpan.FromSeconds(4));
        vm.Refresh(); // the running ring keeps its start
        Assert.Equal(FeedbackKind.Test, vm.Feedback.Kind);

        vm.SelectRow(vm.GlobalRow); // the ring belongs to its realm
        Assert.Equal(FeedbackKind.None, vm.Feedback.Kind);
        vm.SelectRow(vm.RealmRows.Single(r => r.RealmName == "Coldwind Farm"));
        Assert.Equal(FeedbackKind.Test, vm.Feedback.Kind);

        h.Focused = true; // the player clicks into the game
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.Null(h.Controller.PendingTest);
        Assert.Equal(FeedbackKind.Message, vm.Feedback.Kind);
        Assert.StartsWith("Test: switched to", vm.Feedback.Text);
    }

    [Fact]
    public void Presets_Test_ThatGivesUp_SaysSo()
    {
        var (h, vm, _) = ViewModel();
        vm.SelectRow(vm.RealmRows.Single(r => r.RealmName == "Coldwind Farm"));
        h.Focused = false;
        vm.TestSelectedCommand.Execute(null);
        Assert.Equal(FeedbackKind.Test, vm.Feedback.Kind);

        h.Advance(TimeSpan.FromSeconds(16));
        h.Controller.RetryPendingActivation(); // the worker's next look: the 15 s are over

        Assert.Equal(FeedbackKind.Message, vm.Feedback.Kind);
        Assert.True(vm.Feedback.Text == "No click into the game within 15 s, so nothing switched. Press Test to try again.", h.Controller.StatusMessage + " | " + vm.Feedback.Text);
    }

    [Fact]
    public void Presets_Clear_AsksFirst_ThenClears_AndTheRowGoesIntoTheFog()
    {
        var (h, vm, _) = ViewModel();
        RealmRow coldwind = vm.RealmRows.Single(r => r.RealmName == "Coldwind Farm");
        vm.SelectRow(coldwind);
        Assert.False(coldwind.IsUnset);
        Assert.True(vm.ClearSelectedCommand.CanExecute(null));

        vm.ClearSelectedCommand.Execute(null);
        Assert.Equal(FeedbackKind.ClearConfirm, vm.Feedback.Kind);
        Assert.Equal("Clear the preset for Coldwind Farm? This realm will use Global / Menu.", vm.Feedback.Text);
        Assert.True(coldwind.HasPreset); // not yet

        vm.CloseFeedbackCommand.Execute(null); // Keep it
        Assert.Equal(FeedbackKind.None, vm.Feedback.Kind);
        Assert.True(coldwind.HasPreset);

        vm.ClearSelectedCommand.Execute(null);
        vm.ConfirmClearCommand.Execute(null);
        Assert.False(coldwind.HasPreset);
        Assert.True(coldwind.IsUnset);
        Assert.Equal("Uses Global / Menu", coldwind.PresetText);
        Assert.Equal("No preset — uses Global / Menu", vm.DetailPresetText);
        Assert.Equal("No shortcut", vm.DetailShortcutPrefix);
        Assert.Equal(FeedbackKind.Message, vm.Feedback.Kind);
        Assert.Equal("Cleared preset for Coldwind Farm (uses Global).", vm.Feedback.Text);
        Assert.False(vm.ClearSelectedCommand.CanExecute(null));
        Assert.Equal("Choose file", vm.ChangeFileText);
    }

    [Fact]
    public void Presets_WithoutAGlobalPreset_NoTextPromisesGlobal()
    {
        var (_, vm, _) = ViewModel(new ControllerHarnessOptions
        {
            Configure = c =>
            {
                c.DetectionHotkey = Combo("F20");
                c.GlobalPresetPath = null;
            },
        });
        RealmRow autohaven = vm.RealmRows.Single(r => r.RealmName == Autohaven);
        Assert.Equal("No preset; nothing switches", autohaven.PresetText);
        vm.SelectRow(autohaven);
        Assert.Equal("No preset — no Global preset is set, so nothing switches", vm.DetailPresetText);
        Assert.Equal("No preset", vm.GlobalRow.PresetText);
        Assert.True(vm.GlobalRow.IsUnset);
    }

    [Fact]
    public void RestartBanner_ShowInPresets_SelectsTheRealmThatNeedsTheRestart()
    {
        var (h, vm, _) = ViewModel();
        Assert.Null(h.Controller.SetRealmPreset(Autohaven, NewPresetFile(h, "Autohaven")));
        RealmRow autohaven = vm.RealmRows.Single(r => r.RealmName == Autohaven);
        Assert.True(autohaven.NeedsRestart);
        Assert.Equal(RowMark.Red, autohaven.Mark);
        vm.SelectRow(vm.GlobalRow);

        vm.ShowRestartInPresetsCommand.Execute(null);

        Assert.Equal(AppTab.Presets, vm.SelectedTab);
        Assert.Same(autohaven, vm.SelectedRow);
        Assert.True(vm.DetailNeedsRestart);
        Assert.Equal("assigned, not loaded yet", vm.DetailShortcutValue);
    }

    // ---------- the ReShade check ----------

    [Fact]
    public void ReshadeCheck_ReportLines_AreStyledOkInfoIssue()
    {
        Assert.Equal(new ReportLine(ReportLineKind.Ok, "ReShade hook DLL: dxgi.dll"), ReportLine.Parse("[OK] ReShade hook DLL: dxgi.dll"));
        Assert.Equal(new ReportLine(ReportLineKind.Info, "Shortcut entries: 20"), ReportLine.Parse("[--] Shortcut entries: 20"));
        Assert.Equal(new ReportLine(ReportLineKind.Issue, "ReShade.ini not found"), ReportLine.Parse("[!!] ReShade.ini not found"));
        Assert.Equal(new ReportLine(ReportLineKind.Result, "Result: setup looks correct."), ReportLine.Parse("Result: setup looks correct."));
        Assert.Equal(("OK", "Info", "Issue", ""), (ReportLine.Parse("[OK] a").Tag, ReportLine.Parse("[--] a").Tag, ReportLine.Parse("[!!] a").Tag, ReportLine.Parse("a").Tag));
        Assert.Equal(2, ReportLine.ParseReport("[OK] a\r\n\r\n[!!] b\n").Count);

        var (_, vm, _) = ViewModel();
        Assert.False(vm.CopyDiagnosticsCommand.CanExecute(null));
        vm.RunDiagnosticsCommand.Execute(null);
        Assert.NotEmpty(vm.DiagnosticsLines);
        Assert.Contains(vm.DiagnosticsLines, l => l.Kind is ReportLineKind.Ok or ReportLineKind.Issue);
        Assert.True(vm.CopyDiagnosticsCommand.CanExecute(null));
    }

    [Fact]
    public void ResetSettings_AsksInline_ThenResets()
    {
        var (h, vm, _) = ViewModel();
        vm.SoundEnabled = false;
        vm.ResetSettingsCommand.Execute(null);
        Assert.True(vm.IsResetConfirming);
        Assert.False(h.Controller.Config.SoundEnabled); // not yet

        vm.CancelResetCommand.Execute(null);
        Assert.False(vm.IsResetConfirming);

        vm.ResetSettingsCommand.Execute(null);
        vm.ConfirmResetCommand.Execute(null);
        Assert.False(vm.IsResetConfirming);
        Assert.True(h.Controller.Config.SoundEnabled);
        Assert.True(vm.SoundEnabled);
        Assert.Equal("Settings reset. Your preset files were not touched.", vm.ResetNote);
        Assert.Null(vm.ResetProblem);
    }

    private static AppConfig Reload(ControllerHarness h)
    {
        var service = new AppConfigService(h.ConfigPath);
        service.Load();
        return service.Config;
    }
}

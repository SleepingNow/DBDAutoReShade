using System.Collections.Specialized;
using System.Windows.Input;
using DBDAutoReShade.App.ViewModels;
using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// The view model over the real controller: no UI churn on every event (C21),
/// hotkey capture (C22), truthful texts (C24) and the restart banner (C25).
/// Part of <see cref="ControllerActivationTests"/> (sequential); the harness's
/// global hotkey is a fake, so nothing is registered with Windows.
/// </summary>
public sealed partial class ControllerActivationTests
{
    private (ControllerHarness Harness, MainViewModel ViewModel, List<string> Warnings) ViewModel(
        ControllerHarnessOptions? options = null)
    {
        ControllerHarness h = Harness(options ?? new ControllerHarnessOptions { Configure = c => c.DetectionHotkey = Combo("F20") });
        var warnings = new List<string>();
        var vm = new MainViewModel(h.Controller, warnings.Add);
        vm.Refresh();
        return (h, vm, warnings);
    }

    // ---------- C21: no rebuild on every event ----------

    [Fact]
    public void Refresh_UpdatesRowsInPlace_InsteadOfRebuildingThem()
    {
        var (h, vm, _) = ViewModel();
        List<RealmRow> rows = vm.RealmRows.ToList();
        List<string> realms = vm.AvailableRealms;
        Assert.Equal(h.Controller.Database.Realms.Count, rows.Count);
        int collectionChanges = 0;
        vm.RealmRows.CollectionChanged += (_, _) => collectionChanges++;
        int realmListChanges = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.AvailableRealms))
            {
                realmListChanges++;
            }
        };

        h.Controller.SetDebug(true); // any controller event (scan samples raise many)
        vm.Refresh();

        RealmRow autohavenRow = rows.Single(r => r.RealmName == Autohaven);
        Assert.False(autohavenRow.HasPreset);
        bool canTestBefore = autohavenRow.TestCommand.CanExecute(null);
        string autohaven = NewPresetFile(h, "Autohaven");
        Assert.Null(h.Controller.SetRealmPreset(Autohaven, autohaven)); // the event refreshes the view model

        Assert.Equal(0, collectionChanges);
        Assert.Equal(0, realmListChanges);
        Assert.Same(realms, vm.AvailableRealms); // the open override drop-down keeps its list
        Assert.Equal(rows, vm.RealmRows); // same row objects (same buttons)
        Assert.True(autohavenRow.HasPreset); // updated in place
        Assert.Equal(Path.GetFileName(autohaven), autohavenRow.PresetName);
        Assert.False(canTestBefore);
        Assert.True(autohavenRow.TestCommand.CanExecute(null));
    }

    // ---------- C25: restart banner ----------

    [Fact]
    public void RestartBanner_NamesThePresetsWaitingForADbdRestart()
    {
        var (h, vm, _) = ViewModel();
        Assert.False(vm.ShowRestartNotice);

        Assert.Null(h.Controller.SetRealmPreset(Autohaven, NewPresetFile(h, "Autohaven")));
        Assert.True(vm.ShowRestartNotice);
        Assert.Contains("Autohaven Wreckers", vm.RestartNoticeText);
        Assert.DoesNotContain("Coldwind", vm.RestartNoticeText);

        h.Dbd.ProcessId = 777; // DBD restarted: it loads the new shortcut
        h.Dbd.RaiseStatusChanged(running: true);
        Assert.False(vm.ShowRestartNotice);
        Assert.Equal(string.Empty, vm.RestartNoticeText);
    }

    // ---------- C22: hotkey capture ----------

    [Fact]
    public void HotkeyCapture_PausesTheGlobalHotkey_EscRestoresIt()
    {
        var (h, vm, warnings) = ViewModel();
        Assert.Equal("F20", h.Hotkey.RegisteredHotkey?.Key);

        vm.BeginHotkeyCapture();
        Assert.True(vm.HotkeyCaptureMode);
        Assert.False(h.Hotkey.IsRegistered); // pressing it now reaches the window
        Assert.False(h.Hotkey.Holds(Combo("F20")));

        Assert.False(vm.CaptureHotkey(Key.Escape, ModifierKeys.None));
        Assert.False(vm.HotkeyCaptureMode);
        Assert.Equal("F20", h.Hotkey.RegisteredHotkey?.Key);
        Assert.Equal("F20", h.Controller.Config.DetectionHotkey.Key);
        Assert.Empty(warnings);
    }

    [Fact]
    public void HotkeyCapture_CancelledByTheWindow_RestoresTheHotkey()
    {
        var (h, vm, _) = ViewModel();
        vm.BeginHotkeyCapture();

        vm.CancelHotkeyCapture(); // the window lost focus or closed

        Assert.False(vm.HotkeyCaptureMode);
        Assert.Equal("F20", h.Hotkey.RegisteredHotkey?.Key);
    }

    [Fact]
    public void HotkeyCapture_NewKey_BecomesTheHotkey()
    {
        var (h, vm, warnings) = ViewModel();
        vm.BeginHotkeyCapture();

        Assert.True(vm.CaptureHotkey(Key.F19, ModifierKeys.Control | ModifierKeys.Alt));

        Assert.False(vm.HotkeyCaptureMode);
        Assert.Equal("F19", h.Hotkey.RegisteredHotkey?.Key);
        Assert.Equal("F19", h.Controller.Config.DetectionHotkey.Key);
        Assert.Equal("Ctrl+Alt+F19", vm.HotkeyText);
        Assert.Empty(warnings);
    }

    [Fact]
    public void HotkeyCapture_UnsupportedKey_ExplainsTheRules()
    {
        var (h, vm, warnings) = ViewModel();
        vm.BeginHotkeyCapture();

        Assert.False(vm.CaptureHotkey(Key.Space, ModifierKeys.None));

        string warning = Assert.Single(warnings);
        Assert.Equal(MainViewModel.UnsupportedHotkeyMessage, warning);
        Assert.Contains("NumPad", warning);
        Assert.Contains("Ctrl, Alt or Shift", warning);
        Assert.DoesNotContain("A–Z or 0–9", warning);
        Assert.False(vm.HotkeyCaptureMode);
        Assert.Equal("F20", h.Hotkey.RegisteredHotkey?.Key);
    }

    [Fact]
    public void HotkeyCapture_BareLetter_IsRefused_TheOldHotkeyStays()
    {
        var (h, vm, warnings) = ViewModel();
        vm.BeginHotkeyCapture();

        Assert.False(vm.CaptureHotkey(Key.W, ModifierKeys.None));

        string warning = Assert.Single(warnings);
        Assert.Contains(HotkeyParser.ModifierRequiredError, warning);
        Assert.Contains("still Ctrl+Alt+F20", warning);
        Assert.DoesNotContain("saved", warning);
        Assert.Equal("F20", h.Hotkey.RegisteredHotkey?.Key);
        Assert.Equal("F20", h.Controller.Config.DetectionHotkey.Key);
    }

    // ---------- C24 / C16: texts and the manifest URL ----------

    [Fact]
    public void HelpTexts_UseTheConfiguredHotkey_AndSayWhenAnOverrideEnds()
    {
        var (_, vm, _) = ViewModel();

        Assert.Contains("Ctrl+Alt+F20", vm.ScanHelpText);
        Assert.Contains("Ctrl+Alt+F20", vm.OverrideHelpText);
        Assert.DoesNotContain("F8", vm.ScanHelpText + vm.OverrideHelpText);
        Assert.Contains("Return to automatic", vm.OverrideHelpText); // the Status button's label, as it reads
        Assert.Contains("only when the app is tracking the match", vm.OverrideHelpText);
    }

    [Fact]
    public void ClearingARealm_WithAGlobalPreset_SaysItUsesGlobal()
    {
        var (h, vm, _) = ViewModel();
        RealmRow coldwind = vm.RealmRows.Single(r => r.RealmName == "Coldwind Farm");
        Assert.Equal(RealmRow.ClearToolTipWithGlobal, coldwind.ClearToolTip);

        coldwind.ClearCommand.Execute(null);

        Assert.Equal("Cleared preset for Coldwind Farm (uses Global).", h.Controller.StatusMessage);
        Assert.False(coldwind.HasPreset);
    }

    [Fact]
    public async Task ClearingARealm_WithoutAGlobalPreset_SaysDetectingItSwitchesNothing()
    {
        // The owner's setup: no Global preset; ReShade.ini (CRLF, relative PresetPath) starts
        // with the user's own Ctrl+Shift+F9 → Universal, the app's entries follow.
        var (h, vm, _) = ViewModel(new ControllerHarnessOptions
        {
            Configure = c =>
            {
                c.GlobalPresetPath = null;
                c.DetectionHotkey = Combo("F20");
                File.WriteAllText(c.ReshadeIniPath!,
                    "[GENERAL]\r\nPresetPath=" + OwnersRelativeUniversal + "\r\n" +
                    "PresetShortcutKeys=120,1,1,0\r\nPresetShortcutPaths=" + OwnersRelativeUniversal + "\r\n");
            }
        });
        RealmRow coldwind = vm.RealmRows.Single(r => r.RealmName == "Coldwind Farm");
        Assert.Equal(RealmRow.ClearToolTipWithoutGlobal, coldwind.ClearToolTip);

        coldwind.ClearCommand.Execute(null);

        Assert.Equal(
            "Cleared preset for Coldwind Farm; no Global preset is set, so detecting Coldwind Farm switches nothing.",
            h.Controller.StatusMessage);

        // And so it is: detecting Coldwind now sends nothing.
        await h.Controller.TriggerDetectionAsync();
        Assert.StartsWith("Detected Coldwind Farm, but no preset is assigned and no Global preset is set.", h.Controller.StatusMessage);
        Assert.Equal(0, h.Keys.CallCount);
    }

    [Fact]
    public void ClearButtonToolTip_FollowsTheGlobalPreset()
    {
        var (h, vm, _) = ViewModel();
        RealmRow coldwind = vm.RealmRows.Single(r => r.RealmName == "Coldwind Farm");
        Assert.Equal(RealmRow.ClearToolTipWithGlobal, coldwind.ClearToolTip);

        Assert.Null(h.Controller.SetGlobalPreset(null)); // the event refreshes the view model
        Assert.Equal(RealmRow.ClearToolTipWithoutGlobal, coldwind.ClearToolTip);

        Assert.Null(h.Controller.SetGlobalPreset(h.GlobalIni));
        Assert.Equal(RealmRow.ClearToolTipWithGlobal, coldwind.ClearToolTip);
    }

    [Fact]
    public void ManifestUrl_InvalidIsShownAndKept_NotSaved()
    {
        var (h, vm, _) = ViewModel();

        vm.ManifestUrl = "http://example.com/manifest.json";
        Assert.NotNull(vm.ManifestUrlError);
        Assert.Null(h.Controller.Config.DatabaseUpdateManifestUrl);
        vm.Refresh();
        Assert.Equal("http://example.com/manifest.json", vm.ManifestUrl); // left for the user to fix

        vm.ManifestUrl = "https://example.com/manifest.json";
        Assert.Null(vm.ManifestUrlError);
        Assert.Equal("https://example.com/manifest.json", h.Controller.Config.DatabaseUpdateManifestUrl);

        // A later invalid draft also survives refreshes (the saved URL above is not "news").
        vm.ManifestUrl = "ftp://example.com/manifest.json";
        vm.Refresh();
        Assert.Equal("ftp://example.com/manifest.json", vm.ManifestUrl);
        Assert.NotNull(vm.ManifestUrlError);
        Assert.Equal("https://example.com/manifest.json", h.Controller.Config.DatabaseUpdateManifestUrl);
    }

    [Fact]
    public void ManifestUrl_InvalidDraft_IsDroppedWhenSettingsAreReset()
    {
        var (h, vm, _) = ViewModel();
        vm.ManifestUrl = "http://example.com/manifest.json";
        Assert.NotNull(vm.ManifestUrlError);

        h.Controller.ResetSettings(); // the stored URL was and stays unset; the draft still goes

        Assert.Null(vm.ManifestUrlError);
        Assert.Equal(string.Empty, vm.ManifestUrl);
    }

    [Fact]
    public void ManifestUrl_InvalidDraft_GivesWayToAUrlStoredElsewhere()
    {
        var (h, vm, _) = ViewModel();
        vm.ManifestUrl = "http://example.com/manifest.json";
        Assert.NotNull(vm.ManifestUrlError);

        Assert.Null(h.Controller.SetManifestUrl("https://example.org/manifest.json")); // e.g. the setup wizard

        Assert.Null(vm.ManifestUrlError);
        Assert.Equal("https://example.org/manifest.json", vm.ManifestUrl);
    }
}

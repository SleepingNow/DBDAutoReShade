using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DBDAutoReShade.App;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.ViewModels;
using DBDAutoReShade.App.Views;
using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Tests.Campfire;

/// <summary>
/// The REAL main window (off screen, never activated; nothing is placed on the desktop and no input reaches Windows)
/// for assistive technology and the keyboard: the outcomes shown inline speak (their live regions raise
/// LiveRegionChanged; a banner speaks once per change), an action that hides or disables the focused control hands
/// the focus on (also across sections), Tab enters the rail at the section shown, the rail's groups are named for
/// screen readers, and one name is used for the Global preset hotkey. Buttons are pressed through their automation
/// peers (as a screen reader does); focus is the window's logical focus, which an inactive window keeps.
/// </summary>
public sealed class AccessibilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-a11y-" + Guid.NewGuid().ToString("N"));

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

    // ---------- live regions ----------

    [Fact]
    public void SettingsOutcomes_AreAnnounced_EachTime_OnlyWhileTheirPageIsShown()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "settings-live"), "ready");
            using var heard = new Announcements();
            try
            {
                MainViewModel vm = scene.ViewModel;
                var settings = (SettingsView)scene.Window.FindName("SettingsPage");
                vm.ClipboardWriter = _ => { };
                vm.RunDiagnostics();

                // Copy report on the Status tab: the Settings page is hidden, so its note says nothing
                vm.CopyDiagnostics();
                CampfireTestHost.Pump();
                TextBlock copyNote = TextOn(settings, "Report copied.");
                Assert.Equal(0, heard.Count(copyNote));

                // on Settings, every copy is told (the same "Report copied." again too)
                vm.SelectedTab = AppTab.Settings;
                CampfireTestHost.Pump();
                vm.CopyDiagnostics();
                CampfireTestHost.Pump();
                Assert.Equal(1, heard.Count(copyNote));
                vm.CopyDiagnostics();
                CampfireTestHost.Pump();
                Assert.Equal(2, heard.Count(copyNote));

                // the hotkey capture: its help appears, then "Hotkey unchanged." when it is cancelled
                vm.BeginHotkeyCapture();
                CampfireTestHost.Pump();
                Assert.Equal(1, heard.Count(TextOn(settings, MainViewModel.CaptureHelpText)));
                vm.CancelHotkeyCapture();
                CampfireTestHost.Pump();
                Assert.Equal(1, heard.Count(TextOn(settings, "Hotkey unchanged.")));

                // Global preset hotkey cleared
                vm.ClearGlobalHotkey();
                CampfireTestHost.Pump();
                Assert.Equal(1, heard.Count(TextOn(settings, "Global preset hotkey cleared.")));

                // a Behaviour setting that can't be saved: the problem is told, and told again when it happens again
                File.Delete(scene.Harness.ConfigPath);
                Directory.CreateDirectory(scene.Harness.ConfigPath); // the config can no longer be written
                vm.DebugMode = true;
                CampfireTestHost.Pump();
                Assert.NotNull(vm.BehaviourProblem);
                TextBlock problem = TextOn(settings, vm.BehaviourProblem!);
                Assert.Equal(1, heard.Count(problem));
                vm.DebugMode = false;
                CampfireTestHost.Pump();
                Assert.Equal(2, heard.Count(problem));
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void StatusLine_AndPresetsFeedback_AreAnnounced()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "status-live"), "active");
            using var heard = new Announcements();
            try
            {
                MainViewModel vm = scene.ViewModel;
                var status = (StatusView)scene.Window.FindName("StatusPage");
                var message = (CrossfadeText)status.FindName("MessageText");
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(message));
                string before = message.Text;

                // the override changes the status line: it speaks (a crossfading text has a peer to speak with)
                vm.SelectedOverrideRealm = "Coldwind Farm";
                vm.ApplyOverrideCommand.Execute(null);
                WindowScene.PumpUntil(() => message.Text != before, "the status line changed");
                Assert.True(heard.Count(message) >= 1, "the status line was announced");
                Assert.Equal(AutomationControlType.Text, UIElementAutomationPeer.CreatePeerForElement(message)!.GetAutomationControlType());

                // Presets: the inline clear question speaks
                vm.SelectedTab = AppTab.Presets;
                CampfireTestHost.Pump();
                vm.SelectRow(vm.RealmRows.First(r => r.RealmName == "Autohaven Wreckers"));
                vm.ClearSelectedCommand.Execute(null);
                CampfireTestHost.Pump();
                var presets = (PresetsView)scene.Window.FindName("PresetsPage");
                var question = (TextBlock)presets.FindName("ClearQuestionText");
                Assert.False(string.IsNullOrEmpty(question.Text));
                Assert.Equal(1, heard.Count(question));
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void HotkeyBanner_SpeaksOncePerChange_WhenItOpens_AndWhenItsMessageChanges()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "banner-live"), "ready");
            using var heard = new Announcements();
            try
            {
                MainViewModel vm = scene.ViewModel;
                var status = (StatusView)scene.Window.FindName("StatusPage");
                var banner = (RestartBanner)status.FindName("HotkeyBanner");

                // another program takes F9: the Global preset hotkey can't be registered again, and the banner opens
                // (its binding and the window's drop-in are one announcement)
                FailToRegister(scene, vm, "F9");
                Assert.True(banner.IsOpen);
                Assert.Equal(1, heard.Count(banner));

                // then F8 too: the open banner's message changes, and that is told
                string first = banner.Text;
                FailToRegister(scene, vm, "F8");
                Assert.NotEqual(first, banner.Text);
                Assert.Equal(2, heard.Count(banner));
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    // ---------- the focus follows ----------

    [Fact]
    public void ShowInPresets_TheFocusGoesToTheRealmShown()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "focus-presets"), "restartNeeded");
            try
            {
                MainViewModel vm = scene.ViewModel;
                var status = (StatusView)scene.Window.FindName("StatusPage");
                Button action = Descendants<Button>((RestartBanner)status.FindName("RestartBanner")).Single();
                FocusManager.SetFocusedElement(scene.Window, action);

                Press(action);
                Assert.Equal(AppTab.Presets, vm.SelectedTab);
                Assert.Equal("Haddonfield", vm.SelectedRealmRow?.RealmName);
                var presets = (PresetsView)scene.Window.FindName("PresetsPage");
                var list = (ListBox)presets.FindName("RealmList");
                var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(vm.SelectedRealmRow);
                Assert.True(row.IsVisible);
                Assert.True(CampfireAssist.HoldsFocus(row), "the focus is on the realm shown, not on the window: " + FocusName(scene.Window));
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void OpenSettings_TheFocusGoesToTheHotkeyToChange()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "focus-settings"), "ready");
            try
            {
                MainViewModel vm = scene.ViewModel;
                FailToRegister(scene, vm, "F9");
                var status = (StatusView)scene.Window.FindName("StatusPage");
                Button action = Descendants<Button>((RestartBanner)status.FindName("HotkeyBanner")).Single();
                FocusManager.SetFocusedElement(scene.Window, action);

                Press(action);
                Assert.Equal(AppTab.Settings, vm.SelectedTab);
                var settings = (SettingsView)scene.Window.FindName("SettingsPage");
                Assert.True(CampfireAssist.HoldsFocus((UIElement)settings.FindName("GlobalHotkeyButton")),
                    "only the Global preset hotkey failed: its Change hotkey has the focus, not the window: " + FocusName(scene.Window));
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void ActionsThatHideOrDisableThemselves_HandTheFocusOn()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "focus-leave"), "active");
            try
            {
                MainViewModel vm = scene.ViewModel;
                var status = (StatusView)scene.Window.FindName("StatusPage");
                var settings = (SettingsView)scene.Window.FindName("SettingsPage");
                var presets = (PresetsView)scene.Window.FindName("PresetsPage");

                // "Return to automatic" disables itself: the focus goes to the realm list of the override
                vm.SelectedOverrideRealm = "Coldwind Farm";
                vm.ApplyOverrideCommand.Execute(null);
                var back = (Button)status.FindName("ReturnToAutoButton");
                WindowScene.PumpUntil(() => back.IsEnabled, "an override to return from");
                FocusManager.SetFocusedElement(scene.Window, back);
                Press(back);
                WindowScene.PumpUntil(() => !back.IsEnabled, "back to automatic");
                CampfireTestHost.Pump();
                Assert.True(CampfireAssist.HoldsFocus((UIElement)status.FindName("OverrideRealm")), "Return to automatic → " + FocusName(scene.Window));

                // Settings: the Global preset hotkey's Clear disables itself: the focus goes to its Change hotkey
                vm.SelectedTab = AppTab.Settings;
                CampfireTestHost.Pump();
                var clear = (Button)settings.FindName("ClearGlobalHotkeyButton");
                Assert.True(clear.IsEnabled);
                FocusManager.SetFocusedElement(scene.Window, clear);
                Press(clear);
                Assert.False(clear.IsEnabled);
                Assert.True(CampfireAssist.HoldsFocus((UIElement)settings.FindName("GlobalHotkeyButton")), "Clear → " + FocusName(scene.Window));

                // Presets: Clear filter hides itself: the focus goes to the filter box
                vm.SelectedTab = AppTab.Presets;
                vm.FilterText = "no such realm";
                CampfireTestHost.Pump();
                var clearFilter = (Button)presets.FindName("ClearFilterButton");
                Assert.True(clearFilter.IsVisible);
                FocusManager.SetFocusedElement(scene.Window, clearFilter);
                Press(clearFilter);
                Assert.False(clearFilter.IsVisible);
                Assert.True(CampfireAssist.HoldsFocus((UIElement)presets.FindName("Filter")), "Clear filter → " + FocusName(scene.Window));
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    // ---------- the rail ----------

    [Fact]
    public void Tab_EntersTheRail_AtTheSectionShown_HoweverItWasChosen()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "rail-tab"), "restartNeeded");
            try
            {
                MainViewModel vm = scene.ViewModel;
                Window window = scene.Window;
                var nav = (FrameworkElement)window.FindName("Nav");
                void AssertRailStop(string tab, string after)
                {
                    List<DependencyObject> order = MainWindowLayoutTests.TabOrder(window);
                    string seen = MainWindowLayoutTests.Describe(order);
                    DependencyObject railStop = Assert.Single(order, nav.IsAncestorOf);
                    Assert.True(ReferenceEquals(window.FindName(tab), railStop), $"after {after}, Tab enters the rail at {tab}: {seen}");
                }

                AssertRailStop("StatusTab", "start");

                // the restart banner's "Show in Presets"
                vm.ShowRestartInPresets();
                CampfireTestHost.Pump();
                AssertRailStop("PresetsTab", "Show in Presets");

                // Shift+Tab from the page's first control comes back to the section shown too
                var filter = (UIElement)((PresetsView)window.FindName("PresetsPage")).FindName("Filter");
                Assert.Same(window.FindName("PresetsTab"), MainWindowLayoutTests.PreviousTab(filter));

                // a rail item that had the focus before does not keep the stop once another section is shown
                FocusManager.SetFocusedElement(window, (IInputElement)window.FindName("PresetsTab"));
                vm.SelectedTab = AppTab.Settings; // e.g. the hotkey banner's "Open Settings"
                CampfireTestHost.Pump();
                AssertRailStop("SettingsTab", "Open Settings");
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    [Fact]
    public void Rail_SectionsAndFooter_AreNamedGroups()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "rail-groups"), "ready");
            try
            {
                Window window = scene.Window;
                AutomationPeer sections = PeerOf((UIElement)window.FindName("Nav"));
                Assert.Equal(AutomationControlType.Group, sections.GetAutomationControlType());
                Assert.Equal("Sections", sections.GetName());
                Assert.True(sections.IsControlElement());
                Assert.Equal(new[] { "Status", "Presets", "Settings" },
                    sections.GetChildren().Where(p => p.GetAutomationControlType() == AutomationControlType.RadioButton).Select(p => p.GetName()));

                AutomationPeer glance = PeerOf((UIElement)window.FindName("RailFoot"));
                Assert.Equal(AutomationControlType.Group, glance.GetAutomationControlType());
                Assert.Equal("At a glance", glance.GetName());
                Assert.Contains("Dead by Daylight", Flatten(glance).Select(p => p.GetName()));

                // the window reaches the sections through their group (they are no longer its flat children)
                AutomationPeer windowPeer = PeerOf(window);
                Assert.Contains(sections, Flatten(windowPeer));
                Assert.DoesNotContain(Flatten(windowPeer).Where(p => ReferenceEquals(p.GetParent(), windowPeer)),
                    p => p.GetAutomationControlType() == AutomationControlType.RadioButton);
            }
            finally
            {
                scene.Dispose();
            }
        });
    }

    // ---------- names ----------

    [Fact]
    public void TheGlobalPresetHotkey_HasOneName_AndTheWizardSaysWhatItShows()
    {
        CampfireTestHost.RunSta(() =>
        {
            WindowScene scene = WindowScene.Create(Path.Combine(_dir, "names"), "ready");
            var wizard = new SetupWizardWindow(scene.ViewModel, new MotionPolicy { AnimationsEnabled = false })
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -20000,
                Top = -20000,
            };
            try
            {
                wizard.Show();
                CampfireTestHost.Pump();
                var settings = (SettingsView)scene.Window.FindName("SettingsPage");
                List<string> texts = LogicalDescendants<TextBlock>(settings).Select(t => t.Text).ToList();
                Assert.Contains("Global preset hotkey", texts);
                Assert.DoesNotContain("Global hotkey", texts);

                // the wizard's window name (Alt+Tab, the taskbar, screen readers) is the title it shows
                Assert.Contains(wizard.Title, Descendants<TextBlock>(wizard).Select(t => t.Text));
            }
            finally
            {
                wizard.Close();
                scene.Dispose();
            }
        });
    }

    // ---------- helpers ----------

    /// <summary>
    /// Makes <paramref name="key"/> unavailable (another program holds it) and has the app register its hotkeys again
    /// (a hotkey capture cancelled): the one on that key fails, and the banner says so.
    /// </summary>
    private static void FailToRegister(WindowScene scene, MainViewModel vm, string key)
    {
        scene.Harness.Hotkey.HoldElsewhere(new DetectionHotkeyConfig { Key = key });
        string? before = vm.BannerError;
        vm.BeginHotkeyCapture();
        vm.CancelHotkeyCapture();
        WindowScene.PumpUntil(() => vm.BannerError is not null && vm.BannerError != before, $"{key} could not be registered");
        CampfireTestHost.Pump();
    }

    /// <summary>Presses <paramref name="button"/> as assistive technology does (its Invoke pattern), and lets everything it started run.</summary>
    private static void Press(Button button)
    {
        var invoke = (IInvokeProvider)PeerOf(button).GetPattern(PatternInterface.Invoke)!;
        invoke.Invoke();
        CampfireTestHost.Pump();
    }

    private static AutomationPeer PeerOf(UIElement element) =>
        UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element)
        ?? throw new Xunit.Sdk.XunitException($"{element} has no automation peer.");

    private static IEnumerable<AutomationPeer> Flatten(AutomationPeer peer)
    {
        foreach (AutomationPeer child in peer.GetChildren() ?? new List<AutomationPeer>())
        {
            yield return child;
            foreach (AutomationPeer inner in Flatten(child))
            {
                yield return inner;
            }
        }
    }

    /// <summary>The text block on <paramref name="page"/> (its XAML: a page never shown has no visual tree yet) saying <paramref name="text"/>; the shown one when several say it.</summary>
    private static TextBlock TextOn(DependencyObject page, string text)
    {
        List<TextBlock> saying = LogicalDescendants<TextBlock>(page).Where(t => t.Text == text).ToList();
        return saying.Count == 1 ? saying[0] : saying.Single(t => t.IsVisible);
    }

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (T inner in LogicalDescendants<T>(child))
            {
                yield return inner;
            }
        }
    }

    private static string FocusName(Window window)
    {
        object? focus = Keyboard.FocusedElement ?? FocusManager.GetFocusedElement(window);
        return focus is FrameworkElement { Name.Length: > 0 } named ? named.Name : focus?.GetType().Name ?? "nothing";
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T inner in Descendants<T>(child))
            {
                yield return inner;
            }
        }
    }

    /// <summary>The live-region announcements asked for while it is alive (on this test's thread).</summary>
    private sealed class Announcements : IDisposable
    {
        private readonly List<UIElement> _heard = new();

        public Announcements() => CampfireAutomation.AnnouncementRequested += OnAnnounced;

        public int Count(UIElement element) => _heard.Count(e => ReferenceEquals(e, element));

        public void Dispose() => CampfireAutomation.AnnouncementRequested -= OnAnnounced;

        private void OnAnnounced(object? sender, UIElement element) => _heard.Add(element);
    }
}

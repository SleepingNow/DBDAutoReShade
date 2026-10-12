using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Infrastructure;
using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.ViewModels;

namespace DBDAutoReShade.App;

/// <summary>
/// Main window: Status / Presets / Settings in the Campfire design. Closing it exits the app (no tray).
/// It is the stage of the Great (<see cref="IGreatStage"/>): when a map is found while the window is shown and active,
/// the skill check lands (with animations off as a still flash) and the rest of the screen waits until the dial has
/// lifted away (the Status screen shown is held, then the newest one is presented). It directs one orchestrated moment
/// per state change across the whole window (<see cref="CurrentMoment"/>; see MainWindow.Motion.cs). Motion follows its
/// <see cref="MotionPolicy"/>, which tracks this window: while it is minimized, hidden or inactive (the game in front)
/// nothing moves, ambient loops hold and changes are instant; a page that is not shown does not move at all. Opened centred, it is fitted to its monitor's work area (the 1280 x 800
/// design size when it fits); maximized, it is padded by the frame it overhangs the monitor with.
/// </summary>
public partial class MainWindow : Window, IGreatStage
{
    private readonly MainViewModel _viewModel;
    private readonly MotionPolicy _policy;
    private readonly IDisposable _policyAttachment;
    private readonly TranslateTransform _pageRise = new();
    private readonly System.Windows.Threading.DispatcherTimer _greatGrace;
    private StatusScreen? _shown;
    private StatusScreen? _deferred;
    private bool _landing;
    private bool _pointerTab;
    private AppTab _shownTab;
    private MotionMoment? _tabMoment;
    private bool _restartShown;
    private bool _bannerShown;

    /// <param name="viewModel">The main view model.</param>
    /// <param name="policy">The motion policy this window drives and follows (default: the process-wide one).</param>
    public MainWindow(MainViewModel viewModel, MotionPolicy? policy = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _policy = policy ?? MotionPolicy.Current;
        _greatGrace = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal) { Interval = GreatGrace };
        _greatGrace.Tick += (_, _) => EndGreatGrace();
        CampfireTheme.EnsureFor(this);
        InitializeComponent();
        MotionPolicy.SetPolicy(this, _policy);
        _policyAttachment = _policy.AttachWindow(this);
        _policy.AnimationsEnabled = viewModel.AnimationsEnabled;
        _policy.Changed += OnPolicyChanged;
        DataContext = _viewModel;

        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.Great.Stage = this;
        _viewModel.Great.Scheduler ??= new DispatcherDelayScheduler(Dispatcher);
        StatusPage.OpenSettingsRequested += (_, _) => _viewModel.SelectedTab = AppTab.Settings;
        PreviewKeyDown += OnPreviewKeyDown;
        StateChanged += (_, _) => UpdateWindowState();
        SourceInitialized += (_, _) =>
        {
            WindowPlacement.FitCenteredWindow(this); // CenterScreen does not clamp: keep the title bar on the screen
            UpdateWindowState();
        };
        DpiChanged += (_, _) => UpdateWindowState();
        SizeChanged += (_, _) => UpdateLayoutMetrics();
        Loaded += (_, _) => UpdateLayoutMetrics();

        // Capture mode pauses both global hotkeys: never leave them paused once the
        // window can't receive the key any more.
        Deactivated += (_, _) => _viewModel.CancelHotkeyCapture();
        Closed += (_, _) => OnClosed();

        foreach (FrameworkElement page in new FrameworkElement[] { StatusPage, PresetsPage, SettingsPage })
        {
            page.RenderTransform = _pageRise;
        }

        _shownTab = _viewModel.SelectedTab;
        ShowTab(_shownTab, animate: false);
        ApplyScreen(_viewModel.StatusScreen, animate: false);
        _restartShown = _viewModel.ShowRestartNotice;
        _bannerShown = !string.IsNullOrEmpty(_viewModel.BannerError);
        UpdateWindowState();
    }

    /// <summary>
    /// How long a scan's end that found a map waits for the Great: the controller changes state an instant before it
    /// reports the map, and that refresh can reach the screen first. Held this long, the skill check still spins
    /// when the landing comes; if no landing comes (none was possible), the change is presented anyway.
    /// </summary>
    public static readonly TimeSpan GreatGrace = TimeSpan.FromMilliseconds(250);

    /// <summary>The motion policy this window drives (animations setting) and follows (activation, visibility).</summary>
    public MotionPolicy Policy => _policy;

    /// <summary>The Status screen currently presented (it lags the view model while the skill check lands).</summary>
    public StatusScreen? PresentedScreen => _shown;

    /// <summary>A landing is in progress (the screen waits for the dial to lift).</summary>
    public bool IsLanding => _landing;

    /// <inheritdoc />
    bool IGreatStage.CanLand => GreatCoordinator.CanLand(
        _policy,
        statusShown: _viewModel.SelectedTab == AppTab.Status && IsVisible && WindowState != WindowState.Minimized,
        skillCheckScanning: !_landing && StatusPage.MapPlate.SkillCheck.State == SkillCheckState.Scanning);

    /// <inheritdoc />
    TimeSpan IGreatStage.Land(string realm, string? map)
    {
        SkillCheck skill = StatusPage.MapPlate.SkillCheck;

        // The landing first: if it fails, nothing is held (the grace, if any, still presents the change) and the
        // coordinator plays the sound at once. Its Hit and Finished come later (timers), never from inside this call.
        // It opens the state change's moment (scanning -> map found): the rest of the screen joins it after the lift.
        var found = new StateMoment("Scanning → map found");
        TimeSpan hit;
        using (found.Enter(TimeSpan.Zero))
        {
            hit = StatusPage.MapPlate.LandGreat(map, realm); // (a realm-only read says "Realm found", never "Map found")
        }

        _greatGrace.Stop(); // the held change is told once the dial has lifted
        _landing = true;
        TakeOver(found);
        TimeSpan reveal = hit + SkillCheck.FinishAfterHit;
        EventHandler? finished = null;
        finished = (_, _) =>
        {
            skill.Finished -= finished;
            _landing = false;
            StatusScreen next = _deferred ?? _viewModel.StatusScreen;
            _deferred = null;

            // the fog clears off the map as part of the Great's own moment (unless a newer change has taken over)
            ApplyScreen(next, animate: true, continuing: ReferenceEquals(_stateMoment, found) ? found : null, offset: reveal);
        };
        skill.Finished += finished;
        return hit;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.StatusScreen):
                StatusScreen next = _viewModel.StatusScreen;
                bool quick = _viewModel.IsUserDrivenChange; // (the override on or off: a quick swap, never a Great)
                if (_landing || _greatGrace.IsEnabled)
                {
                    _deferred = next; // told once the dial has lifted (or the grace is over)
                }
                else if (!quick && AwaitsTheGreat(next))
                {
                    _deferred = next;
                    _greatGrace.Start();
                }
                else
                {
                    ApplyScreen(next, animate: true, quick: quick);
                }

                break;
            case nameof(MainViewModel.SelectedTab):
                ShowTab(_viewModel.SelectedTab, animate: _pointerTab);
                _pointerTab = false;
                break;
            case nameof(MainViewModel.AnimationsEnabled):
                _policy.AnimationsEnabled = _viewModel.AnimationsEnabled; // (off: OnPolicyChanged stops everything at once)
                break;
            case nameof(MainViewModel.BackdropMap):
                UpdateBackdrop(TimeSpan.FromMilliseconds(220));
                break;
            case nameof(MainViewModel.ShowRestartNotice):
            {
                bool opened = _viewModel.ShowRestartNotice && !_restartShown;
                bool closed = !_viewModel.ShowRestartNotice && _restartShown;
                _restartShown = _viewModel.ShowRestartNotice;
                if (opened || closed)
                {
                    TellBanners(restartOpened: opened, hotkeyOpened: false);
                }

                break;
            }

            case nameof(MainViewModel.BannerError):
            {
                bool shown = !string.IsNullOrEmpty(_viewModel.BannerError);
                if (shown != _bannerShown)
                {
                    _bannerShown = shown;
                    TellBanners(restartOpened: false, hotkeyOpened: shown);
                }

                break;
            }
        }
    }

    /// <summary>A spinning scan on screen ends with a map (or, from a realm-only read, a realm) found, and the Great could land here.</summary>
    private bool AwaitsTheGreat(StatusScreen next) =>
        _shown is { State: ScreenState.Scanning } &&
        (next.DetectedRealm is not null || next.Plate.IsMap || next.State is ScreenState.WaitingFocus or ScreenState.Active) &&
        ((IGreatStage)this).CanLand;

    private void EndGreatGrace()
    {
        _greatGrace.Stop();
        if (_landing)
        {
            return;
        }

        StatusScreen next = _deferred ?? _viewModel.StatusScreen;
        _deferred = null;
        ApplyScreen(next, animate: true);
    }

    /// <summary>The map behind the fog: the presented Status screen's, the Presets hero, or none (Settings).</summary>
    private void UpdateBackdrop(TimeSpan crossfade)
    {
        string? map = _shownTab switch
        {
            AppTab.Status => _shown?.BackdropMap,
            AppTab.Presets => _viewModel.PresetsBackdropMap,
            _ => null,
        };
        Backdrop.ShowMap(map, crossfade);
    }

    /// <summary>The ambient budget: the legs sway only while idle and no other loop (the live campfire) owns the screen.</summary>
    private void UpdateSway()
    {
        bool fireOnScreen = _shownTab == AppTab.Status && _shown is { Plate.IsMap: false } screen && screen.Plate.Fire == FireState.Burning;
        Legs.IsSwayAllowed = _shown is not { Leaning: true } && !fireOnScreen;
    }

    /// <summary>
    /// Shows a section: a short crossfade with a 6 px rise for pointer switches; keyboard switches are instant. A page
    /// that is not shown follows a still policy (<see cref="_offstage"/>): its ambient loops stop and its changes are
    /// instant, like the prototype's hidden panels. When the keyboard focus was on the page that goes away (a banner's
    /// "Show in Presets" or "Open Settings" switched the section), it follows to the page shown
    /// (<see cref="FocusPageSoon"/>) instead of dropping on the window.
    /// </summary>
    private void ShowTab(AppTab tab, bool animate)
    {
        FrameworkElement leaving = PageOf(_shownTab);
        _shownTab = tab;
        FrameworkElement shown = PageOf(tab);
        bool carryFocus = !ReferenceEquals(leaving, shown) && CampfireAssist.HoldsFocusWithin(leaving);
        _tabMoment?.Stop();
        _tabMoment = null;
        foreach (FrameworkElement page in new FrameworkElement[] { StatusPage, PresetsPage, SettingsPage })
        {
            bool on = ReferenceEquals(page, shown);
            if (on)
            {
                page.ClearValue(MotionPolicy.PolicyProperty); // the window's policy again
            }

            page.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on)
            {
                MotionPolicy.SetPolicy(page, _offstage);
            }
        }

        if (tab == AppTab.Presets && IsLoaded)
        {
            // its first frame already shows the selected realm in the middle of the list with its bar (they are not
            // left to a later dispatcher pass that draws one frame without them)
            PresetsPage.PrepareToShow();
        }

        double frontWas = FogFront.Opacity;
        FogFront.Opacity = tab == AppTab.Status ? 1 : 0;
        if (tab == AppTab.Status)
        {
            FogFront.ClearValue(MotionPolicy.PolicyProperty);
        }
        else
        {
            MotionPolicy.SetPolicy(FogFront, _offstage);
        }

        MotionMoment.StopDisallowed(); // whatever was still playing on the page that went away lands at once

        if (animate && IsLoaded && _policy.ShouldAnimate)
        {
            var moment = new MotionMoment(this, "MainWindow.tab");
            moment.Add(shown, OpacityProperty, Anim.FromTo(0, 1, 180));
            if (!_policy.ReducedMotion)
            {
                moment.Add(_pageRise, TranslateTransform.YProperty, Anim.FromTo(6, 0, 180));
            }

            if (Math.Abs(frontWas - FogFront.Opacity) > 0.01)
            {
                moment.Add(FogFront, OpacityProperty, Anim.FromTo(frontWas, FogFront.Opacity, 180));
            }

            _tabMoment = moment;
            moment.Begin();
        }

        UpdateBackdrop(animate ? TimeSpan.FromMilliseconds(220) : TimeSpan.Zero);
        UpdateSway();
        if (carryFocus)
        {
            FocusPageSoon(tab, leaving);
        }
    }

    private FrameworkElement PageOf(AppTab tab) => tab switch
    {
        AppTab.Presets => PresetsPage,
        AppTab.Settings => SettingsPage,
        _ => StatusPage,
    };

    /// <summary>
    /// The page that held the keyboard focus was hidden by its own action: once layout has caught up (before WPF
    /// would drop the focus on the window), the focus goes to the shown page's entry: the selected row on Presets (the
    /// realm a banner was showing), the first control on Settings, the override on Status. Nothing moves when the
    /// section changed again meanwhile or the focus has already gone somewhere that is still shown. (In a window that is
    /// not active its focus scope's focus is followed: the window gets it back when activated.)
    /// </summary>
    private void FocusPageSoon(AppTab tab, FrameworkElement leaving) =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            IInputElement? focus = Keyboard.FocusedElement ?? FocusManager.GetFocusedElement(this);
            bool lost = focus is null || ReferenceEquals(focus, this) || focus is UIElement { IsVisible: false } || CampfireAssist.HoldsFocusWithin(leaving);
            if (_shownTab != tab || !lost)
            {
                return;
            }

            switch (tab)
            {
                case AppTab.Presets:
                    PresetsPage.FocusSelection();
                    break;
                case AppTab.Settings:
                    SettingsPage.FocusEntry();
                    break;
                default:
                    StatusPage.FocusEntry();
                    break;
            }
        }));

    private void OnNavPointerDown(object sender, MouseButtonEventArgs e) => _pointerTab = true;

    /// <summary>A press on the rail that switched no section must not make a later switch (e.g. from the keyboard) animate.</summary>
    private void OnNavPointerUp(object sender, MouseButtonEventArgs e) =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => _pointerTab = false));

    /// <summary>The rail's sections move with the arrow keys (Up / Down, Home / End), without motion.</summary>
    private void OnNavKeyDown(object sender, KeyEventArgs e)
    {
        var tabs = new[] { (StatusTab, AppTab.Status), (PresetsTab, AppTab.Presets), (SettingsTab, AppTab.Settings) };
        int current = Array.FindIndex(tabs, t => t.Item2 == _viewModel.SelectedTab);
        int next = e.Key switch
        {
            Key.Down => (current + 1) % tabs.Length,
            Key.Up => (current + tabs.Length - 1) % tabs.Length,
            Key.Home => 0,
            Key.End => tabs.Length - 1,
            _ => -1,
        };
        if (next < 0)
        {
            return;
        }

        e.Handled = true;
        _pointerTab = false;
        _viewModel.SelectedTab = tabs[next].Item2;
        tabs[next].Item1.Focus();
    }

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

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    /// <summary>
    /// Maximized, a chromeless window overhangs the screen by its resize frame and padded border: pad it back in (so
    /// the caption, the rail's marker, the Close button and the bottom edge are on screen); swap the glyph.
    /// </summary>
    private void UpdateWindowState()
    {
        bool maximized = WindowState == WindowState.Maximized;
        Root.Margin = maximized ? WindowPlacement.MaximizedInset(this) : new Thickness(0);
        Controls.CampfireAssist.SetIcon(MaximizeButton, (Geometry)FindResource(maximized ? "CfGlyphRestore" : "CfGlyphMaximize"));
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    /// <summary>The rail is clamp(168px, 15.3vw, 220px); the legs clamp(470px, 50vw, 800px); short windows tighten the rail.</summary>
    private void UpdateLayoutMetrics()
    {
        Size viewport = CampfireType.GetViewport(this);
        double vw = viewport.Width, vh = viewport.Height;
        if (vw <= 0 || vh <= 0)
        {
            return;
        }

        RailColumn.Width = new GridLength(Math.Round(CampfireType.Clamp(168, 15.3, 220, vw)));
        Legs.Width = CampfireType.Clamp(470, 50, 800, vw);
        bool shortWindow = vh <= CampfireType.ShortViewportHeight;
        RailPanel.Margin = new Thickness(0, shortWindow ? 18 : 28, 0, 18);
        RailFoot.Padding = new Thickness(vw < 1180 ? 22 : 26, 16, 16, 0);
        double gap = shortWindow ? 10 : 14;
        foreach (FrameworkElement item in new FrameworkElement[] { RailScanKey, RailGlobalKey, RailLastSwitch })
        {
            item.Margin = new Thickness(0, gap, 0, 0);
        }
    }

    private void OnClosed()
    {
        _greatGrace.Stop();
        _viewModel.CancelHotkeyCapture();
        SettingsPage.CommitVolume();
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _policy.Changed -= OnPolicyChanged;
        _stateMoment?.Interrupt(TimeSpan.Zero);
        if (ReferenceEquals(_viewModel.Great.Stage, this))
        {
            _viewModel.Great.Stage = null;
        }

        _policyAttachment.Dispose();
    }
}

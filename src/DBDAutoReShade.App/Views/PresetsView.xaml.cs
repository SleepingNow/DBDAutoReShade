using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.Services;
using DBDAutoReShade.App.ViewModels;

namespace DBDAutoReShade.App.Views;

/// <summary>
/// The Presets screen. Besides its bindings it tells the prototype's small moments: the selection bar glides between
/// rows (220 ms ease-in-out; keyboard moves snap), the hero picture swaps with a blur-bridged crossfade (≤ 220 ms) when
/// a row or a thumbnail is clicked, rows that come back when the filter changes fade up (30 ms apart), and the Test
/// button's countdown ring follows the controller's 15 s focus wait. A state change seen from here is told smaller on
/// the hero picture (<see cref="TellStateChange"/>). Every moment asks the <see cref="MotionPolicy"/>.
/// The inline clear confirmation keeps keyboard focus: it opens on "Clear preset", and when it closes the focus goes
/// back to Clear (or to the file button once there is nothing left to clear).
/// </summary>
public partial class PresetsView : UserControl
{
    private readonly ScaleTransform _barScale = new(1, 1);
    private readonly TranslateTransform _barMove = new();
    private readonly ScaleTransform _pinnedScale = new(1, 1);
    private readonly TranslateTransform _pinnedMove = new();
    private readonly List<RealmRow> _watchedRows = new();
    private readonly List<RealmRow> _cameBack = new();
    private readonly ScaleTransform _heroScale = new(1, 1);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ListBoxItem, TranslateTransform> _rowRises = new();
    private readonly TranslateTransform _captionRise = new();
    private MainViewModel? _viewModel;
    private Rectangle? _bar;
    private MotionMoment? _barMoment;
    private MotionMoment? _heroMoment;
    private MotionMoment? _rowsMoment;
    private bool _pointerSelect;
    private bool _pointerHero;
    private string? _shownHero;
    private bool _shownGlobal;
    private (string? Map, bool Global)? _heroBefore;

    /// <summary>Creates the view (the window sets its data context to the main view model).</summary>
    public PresetsView()
    {
        CampfireTheme.EnsureFor(this);
        InitializeComponent();
        PinnedIndicator.RenderTransform = new TransformGroup { Children = { _pinnedScale, _pinnedMove } };
        HeroPicture.RenderTransformOrigin = new Point(0.5, 0.5);
        HeroPicture.RenderTransform = _heroScale;
        HeroCaption.RenderTransform = _captionRise;
        DataContextChanged += (_, e) => Attach(e.NewValue as MainViewModel);
        SizeChanged += (_, _) => UpdateLayoutMetrics();
        Loaded += (_, _) =>
        {
            UpdateLayoutMetrics();
            RevealSelection();
            PlaceIndicators(animate: false);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    RevealSelection();
                    PlaceIndicators(animate: false);
                }));
            }
        };
        GlobalRowButton.PreviewMouseLeftButtonDown += (_, _) => _pointerSelect = _pointerHero = true;

        // a press that changed nothing must not make a later change (from the keyboard or a refresh) glide or swap
        PreviewMouseLeftButtonUp += (_, _) => ForgetPointerSoon();
        RealmList.LayoutUpdated += (_, _) => KeepIndicatorInPlace();
    }

    /// <summary>
    /// The page is about to be shown (the window switches to Presets): it is laid out now, the selected realm is
    /// scrolled into the middle of the list and its bar is placed, so the first frame drawn is already right (a
    /// selection made while the page was hidden, e.g. by a detection, does not jump into place a frame later).
    /// </summary>
    public void PrepareToShow()
    {
        if (!IsLoaded)
        {
            return;
        }

        UpdateLayout();
        RevealSelection();
        PlaceIndicators(animate: false);
    }

    private void Attach(MainViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = viewModel;
        if (viewModel is null)
        {
            return;
        }

        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.RealmRows.CollectionChanged += (_, _) => WatchRows();
        WatchRows();
        _shownHero = viewModel.HeroMap;
        _shownGlobal = viewModel.DetailIsGlobal;
        ApplyFeedback(viewModel.Feedback);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SelectedRow):
                if (!_pointerSelect)
                {
                    RevealSelection();
                }

                PlaceIndicators(animate: _pointerSelect);
                break;
            case nameof(MainViewModel.HeroMap):
            case nameof(MainViewModel.DetailIsGlobal):
                SwapHero();
                break;
            case nameof(MainViewModel.Feedback):
                ApplyFeedback(_viewModel!.Feedback);
                break;
        }
    }

    // ---------- the selection bars ----------

    private Rectangle? Bar
    {
        get
        {
            if (_bar is null)
            {
                RealmList.ApplyTemplate();
                _bar = RealmList.Template.FindName("Indicator", RealmList) as Rectangle;
                if (_bar is not null)
                {
                    _bar.RenderTransform = new TransformGroup { Children = { _barScale, _barMove } };
                }
            }

            return _bar;
        }
    }

    /// <summary>One bar per list sits on the selected row: it glides there (pointer) or snaps (keyboard, layout).</summary>
    private void PlaceIndicators(bool animate)
    {
        if (_viewModel is null || !IsLoaded)
        {
            return;
        }

        // where the bars are drawn now (a glide may be mid-way), read before its clocks go: a new target is glided to
        // from there, never from the old target
        double fromTop = _barMove.Y, fromScale = _barScale.ScaleY;
        double barWas = Bar?.Opacity ?? 0, pinnedWas = PinnedIndicator.Opacity;
        _barMoment?.Stop();
        _barMoment = null;
        MotionPolicy policy = MotionPolicy.For(this);
        bool glide = animate && policy.ShouldAnimate && !policy.ReducedMotion;
        MotionMoment? moment = glide || (animate && policy.ShouldAnimate) ? new MotionMoment(this, "PresetsView.selection") : null;

        // the realm list
        if (Bar is { } bar)
        {
            RealmRow? row = _viewModel.SelectedRealmRow;
            var container = row is null ? null : RealmList.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem;
            bool show = container is { IsVisible: true } && bar.Parent is Canvas;
            if (show)
            {
                Point at = container!.TranslatePoint(new Point(0, 0), (Canvas)bar.Parent);
                double top = at.Y + 8, scale = Math.Max(8, container.ActualHeight - 16) / 40;
                bool wasOn = barWas > 0.5;
                _barMove.Y = top;
                _barScale.ScaleY = scale;
                if (glide && wasOn && moment is not null)
                {
                    moment.Add(_barMove, TranslateTransform.YProperty, Anim.FromTo(fromTop, top, 220, Ease.InOut));
                    moment.Add(_barScale, ScaleTransform.ScaleYProperty, Anim.FromTo(fromScale, scale, 220, Ease.InOut));
                }
            }

            FadeBar(bar, barWas, show ? 1 : 0, moment);
        }

        // the pinned Global / Menu row
        bool global = _viewModel.SelectedRow is GlobalPresetRow;
        if (global)
        {
            double h = GlobalRowButton.ActualHeight;
            _pinnedMove.Y = 8;
            _pinnedScale.ScaleY = Math.Max(8, h - 16) / 40;
        }

        FadeBar(PinnedIndicator, pinnedWas, global ? 1 : 0, moment);
        moment?.Begin();
        _barMoment = moment;
    }

    /// <summary>A bar shows or hides (150 ms) from the opacity it is drawn at (<paramref name="from"/>).</summary>
    private static void FadeBar(Rectangle bar, double from, double to, MotionMoment? moment)
    {
        bar.Opacity = to;
        if (moment is not null && Math.Abs(from - to) > 0.01)
        {
            moment.Add(bar, OpacityProperty, Anim.FromTo(from, to, 150));
        }
    }

    // after the list re-lays out (filtering, a resize) the bar follows its row without motion
    private void KeepIndicatorInPlace()
    {
        if (_viewModel?.SelectedRealmRow is not { } row || Bar is not { } bar || bar.Parent is not Canvas canvas || _barMoment is { IsActive: true })
        {
            return;
        }

        if (RealmList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem { IsVisible: true } container)
        {
            return;
        }

        double top = container.TranslatePoint(new Point(0, 0), canvas).Y + 8;
        double scale = Math.Max(8, container.ActualHeight - 16) / 40;
        if (Math.Abs(top - _barMove.Y) > 0.5 || Math.Abs(scale - _barScale.ScaleY) > 0.01)
        {
            _barMove.Y = top;
            _barScale.ScaleY = scale;
        }
    }

    /// <summary>Scrolls the realm list so the selected row sits in the middle (the prototype's revealSel).</summary>
    private void RevealSelection()
    {
        if (_viewModel?.SelectedRealmRow is not { } row || !IsLoaded)
        {
            return;
        }

        RealmList.ApplyTemplate();
        if (RealmList.Template.FindName("RowsScroller", RealmList) is not ScrollViewer scroller ||
            RealmList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem { IsVisible: true } item ||
            scroller.Content is not UIElement content)
        {
            return;
        }

        RealmList.UpdateLayout();
        double top = item.TranslatePoint(new Point(0, 0), content).Y;
        double offset = top - scroller.ViewportHeight / 2 + item.ActualHeight / 2;
        scroller.ScrollToVerticalOffset(Math.Clamp(offset, 0, Math.Max(0, scroller.ExtentHeight - scroller.ViewportHeight)));
        scroller.UpdateLayout();
    }

    // A row is selected while the press is handled: the flags last exactly that long. (The Global / Menu row and the
    // thumbnails act on the click, at the release: the release clears theirs.)
    private void OnRealmListPointerDown(object sender, MouseButtonEventArgs e)
    {
        _pointerSelect = _pointerHero = true;
        ForgetPointerSoon();
    }

    private void OnThumbClick(object sender, RoutedEventArgs e)
    {
        _pointerHero = InputManager.Current.MostRecentInputDevice is MouseDevice;
        ForgetPointerSoon();
    }

    private void ForgetPointerSoon() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => _pointerSelect = _pointerHero = false));

    /// <summary>Up / Down move between the pinned row and the realm list (the list moves within itself).</summary>
    private void OnGlobalRowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || _viewModel is null)
        {
            return;
        }

        RealmRow? first = _viewModel.RealmRows.FirstOrDefault(r => r.IsVisible);
        if (first is null)
        {
            return;
        }

        e.Handled = true;
        _viewModel.SelectRow(first);
        FocusRow(first);
    }

    private void OnRealmListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Up || _viewModel?.SelectedRealmRow is not { } row)
        {
            return;
        }

        RealmRow? firstVisible = _viewModel.RealmRows.FirstOrDefault(r => r.IsVisible);
        if (!ReferenceEquals(firstVisible, row))
        {
            return;
        }

        e.Handled = true;
        _viewModel.SelectRow(_viewModel.GlobalRow);
        GlobalRowButton.Focus();
    }

    /// <summary>
    /// Puts the keyboard focus on the selected row: the realm (e.g. the one a banner's "Show in Presets" selected), or
    /// the pinned Global / Menu row; the filter box when the selected realm is filtered out or nothing is selected.
    /// </summary>
    public void FocusSelection()
    {
        switch (_viewModel?.SelectedRow)
        {
            case RealmRow { IsVisible: true } row:
                FocusRow(row);
                break;
            case GlobalPresetRow:
                CampfireAssist.MoveFocusTo(GlobalRowButton);
                break;
            default:
                CampfireAssist.MoveFocusTo(Filter);
                break;
        }
    }

    private void FocusRow(RealmRow row)
    {
        RealmList.ScrollIntoView(row);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (RealmList.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item)
            {
                CampfireAssist.MoveFocusTo(item);
            }
        }));
    }

    // ---------- the filter: rows that come back fade up ----------

    private void WatchRows()
    {
        foreach (RealmRow row in _watchedRows)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        _watchedRows.Clear();
        if (_viewModel is null)
        {
            return;
        }

        foreach (RealmRow row in _viewModel.RealmRows)
        {
            row.PropertyChanged += OnRowChanged;
            _watchedRows.Add(row);
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RealmRow.IsVisible) || sender is not RealmRow { IsVisible: true } row || !IsLoaded || !IsVisible)
        {
            return;
        }

        if (_cameBack.Count == 0)
        {
            // (above Render: the rows' fade-up is applied before the frame that shows them, so they never pop in fully
            // drawn for a frame and then vanish into the fade)
            Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(FadeUpRows));
        }

        _cameBack.Add(row);
    }

    private void FadeUpRows()
    {
        List<RealmRow> rows = _cameBack.ToList();
        _cameBack.Clear();
        MotionPolicy policy = MotionPolicy.For(this);
        if (!policy.ShouldAnimate || rows.Count == 0)
        {
            return;
        }

        _rowsMoment?.Stop();
        RealmList.UpdateLayout();
        RealmList.ApplyTemplate();
        var scroller = RealmList.Template.FindName("RowsScroller", RealmList) as ScrollViewer;
        var moment = new MotionMoment(this, "PresetsView.filter");
        int k = 0;
        List<RealmRow> order = _viewModel?.RealmRows.ToList() ?? new List<RealmRow>();
        foreach (RealmRow row in rows.OrderBy(order.IndexOf))
        {
            if (RealmList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem item || !InView(item, scroller))
            {
                continue; // (a row out of sight comes back without a moment: the cascade starts at the top of the view)
            }

            double delay = Math.Min(k++, 6) * 30;
            moment.Add(item, OpacityProperty, Anim.FromTo(0, 1, 200, Ease.Out, delay));
            if (!policy.ReducedMotion)
            {
                moment.Add(RiseOf(item), TranslateTransform.YProperty, Anim.FromTo(4, 0, 200, Ease.Out, delay));
            }
        }

        _rowsMoment = moment;
        moment.Begin();
    }

    /// <summary>The row is (at least partly) inside the list's viewport.</summary>
    private static bool InView(ListBoxItem item, ScrollViewer? scroller)
    {
        if (scroller is null || !item.IsVisible)
        {
            return item.IsVisible;
        }

        double top = item.TranslatePoint(new Point(0, 0), scroller).Y;
        return top + item.ActualHeight > 0 && top < scroller.ViewportHeight;
    }

    /// <summary>The row's own rise, composed with whatever transform it already has (the press feedback's scale keeps
    /// working).</summary>
    private TranslateTransform RiseOf(ListBoxItem item)
    {
        if (_rowRises.TryGetValue(item, out TranslateTransform? rise))
        {
            return rise;
        }

        rise = new TranslateTransform();
        Transform existing = item.RenderTransform;
        if (existing is null || existing == Transform.Identity)
        {
            item.RenderTransform = rise;
        }
        else
        {
            item.RenderTransform = new TransformGroup { Children = { existing, rise } };
        }

        _rowRises.Add(item, rise);
        return rise;
    }

    // ---------- the hero picture ----------

    /// <summary>
    /// A state change seen from Presets: the same story, told smaller on the selected realm's picture (the prototype's
    /// presetsStateMotion). Waiting for focus → active on the same picture: the ember runs round the hero frame and the
    /// frame is lit where the flames meet. A detection that brought another map in (the refresh that told this change
    /// selected it): the map comes out of the fog when it waits for focus, otherwise the pictures swap. The window calls
    /// it inside its state moment, only while this page is shown.
    /// </summary>
    public void TellStateChange(ScreenState from, ScreenState to)
    {
        (string? Map, bool Global)? before = _heroBefore;
        _heroBefore = null;
        if (_viewModel is null || !IsLoaded || !IsVisible)
        {
            return;
        }

        MotionPolicy policy = MotionPolicy.For(this);
        if (!policy.ShouldAnimate)
        {
            return;
        }

        if (before is not { } old)
        {
            if (from == ScreenState.WaitingFocus && to == ScreenState.Active && _viewModel.DetailIsLive)
            {
                ConfirmHero(policy);
            }

            return;
        }

        if (to == ScreenState.WaitingFocus && !_viewModel.DetailIsGlobal && HeroPicture.HasPicture)
        {
            RevealHero(old.Map, old.Global, policy);
        }
        else
        {
            PlayHeroSwap(old.Map, old.Global, policy);
        }
    }

    /// <summary>
    /// The hero changed: a click swaps it with a blur-bridged crossfade at once; a change made by a refresh (a new
    /// detection selects its realm and map) is remembered for the state change told in the same refresh.
    /// </summary>
    private void SwapHero()
    {
        if (_viewModel is null)
        {
            return;
        }

        string? oldMap = _shownHero;
        bool oldGlobal = _shownGlobal;
        _shownHero = _viewModel.HeroMap;
        _shownGlobal = _viewModel.DetailIsGlobal;
        if (oldMap == _shownHero && oldGlobal == _shownGlobal)
        {
            return; // (a selection raises this before the hero has changed: the press still counts for the change)
        }

        bool pointer = _pointerHero;
        _pointerHero = false;

        if (!pointer)
        {
            if (_heroBefore is null)
            {
                _heroBefore = (oldMap, oldGlobal);
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _heroBefore = null));
            }

            return;
        }

        if (!IsLoaded || !IsVisible || !MotionPolicy.For(this).ShouldAnimate)
        {
            return;
        }

        PlayHeroSwap(oldMap, oldGlobal, MotionPolicy.For(this));
    }

    /// <summary>The blur-bridged crossfade: the old picture blurs out (160 ms), the new one comes in out of a 4 px blur
    /// (200 ms), the caption fades in (160 ms after 40 ms).</summary>
    private void PlayHeroSwap(string? oldMap, bool oldGlobal, MotionPolicy policy)
    {
        MotionMoment moment = NewHeroMoment("PresetsView.heroSwap");
        AddHeroGhost(moment, oldMap, oldGlobal, 160, 10, policy.ReducedMotion);
        FrameworkElement art = _viewModel!.DetailIsGlobal ? HeroCampfire : HeroPicture;
        moment.Add(art, OpacityProperty, Anim.FromTo(0, 1, 200));
        if (!policy.ReducedMotion)
        {
            var blurIn = new BlurEffect { Radius = 0 };
            art.Effect = blurIn;
            moment.Add(blurIn, BlurEffect.RadiusProperty, Anim.FromTo(10, 0, 200));
        }

        moment.Add(HeroCaption, OpacityProperty, Anim.FromTo(0, 1, 160, Ease.Out, 40));
        moment.Begin();
    }

    /// <summary>
    /// The detected map comes out of the fog: the old picture blurs out (240 ms), the new one emerges from a 10 px blur
    /// at 104 % (600 ms after 100 ms), the caption rises 8 px into place (320 ms after 480 ms).
    /// </summary>
    private void RevealHero(string? oldMap, bool oldGlobal, MotionPolicy policy)
    {
        MotionMoment moment = NewHeroMoment("PresetsView.heroReveal");
        bool reduced = policy.ReducedMotion;
        AddHeroGhost(moment, oldMap, oldGlobal, 240, 15, reduced);
        moment.Add(HeroPicture, OpacityProperty, Anim.FromTo(0, 1, reduced ? 200 : 600, Ease.Out, reduced ? 0 : 100));
        moment.Add(HeroCaption, OpacityProperty, Anim.FromTo(0, 1, reduced ? 200 : 320, Ease.Out, reduced ? 160 : 480));
        if (!reduced)
        {
            var blurIn = new BlurEffect { Radius = 0 };
            HeroPicture.Effect = blurIn;
            moment.Add(blurIn, BlurEffect.RadiusProperty, Anim.FromTo(25, 0, 600, Ease.Out, 100));
            moment.Add(_heroScale, ScaleTransform.ScaleXProperty, Anim.FromTo(1.04, 1, 600, Ease.Out, 100));
            moment.Add(_heroScale, ScaleTransform.ScaleYProperty, Anim.FromTo(1.04, 1, 600, Ease.Out, 100));
            moment.Add(_captionRise, TranslateTransform.YProperty, Anim.FromTo(8, 0, 320, Ease.Out, 480));
        }

        moment.Begin();
    }

    /// <summary>
    /// The preset was confirmed with this realm's picture in view: the ember runs round the hero frame (760 ms) and
    /// where the two flames meet the frame is lit (the live border and glow come up from 720 ms, 260 ms).
    /// </summary>
    private void ConfirmHero(MotionPolicy policy)
    {
        MotionMoment moment = NewHeroMoment("PresetsView.confirm");
        if (!policy.ReducedMotion)
        {
            HeroSweep.Children.Clear();
            EmberSweep.Add(moment, HeroSweep, HeroCard.ActualWidth, HeroCard.ActualHeight, 760);
        }

        moment.Add(HeroBorder, OpacityProperty, Anim.Keys(0, new[] { new Kf(0, 0.35), new Kf(720, 0.35), new Kf(980, 1, Ease.Out) }));
        moment.Add(HeroGlow, OpacityProperty, Anim.Keys(0, new[] { new Kf(0, 0), new Kf(720, 0), new Kf(980, HeroGlow.Opacity, Ease.Out) }));
        moment.Begin();
    }

    /// <summary>A moment on the hero picture: it replaces the one playing; its ghosts and effects go when it ends.</summary>
    private MotionMoment NewHeroMoment(string name)
    {
        _heroMoment?.Stop();
        RemoveGhosts();
        var moment = new MotionMoment(this, name);
        moment.Ended += (_, _) =>
        {
            if (ReferenceEquals(_heroMoment, moment))
            {
                _heroMoment = null;
                RemoveGhosts();
                HeroPicture.Effect = null;
                HeroCampfire.Effect = null;
            }
        };
        _heroMoment = moment;
        return moment;
    }

    /// <summary>The leaving picture, as a ghost on top: it fades out over <paramref name="ms"/>, blurring to <paramref name="blur"/>.</summary>
    private void AddHeroGhost(MotionMoment moment, string? oldMap, bool oldGlobal, double ms, double blur, bool reduced)
    {
        FrameworkElement? ghost = null;
        if (!oldGlobal && MapImageCatalog.Default.TryGetImage(oldMap) is { } oldImage)
        {
            var image = new Image { Source = oldImage, Stretch = Stretch.UniformToFill, Margin = new Thickness(1), IsHitTestVisible = false, Tag = "ghost" };
            MapImageCatalog.ApplyQuality(image);
            ghost = image;
        }
        else if (oldGlobal)
        {
            ghost = new CampfireScene { FireState = HeroCampfire.FireState, FireX = 292, IsLive = false, AnimateChanges = false, Margin = new Thickness(1), IsHitTestVisible = false, Tag = "ghost" };
        }

        if (ghost is null)
        {
            return;
        }

        HeroArt.Children.Insert(HeroArt.Children.IndexOf(HeroCaption), ghost);
        ghost.Opacity = 0;
        moment.Add(ghost, OpacityProperty, Anim.FromTo(1, 0, ms));
        if (!reduced)
        {
            var effect = new BlurEffect { Radius = 0 };
            ghost.Effect = effect;
            moment.Add(effect, BlurEffect.RadiusProperty, Anim.FromTo(0, blur, ms));
        }
    }

    private void RemoveGhosts()
    {
        foreach (UIElement ghost in HeroArt.Children.OfType<FrameworkElement>().Where(c => Equals(c.Tag, "ghost")).ToList())
        {
            HeroArt.Children.Remove(ghost);
        }
    }

    // ---------- the Test countdown ----------

    private void ApplyFeedback(PresetFeedback feedback)
    {
        // read before the panel collapses (focus moves off a hidden element later, asynchronously)
        bool confirmHadFocus = ClearConfirmPanel.IsKeyboardFocusWithin;
        if (feedback.Kind == FeedbackKind.Test)
        {
            TestRing.Start(feedback.Elapsed);
        }
        else if (TestRing.IsRunning)
        {
            TestRing.Cancel();
        }

        if (feedback.Kind == FeedbackKind.ClearConfirm)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ConfirmClearButton.Focus()));
        }
        else if (confirmHadFocus)
        {
            // "Clear preset" or "Keep it" closed the question: back to Clear, or to the file button once cleared
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!(ClearButton.IsEnabled && ClearButton.IsVisible && ClearButton.Focus()))
                {
                    ChangeFileButton.Focus();
                }
            }));
        }
    }

    // ---------- layout ----------

    /// <summary>
    /// A move to a monitor with another scale keeps the layout's DIP sizes, but the hero's cap is in physical pixels:
    /// measure the frame again.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateLayoutMetrics();
        HeroFrame.InvalidateMeasure();
    }

    /// <summary>The list column is clamp(300px, 28vw, 380px); the detail pads less under 1180 px; the hero frame (only
    /// the frame: the preset panel keeps the column's width) is never wider than 1.8 × the narrowest picture at the
    /// current DPI (the thumbnails cap themselves at the picture's own width: they never enlarge).</summary>
    private void UpdateLayoutMetrics()
    {
        Size viewport = CampfireType.GetViewport(this);
        if (viewport.Width <= 0)
        {
            return;
        }

        ListColumn.Width = new GridLength(Math.Round(CampfireType.Clamp(300, 28, 380, viewport.Width)));
        Detail.Margin = viewport.Width < 1180 ? new Thickness(26, 22, 28, 28) : new Thickness(36, 24, 40, 30);
        HeroFrame.MaxWidthPixels = MapImageCatalog.Default.ReferencePixelWidth * MapImageCatalog.MaxUpscale;
    }
}

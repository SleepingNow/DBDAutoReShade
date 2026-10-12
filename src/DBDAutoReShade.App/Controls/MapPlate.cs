using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.App.Controls;

/// <summary>
/// What a <see cref="MapPlate"/> shows: a map (its picture, the realm and map names) or the lobby (the campfire
/// clearing with a title and a line).
/// </summary>
/// <param name="RealmName">The realm (map scenes).</param>
/// <param name="MapName">The map; null or empty means the lobby scene.</param>
/// <param name="Title">Caption line 1 (the realm, or the lobby title such as "No map detected yet").</param>
/// <param name="Subtitle">Caption line 2 (the map, or the lobby line).</param>
/// <param name="Fire">The campfire's state in a lobby scene.</param>
public sealed record PlateScene(string? RealmName, string? MapName, string Title, string Subtitle, FireState Fire = FireState.Burning)
{
    /// <summary>A map scene.</summary>
    public bool IsMap => !string.IsNullOrWhiteSpace(MapName);

    /// <summary>A map scene captioned with the realm and the map.</summary>
    public static PlateScene Map(string realm, string map) => new(realm, map, realm, map);

    /// <summary>A lobby scene (the campfire clearing).</summary>
    public static PlateScene Lobby(FireState fire, string title, string line) => new(null, null, title, line, fire);
}

/// <summary>The border and glow of a <see cref="MapPlate"/>'s frame.</summary>
public enum PlateTone
{
    /// <summary>A hairline (bone 28 %).</summary>
    Neutral,

    /// <summary>Waiting for game focus: a half-lit ember border with a faint glow.</summary>
    Waiting,

    /// <summary>Live / confirmed: an ember border with a 1 px ring and a warm glow.</summary>
    Live,

    /// <summary>Manual override: an Entity-red border with a ring and a red glow.</summary>
    Override,
}

/// <summary>How a <see cref="MapPlate"/> changes from one scene to the next.</summary>
public enum PlateTransition
{
    /// <summary>Chosen from the two scenes (the prototype's plateKind).</summary>
    Auto,

    /// <summary>At once.</summary>
    None,

    /// <summary>Blur-bridged crossfade (user-driven swaps: override, another map).</summary>
    Swap,

    /// <summary>The fog clears off the map: the picture emerges from blur 10 → 0 as the fog veil thins; the names land after.</summary>
    Reveal,

    /// <summary>The picture sinks into the fog, then the next scene returns from behind it.</summary>
    Sink,

    /// <summary>Lobby to lobby: the campfire catches, dies out, burns down or flares back up in place.</summary>
    Fire,
}

/// <summary>
/// The Status map plate: the realm's map picture (UniformToFill, HighQuality, never enlarged beyond 1.8 × at the
/// current DPI: the frame's width and height are capped and the fog fills the rest) with the realm / map caption, or the campfire clearing
/// when no map is known; the skill check (<see cref="SkillCheck"/>) and its score event sit on top.
/// Set <see cref="Scene"/> and the plate tells the change (reveal, sink, swap or the fire's own moment); a new caption on
/// the same art (the same map, or the clearing with the same fire) only re-lands the caption and lets whatever the plate
/// is playing carry on (the prototype keeps the figure and patches its caption). A scene change in the middle of
/// another carries on from what is drawn: the scenes and captions that leave start from the look they had (a picture
/// still coming out of the fog sinks from there, an older leaving scene keeps fading out), and what stays eases back to
/// rest. Call <see cref="Confirm"/> when ReShade confirms the preset (two ember strokes race round the frame, the light
/// swells); <see cref="IsWaitingForFocus"/> runs the heartbeat on the frame's glow (ambient). The frame's glow effect is
/// attached only while it shows.
/// </summary>
public sealed class MapPlate : UserControl
{
    /// <summary>The scene shown. Changing it plays the matching moment (see <see cref="AutoTransition"/>).</summary>
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
        nameof(Scene), typeof(PlateScene), typeof(MapPlate),
        new PropertyMetadata(null, (d, e) => ((MapPlate)d).OnSceneChanged((PlateScene?)e.OldValue, (PlateScene?)e.NewValue)));

    /// <summary>Extra content under the caption (a tag such as "Waiting for game focus", or the red Override pill).</summary>
    public static readonly DependencyProperty CaptionTagProperty = DependencyProperty.Register(
        nameof(CaptionTag), typeof(object), typeof(MapPlate), new PropertyMetadata(null, (d, _) => ((MapPlate)d).UpdateTag()));

    /// <summary>The frame's border and glow.</summary>
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(PlateTone), typeof(MapPlate), new PropertyMetadata(PlateTone.Neutral, (d, _) => ((MapPlate)d).ApplyTone(animate: ((MapPlate)d).IsLoaded)));

    /// <summary>Waiting for game focus: the terror-radius heartbeat on the frame's glow (ambient).</summary>
    public static readonly DependencyProperty IsWaitingForFocusProperty = DependencyProperty.Register(
        nameof(IsWaitingForFocus), typeof(bool), typeof(MapPlate), new PropertyMetadata(false, (d, _) => ((MapPlate)d).UpdateAmbient()));

    /// <summary>The lobby campfire is live (flicker and embers; default true, the Status plate).</summary>
    public static readonly DependencyProperty IsLiveProperty = DependencyProperty.Register(
        nameof(IsLive), typeof(bool), typeof(MapPlate), new PropertyMetadata(true, (d, _) => ((MapPlate)d).ForEachScene(s => s.IsLive = ((MapPlate)d).IsLive)));

    /// <summary>The fire's x in the 400 x 200 campfire scene (default 304).</summary>
    public static readonly DependencyProperty FireXProperty = DependencyProperty.Register(
        nameof(FireX), typeof(double), typeof(MapPlate), new PropertyMetadata(304.0, (d, _) => ((MapPlate)d).ForEachScene(s => s.FireX = ((MapPlate)d).FireX)));

    /// <summary>The tallest frame, width / height (default 1.5: the plate takes spare height up to 3:2).</summary>
    public static readonly DependencyProperty MinAspectProperty = DependencyProperty.Register(
        nameof(MinAspect), typeof(double), typeof(MapPlate), new PropertyMetadata(1.5, (d, _) => ((MapPlate)d).UpdateFrame()));

    /// <summary>The widest frame, width / height (default 2: 2:1).</summary>
    public static readonly DependencyProperty MaxAspectProperty = DependencyProperty.Register(
        nameof(MaxAspect), typeof(double), typeof(MapPlate), new PropertyMetadata(2.0, (d, _) => ((MapPlate)d).UpdateFrame()));

    /// <summary>The picture width the frame cap is based on (default <see cref="Services.MapImageCatalog.BuiltInReferencePixelWidth"/>
    /// = 362, the narrowest built-in picture, so the frame does not change size from one map to the next and no picture
    /// is enlarged beyond <see cref="MaxUpscale"/>).</summary>
    public static readonly DependencyProperty ReferencePixelWidthProperty = DependencyProperty.Register(
        nameof(ReferencePixelWidth), typeof(double), typeof(MapPlate), new PropertyMetadata((double)Services.MapImageCatalog.BuiltInReferencePixelWidth, (d, _) => ((MapPlate)d).UpdateFrame()));

    /// <summary>The picture height the frame's height cap is based on (default
    /// <see cref="Services.MapImageCatalog.BuiltInReferencePixelHeight"/> = 239, the shortest built-in picture): the
    /// frame can be taller than the pictures' aspect (up to <see cref="MinAspect"/> = 3:2), and UniformToFill then
    /// scales a picture by its height, so the height is capped at <see cref="MaxUpscale"/> × this too.</summary>
    public static readonly DependencyProperty ReferencePixelHeightProperty = DependencyProperty.Register(
        nameof(ReferencePixelHeight), typeof(double), typeof(MapPlate), new PropertyMetadata((double)Services.MapImageCatalog.BuiltInReferencePixelHeight, (d, _) => ((MapPlate)d).UpdateFrame()));

    /// <summary>The largest enlargement of the picture (default 1.8).</summary>
    public static readonly DependencyProperty MaxUpscaleProperty = DependencyProperty.Register(
        nameof(MaxUpscale), typeof(double), typeof(MapPlate), new PropertyMetadata(Services.MapImageCatalog.MaxUpscale, (d, _) => ((MapPlate)d).UpdateFrame()));

    /// <summary>Where a capped frame sits (default centre).</summary>
    public static readonly DependencyProperty FrameAlignmentProperty = DependencyProperty.Register(
        nameof(FrameAlignment), typeof(HorizontalAlignment), typeof(MapPlate), new PropertyMetadata(HorizontalAlignment.Center, (d, _) => ((MapPlate)d).UpdateFrame()));

    /// <summary>Scene changes play their moment (default true); false swaps at once.</summary>
    public static readonly DependencyProperty AutoTransitionProperty = DependencyProperty.Register(
        nameof(AutoTransition), typeof(bool), typeof(MapPlate), new PropertyMetadata(true));

    /// <summary>The caption's first line size. Default NaN: it follows the window like the prototype's
    /// <c>clamp(28px, 2.8vw, 38px)</c> (<see cref="TypeRamp.PlateRealm"/>; 35.8 at 1280 px wide); a number fixes it.</summary>
    public static readonly DependencyProperty TitleFontSizeProperty = DependencyProperty.Register(
        nameof(TitleFontSize), typeof(double), typeof(MapPlate), new PropertyMetadata(double.NaN, (d, _) => ((MapPlate)d).ForEachCaption(c => c.TitleSize = ((MapPlate)d).TitleFontSize)));

    private static readonly Color Ember = CampfirePalette.EmberColor;
    private static readonly Color Entity = CampfirePalette.EntityColor;

    private readonly AspectFrame _frame;
    private readonly Grid _frameGrid = new();
    private readonly Border _glowBox;
    private readonly DropShadowEffect _glow = new() { ShadowDepth = 0, BlurRadius = 34, Opacity = 0, Color = Ember };
    private readonly Border _plateGlow;
    private readonly Grid _scenes = new() { ClipToBounds = true, Margin = new Thickness(1) };
    private readonly Grid _captions = new() { Margin = new Thickness(1) };
    private readonly SolidColorBrush _borderBrush = new(CampfirePalette.LineStrongColor);
    private readonly SolidColorBrush _ringBrush = new(Colors.Transparent);
    private readonly Canvas _sweep = new() { IsHitTestVisible = false, Opacity = 0 };
    private readonly MotionPolicyWatch _watch;
    private readonly AmbientLoop _heartbeat;
    private readonly List<MotionMoment> _moments = new();

    // scenes and captions a newer change has taken over while the moments that brought them stop: those moments' ends
    // must not drop them (the newer change lets them leave from where they were)
    private readonly HashSet<UIElement> _carried = new();
    private SceneHost? _current;
    private Caption? _caption;
    private MotionMoment? _toneMoment;
    private MotionMoment? _captionLand;
    private double _captionLandAt;
    private bool _silent;

    /// <summary>Creates an empty plate (the lobby with a burning fire, no caption).</summary>
    public MapPlate()
    {
        Focusable = false;
        IsTabStop = false;
        _glowBox = new Border { Background = CampfirePalette.NightDeep, CornerRadius = new CornerRadius(2), Effect = _glow };
        _plateGlow = new Border
        {
            Margin = new Thickness(-30),
            Opacity = 0,
            IsHitTestVisible = false,
            Background = CampfirePalette.Radial(0.5, 0.5, 0.5, 0.5,
                (0, CampfirePalette.Argb(0.36, Ember)), (0.62, CampfirePalette.Argb(0.12, Ember)), (1, CampfirePalette.Argb(0, Ember))),
        };
        var border = new Border { BorderBrush = _borderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), IsHitTestVisible = false, SnapsToDevicePixels = true };
        var ring = new Border { BorderBrush = _ringBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Margin = new Thickness(-1), IsHitTestVisible = false, SnapsToDevicePixels = true };
        SkillCheck = new SkillCheck();
        _frameGrid.Children.Add(_plateGlow);
        _frameGrid.Children.Add(_glowBox);
        _frameGrid.Children.Add(_scenes);
        _frameGrid.Children.Add(_captions);
        _frameGrid.Children.Add(border);
        _frameGrid.Children.Add(ring);
        _frameGrid.Children.Add(_sweep);
        _frameGrid.Children.Add(SkillCheck);
        _frame = new AspectFrame { Child = _frameGrid };
        Content = _frame;
        _heartbeat = new AmbientLoop(this, "MapPlate.heartbeat", BuildHeartbeat, AmbientLoop.LivelyFrameRate);
        _watch = new MotionPolicyWatch(this, OnPolicyChanged);
        UpdateFrame();
        ApplyTone(animate: false);
        UpdateGlowEffect(animating: false);
        AutomationProperties.SetName(this, "Map");
    }

    /// <summary>The skill check on the plate (and its score event).</summary>
    public SkillCheck SkillCheck { get; }

    /// <summary>The scene shown.</summary>
    public PlateScene? Scene
    {
        get => (PlateScene?)GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    /// <summary>Extra content under the caption.</summary>
    public object? CaptionTag
    {
        get => GetValue(CaptionTagProperty);
        set => SetValue(CaptionTagProperty, value);
    }

    /// <summary>The frame's border and glow.</summary>
    public PlateTone Tone
    {
        get => (PlateTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    /// <summary>Waiting for game focus (heartbeat).</summary>
    public bool IsWaitingForFocus
    {
        get => (bool)GetValue(IsWaitingForFocusProperty);
        set => SetValue(IsWaitingForFocusProperty, value);
    }

    /// <summary>The lobby campfire is live.</summary>
    public bool IsLive
    {
        get => (bool)GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }

    /// <summary>The fire's x in the campfire scene.</summary>
    public double FireX
    {
        get => (double)GetValue(FireXProperty);
        set => SetValue(FireXProperty, value);
    }

    /// <summary>The tallest frame (width / height).</summary>
    public double MinAspect
    {
        get => (double)GetValue(MinAspectProperty);
        set => SetValue(MinAspectProperty, value);
    }

    /// <summary>The widest frame (width / height).</summary>
    public double MaxAspect
    {
        get => (double)GetValue(MaxAspectProperty);
        set => SetValue(MaxAspectProperty, value);
    }

    /// <summary>The picture width the frame cap is based on.</summary>
    public double ReferencePixelWidth
    {
        get => (double)GetValue(ReferencePixelWidthProperty);
        set => SetValue(ReferencePixelWidthProperty, value);
    }

    /// <summary>The picture height the frame's height cap is based on.</summary>
    public double ReferencePixelHeight
    {
        get => (double)GetValue(ReferencePixelHeightProperty);
        set => SetValue(ReferencePixelHeightProperty, value);
    }

    /// <summary>The largest enlargement of the picture.</summary>
    public double MaxUpscale
    {
        get => (double)GetValue(MaxUpscaleProperty);
        set => SetValue(MaxUpscaleProperty, value);
    }

    /// <summary>Where a capped frame sits.</summary>
    public HorizontalAlignment FrameAlignment
    {
        get => (HorizontalAlignment)GetValue(FrameAlignmentProperty);
        set => SetValue(FrameAlignmentProperty, value);
    }

    /// <summary>Scene changes play their moment.</summary>
    public bool AutoTransition
    {
        get => (bool)GetValue(AutoTransitionProperty);
        set => SetValue(AutoTransitionProperty, value);
    }

    /// <summary>The caption's first line size (NaN: follows the window).</summary>
    public double TitleFontSize
    {
        get => (double)GetValue(TitleFontSizeProperty);
        set => SetValue(TitleFontSizeProperty, value);
    }

    /// <summary>The frame element (for layout checks).</summary>
    public FrameworkElement Frame => _frameGrid;

    /// <summary>The lobby's campfire scene, when the lobby is shown.</summary>
    public CampfireScene? Campfire => _current?.Campfire;

    /// <summary>The map picture, when a map is shown.</summary>
    public MapPicture? Picture => _current?.Picture;

    /// <summary>The heartbeat's clocks are running.</summary>
    public bool IsHeartbeatRunning => _heartbeat.IsRunning;

    /// <summary>Shows <paramref name="scene"/> with <paramref name="transition"/>.</summary>
    public void SetScene(PlateScene scene, PlateTransition transition)
    {
        PlateScene? old = Scene;
        _silent = true;
        try
        {
            Scene = scene;
        }
        finally
        {
            _silent = false;
        }

        Transition(old, scene, transition);
    }

    /// <summary>The current picture comes out of the fog (blur 10 → 0, the veil thins), then the names land.</summary>
    public void Reveal()
    {
        if (_current is null)
        {
            return;
        }

        StopMoments();
        if (!_watch.Policy.ShouldAnimate)
        {
            return;
        }

        var moment = NewMoment("MapPlate.reveal");
        AddReveal(moment, _current);
        _caption?.Land(moment, 600, quick: false, reduced: _watch.Policy.ReducedMotion);
        NoteCaptionLand(moment, 600);
        moment.Begin();
    }

    /// <summary>The current picture sinks into the fog and <paramref name="next"/> (usually the lobby) returns.</summary>
    public void Sink(PlateScene next) => SetScene(next, PlateTransition.Sink);

    /// <summary>
    /// ReShade confirmed the preset: an ember ignites at the bottom-left corner and runs both ways round the frame
    /// (820 ms); where the two flames meet the frame is lit (the border turns live at 780 ms); the light swells once.
    /// Ends with <see cref="Tone"/> = <see cref="PlateTone.Live"/>.
    /// </summary>
    public void Confirm()
    {
        PlateTone was = Tone;
        _silent = true;
        try
        {
            Tone = PlateTone.Live;
        }
        finally
        {
            _silent = false;
        }

        _toneMoment?.Stop();
        _toneMoment = null;
        ApplyTone(animate: false);
        MotionPolicy policy = _watch.Policy;
        if (!policy.ShouldAnimate)
        {
            return;
        }

        var moment = NewMoment("MapPlate.confirm");
        (Color b0, Color r0, double g0, double blur0, Color gc0) = ToneValues(was == PlateTone.Live ? PlateTone.Waiting : was);
        (Color b1, Color r1, double g1, double blur1, Color gc1) = ToneValues(PlateTone.Live);
        moment.Add(_borderBrush, SolidColorBrush.ColorProperty, Anim.Colors(0, new ColorKf(0, b0), new ColorKf(780, b0), new ColorKf(1040, b1, Ease.Out)));
        moment.Add(_ringBrush, SolidColorBrush.ColorProperty, Anim.Colors(0, new ColorKf(0, r0), new ColorKf(780, r0), new ColorKf(1040, r1, Ease.Out)));
        moment.Add(_glow, DropShadowEffect.OpacityProperty, Anim.Keys(0, new[] { new Kf(0, g0), new Kf(780, g0), new Kf(1040, g1, Ease.Out) }));
        moment.Add(_glow, DropShadowEffect.BlurRadiusProperty, Anim.Keys(0, new[] { new Kf(0, blur0), new Kf(780, blur0), new Kf(1040, blur1, Ease.Out) }));
        _glow.Color = gc1;
        moment.Add(_plateGlow, OpacityProperty, Anim.Keys(420, new[] { new Kf(0, 0), new Kf(455, 1, Ease.Out), new Kf(1300, 0, Ease.InOut) }));
        if (!policy.ReducedMotion)
        {
            AddEmberSweep(moment, 820);
        }

        UpdateGlowEffect(animating: true);
        moment.Ended += (_, _) => UpdateGlowEffect(animating: _toneMoment is { IsActive: true });
        moment.Begin();
    }

    /// <summary>
    /// Map found while scanning: lands the skill check (see <see cref="Controls.SkillCheck.LandGreat"/>) with the score
    /// event "Great skill check / Map found: <paramref name="mapName"/>", or, for a realm-only read (no map),
    /// "Realm found: <paramref name="realmName"/>"; returns the hit delay.
    /// </summary>
    public TimeSpan LandGreat(string? mapName, string? realmName = null) =>
        SkillCheck.LandGreat(
            !string.IsNullOrWhiteSpace(mapName) ? "Map found: " + mapName
            : !string.IsNullOrWhiteSpace(realmName) ? "Realm found: " + realmName
            : null);

    /// <summary>Freezes every running plate moment (and the skill check's) at <paramref name="time"/> (frame captures).</summary>
    public void FreezeAt(TimeSpan time)
    {
        foreach (MotionMoment moment in _moments.ToList())
        {
            moment.FreezeAt(time);
        }

        _current?.Campfire?.FreezeMomentAt(time);
        SkillCheck.FreezeAt(time);
    }

    private static (Color Border, Color Ring, double Glow, double Blur, Color GlowColor) ToneValues(PlateTone tone) => tone switch
    {
        PlateTone.Waiting => (CampfirePalette.Argb(0.5, Ember), CampfirePalette.Argb(0, Ember), 0.10, 22, Ember),
        PlateTone.Live => (CampfirePalette.Argb(0.85, Ember), CampfirePalette.Argb(0.25, Ember), 0.20, 34, Ember),
        PlateTone.Override => (CampfirePalette.Argb(0.85, Entity), CampfirePalette.Argb(0.25, Entity), 0.18, 30, Entity),
        _ => (CampfirePalette.LineStrongColor, CampfirePalette.Argb(0, Ember), 0, 22, Ember),
    };

    private MotionMoment NewMoment(string name)
    {
        var moment = new MotionMoment(this, name);
        _moments.Add(moment);
        moment.Ended += (_, _) => _moments.Remove(moment);
        return moment;
    }

    /// <summary>Stops every plate moment (each lands on its rest pose); the ghosts (leaving scenes and captions) go at
    /// once, unless a change carries them on (<paramref name="keepGhosts"/>).</summary>
    private void StopMoments(bool keepGhosts = false)
    {
        foreach (MotionMoment moment in _moments.ToList())
        {
            moment.Stop();
        }

        _moments.Clear();
        _sweep.Children.Clear();
        _sweep.Opacity = 0;
        if (keepGhosts)
        {
            return;
        }

        foreach (UIElement ghost in _scenes.Children.OfType<SceneHost>().Where(h => h != _current).ToList())
        {
            _scenes.Children.Remove(ghost);
        }

        foreach (UIElement ghost in _captions.Children.OfType<Caption>().Where(c => c != _caption).ToList())
        {
            _captions.Children.Remove(ghost);
        }
    }

    private void OnSceneChanged(PlateScene? old, PlateScene? scene)
    {
        if (_silent)
        {
            return;
        }

        // a property change animates only on screen (a plate being set up just takes its scene)
        Transition(old, scene, AutoTransition && IsLoaded ? PlateTransition.Auto : PlateTransition.None);
    }

    private void Transition(PlateScene? old, PlateScene? scene, PlateTransition kind)
    {
        if (kind == PlateTransition.Auto && old is not null && scene is not null && _current is not null && SameArt(old, scene))
        {
            ChangeCaption(scene);
            return;
        }

        if (scene is null)
        {
            StopMoments();
            _scenes.Children.Clear();
            _captions.Children.Clear();
            _current = null;
            _caption = null;
            return;
        }

        MotionPolicy policy = _watch.Policy;
        bool animate = kind != PlateTransition.None && old is not null && _current is not null && policy.ShouldAnimate;
        if (kind is PlateTransition.None or PlateTransition.Auto && old is { IsMap: false } && !scene.IsMap && _current?.Campfire is not null)
        {
            // lobby to lobby always keeps the clearing: the fire changes in place (animated or not)
            kind = PlateTransition.Fire;
        }

        if (kind == PlateTransition.Auto)
        {
            kind = old is null ? PlateTransition.None
                : !old.IsMap && scene.IsMap ? PlateTransition.Reveal
                : old.IsMap && !scene.IsMap ? PlateTransition.Sink
                : !old.IsMap && !scene.IsMap ? PlateTransition.Fire
                : PlateTransition.Swap;
        }

        if (kind == PlateTransition.Fire && (_current?.Campfire is null || scene.IsMap))
        {
            kind = PlateTransition.Swap;
        }

        // the caption: the old one always leaves first and fast, so two captions never overlap
        Caption? oldCaption = _caption;
        bool captionChanged = oldCaption is null || oldCaption.Title != scene.Title || oldCaption.Subtitle != scene.Subtitle || oldCaption.IsLobby != !scene.IsMap;

        // what the plate draws now (a moment may be mid-way), read before the moments stop: what leaves carries on from
        // there and what stays eases back to rest, instead of everything snapping to its rest pose first
        IReadOnlyList<(DependencyObject Target, DependencyProperty Property, object Value)> drawn = animate
            ? _moments.SelectMany(m => m.CurrentValues()).ToList()
            : Array.Empty<(DependencyObject, DependencyProperty, object)>();
        SceneHost? staying = kind == PlateTransition.Fire ? _current : null;
        Caption? stayingCaption = captionChanged ? null : oldCaption;
        List<FrameworkElement> leaving = animate
            ? _scenes.Children.OfType<SceneHost>().Where(h => h != staying).Cast<FrameworkElement>()
                .Concat(_captions.Children.OfType<Caption>().Where(c => c != stayingCaption)).ToList()
            : new List<FrameworkElement>();
        foreach (FrameworkElement element in leaving)
        {
            _carried.Add(element);
        }

        try
        {
            StopMoments(keepGhosts: animate);
        }
        finally
        {
            _carried.Clear();
        }

        foreach (FrameworkElement element in leaving)
        {
            Pose(drawn, element); // (the look it had: it leaves from there)
        }

        // the older leaving scenes and captions (a change cut short) fade out from where they are
        List<FrameworkElement> older = leaving.Where(e => e != _current && e != oldCaption).ToList();
        if (captionChanged)
        {
            _caption = new Caption(scene, TitleFontSize, CaptionTag);
            _captions.Children.Add(_caption);
        }

        if (kind == PlateTransition.Fire && _current?.Campfire is CampfireScene fire)
        {
            // lobby to lobby: the same clearing; the fire tells the change in place
            FireState from = fire.FireState;
            _current.Scene = scene;
            fire.SetFireState(scene.Fire, animate);
            double fogTo = scene.Fire == FireState.Embers ? 0.42 : 0;
            double fogWas = DrawnOr(drawn, _current.Fog, OpacityProperty, _current.Fog.Opacity);
            _current.Fog.Opacity = fogTo;
            MotionMoment? m = animate ? NewMoment("MapPlate.fire") : null;
            if (m is null)
            {
                RemoveGhosts(older);
            }
            else
            {
                // what the clearing still showed of an earlier moment eases back to rest; older ghosts fade out
                EaseBack(m, drawn, _current, (_current.Fog, OpacityProperty));
                if (stayingCaption is not null)
                {
                    EaseBack(m, drawn, stayingCaption);
                }

                LeaveGhosts(m, older);
                if (fogTo > fogWas && !policy.ReducedMotion)
                {
                    // burn-down: the fog thickens over the clearing
                    m.Add(_current.Fog, OpacityProperty, Anim.FromTo(fogWas, fogTo, 800, Ease.Out, 150));
                    m.Add(_current.BankA, TranslateTransform.XProperty, Anim.FromTo(-0.06 * _current.BankWidth, 0, 900, Ease.Out, 150));
                    m.Add(_current.BankB, TranslateTransform.XProperty, Anim.FromTo(0.08 * _current.BankWidth, 0, 900, Ease.Out, 150));
                }
                else if (Math.Abs(fogTo - fogWas) > 0.001)
                {
                    m.Add(_current.Fog, OpacityProperty, Anim.FromTo(fogWas, fogTo, policy.ReducedMotion ? 200 : 700));
                }
            }

            double capDelay = (from, scene.Fire) switch
            {
                (FireState.Burning, FireState.Out) => 260,
                (FireState.Burning, FireState.Embers) => 200,
                (_, FireState.Burning) when from != FireState.Burning => 160,
                _ => 40,
            };
            SwapCaptions(m, oldCaption, captionChanged, capDelay, quick: true);
            m?.Begin();
            return;
        }

        SceneHost? oldHost = _current;
        var host = new SceneHost(scene, IsLive, FireX);
        _current = host;
        _scenes.Children.Add(host);
        if (!animate || oldHost is null)
        {
            if (oldHost is not null)
            {
                _scenes.Children.Remove(oldHost);
            }

            if (oldCaption is not null && captionChanged)
            {
                _captions.Children.Remove(oldCaption);
            }

            RemoveGhosts(older);
            return;
        }

        // the old scene sits on top of the new one as a ghost (posed as it was drawn) and leaves the way the story says
        // it should; older ghosts fade out from where they are
        _scenes.Children.Remove(oldHost);
        _scenes.Children.Add(oldHost);
        var moment = NewMoment("MapPlate." + kind);
        bool reduced = policy.ReducedMotion;
        LeaveGhosts(moment, older);
        if (stayingCaption is not null)
        {
            EaseBack(moment, drawn, stayingCaption);
        }

        switch (kind)
        {
            case PlateTransition.Reveal:
            {
                Leave(moment, oldHost, 260, blur: reduced ? 0 : 6, ease: Ease.Out);
                AddReveal(moment, host);
                SwapCaptions(moment, oldCaption, captionChanged, 600, quick: false);
                break;
            }

            case PlateTransition.Sink:
            {
                // the picture sinks back into the fog, then the clearing returns from behind it
                if (oldHost.Picture is MapPicture picture && !reduced)
                {
                    // (from the look it has now: a picture still coming out of the fog sinks from there)
                    var img = picture.Picture;
                    double opacity0 = img.Opacity;
                    double blur0 = (img.Effect as BlurEffect)?.Radius ?? 0;
                    (double scale0, double y0) = ImagePose(img);
                    var move = new TranslateTransform();
                    var scale = new ScaleTransform(1, 1);
                    img.RenderTransformOrigin = new Point(0.5, 0.5);
                    img.RenderTransform = new TransformGroup { Children = { scale, move } };
                    var blur = new BlurEffect { Radius = 0 };
                    img.Effect = blur;
                    img.Opacity = 0.3;
                    move.Y = 18;
                    scale.ScaleX = scale.ScaleY = 0.985;
                    blur.Radius = Blur(10);
                    moment.Add(img, OpacityProperty, Anim.FromTo(opacity0, 0.3, 620, Ease.InOut));
                    moment.Add(move, TranslateTransform.YProperty, Anim.FromTo(y0, 18, 620, Ease.InOut));
                    moment.Add(scale, ScaleTransform.ScaleXProperty, Anim.FromTo(scale0, 0.985, 620, Ease.InOut));
                    moment.Add(scale, ScaleTransform.ScaleYProperty, Anim.FromTo(scale0, 0.985, 620, Ease.InOut));
                    moment.Add(blur, BlurEffect.RadiusProperty, Anim.FromTo(blur0, Blur(10), 620, Ease.InOut));
                }

                double fog0 = oldHost.Fog.Opacity, host0 = oldHost.Opacity;
                oldHost.Fog.Opacity = 0.92;
                moment.Add(oldHost.Fog, OpacityProperty, Anim.FromTo(fog0, 0.92, 460, Ease.Out, 80));
                oldHost.Opacity = 0;
                moment.Add(oldHost, OpacityProperty, Anim.FromTo(host0, 0, 420, Ease.Out, 560));
                moment.Add(host.Fog, OpacityProperty, Anim.FromTo(0.9, host.Fog.Opacity, 700, Ease.Out, 560));
                moment.Add(host.Art, OpacityProperty, Anim.FromTo(0, 1, 420, Ease.Out, 520));
                SwapCaptions(moment, oldCaption, captionChanged, 860, quick: false);
                break;
            }

            default:
            {
                // blur-bridged crossfade
                Leave(moment, oldHost, 180, blur: reduced ? 0 : 4, ease: Ease.Out);
                moment.Add(host.Art, OpacityProperty, Anim.FromTo(0, 1, 220));
                if (!reduced)
                {
                    var blur = new BlurEffect { Radius = 0 };
                    host.Art.Effect = blur;
                    moment.Add(blur, BlurEffect.RadiusProperty, Anim.FromTo(Blur(4), 0, 220));
                }

                SwapCaptions(moment, oldCaption, captionChanged, 110, quick: true);
                break;
            }
        }

        moment.Ended += (_, _) =>
        {
            // (a newer change may have taken these over: then they leave in its moment)
            if (!_carried.Contains(oldHost))
            {
                _scenes.Children.Remove(oldHost);
            }

            if (!_carried.Contains(host) && host.Art.Effect is BlurEffect)
            {
                host.Art.Effect = null;
            }
        };
        moment.Begin();
    }

    /// <summary>The value <paramref name="drawn"/> held for <paramref name="property"/> of <paramref name="target"/>, else <paramref name="fallback"/>.</summary>
    private static double DrawnOr(IReadOnlyList<(DependencyObject Target, DependencyProperty Property, object Value)> drawn, DependencyObject target, DependencyProperty property, double fallback)
    {
        foreach ((DependencyObject t, DependencyProperty p, object v) in drawn)
        {
            if (ReferenceEquals(t, target) && p == property && v is double value)
            {
                return value;
            }
        }

        return fallback;
    }

    /// <summary>Every object that makes up <paramref name="root"/>'s look: its elements and their transforms and effects.</summary>
    private static HashSet<DependencyObject> PartsOf(UIElement root)
    {
        var parts = new HashSet<DependencyObject>();
        void Add(DependencyObject? part)
        {
            if (part is not null && parts.Add(part) && part is TransformGroup group)
            {
                foreach (Transform child in group.Children)
                {
                    Add(child);
                }
            }
        }

        void Walk(DependencyObject node)
        {
            Add(node);
            if (node is UIElement element)
            {
                Add(element.RenderTransform);
                Add(element.Effect);
                Add(element.OpacityMask);
                Add(element.Clip);
            }

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }

        Walk(root);
        return parts;
    }

    /// <summary>Gives <paramref name="root"/>'s parts the values <paramref name="drawn"/> held (a leaving scene or caption
    /// keeps the look it had, now that the moments that animated it have stopped).</summary>
    private static void Pose(IReadOnlyList<(DependencyObject Target, DependencyProperty Property, object Value)> drawn, UIElement root)
    {
        if (drawn.Count == 0)
        {
            return;
        }

        HashSet<DependencyObject> parts = PartsOf(root);
        foreach ((DependencyObject target, DependencyProperty property, object value) in drawn)
        {
            if (parts.Contains(target) && target is not Freezable { IsFrozen: true })
            {
                target.SetValue(property, value);
            }
        }
    }

    /// <summary>What <paramref name="root"/> (a scene or caption that stays) still showed of a stopped moment eases back
    /// to its rest pose (200 ms ease-out) instead of jumping; <paramref name="skip"/> is animated by the new moment itself.</summary>
    private static void EaseBack(MotionMoment moment, IReadOnlyList<(DependencyObject Target, DependencyProperty Property, object Value)> drawn, UIElement root, params (DependencyObject Target, DependencyProperty Property)[] skip)
    {
        if (drawn.Count == 0)
        {
            return;
        }

        HashSet<DependencyObject> parts = PartsOf(root);
        foreach ((DependencyObject target, DependencyProperty property, object value) in drawn)
        {
            if (!parts.Contains(target) || skip.Any(s => ReferenceEquals(s.Target, target) && s.Property == property))
            {
                continue;
            }

            object rest = target.GetValue(property);
            if (value is double from && rest is double to && double.IsFinite(from) && double.IsFinite(to) && Math.Abs(from - to) > 0.0005)
            {
                moment.Add(target, property, Anim.FromTo(from, to, 200, Ease.Out));
            }
            else if (value is Color fromColor && rest is Color toColor && fromColor != toColor)
            {
                moment.Add(target, property, Anim.Colors(0, new ColorKf(0, fromColor), new ColorKf(200, toColor, Ease.Out)));
            }
        }
    }

    /// <summary>Older leaving scenes and captions (a change cut short) fade out from where they are (140 ms), then go.</summary>
    private void LeaveGhosts(MotionMoment moment, IEnumerable<FrameworkElement> ghosts)
    {
        foreach (FrameworkElement ghost in ghosts)
        {
            double from = ghost.Opacity;
            ghost.Opacity = 0;
            if (from < 0.01)
            {
                (ghost.Parent as Panel)?.Children.Remove(ghost);
                continue;
            }

            moment.Add(ghost, OpacityProperty, Anim.FromTo(from, 0, 140));
            moment.Ended += (_, _) =>
            {
                if (!_carried.Contains(ghost))
                {
                    (ghost.Parent as Panel)?.Children.Remove(ghost);
                }
            };
        }
    }

    /// <summary>Removes leaving scenes and captions at once (nothing may animate).</summary>
    private static void RemoveGhosts(IEnumerable<FrameworkElement> ghosts)
    {
        foreach (FrameworkElement ghost in ghosts)
        {
            (ghost.Parent as Panel)?.Children.Remove(ghost);
        }
    }

    /// <summary>The scale and the vertical shift a picture is drawn at (a reveal's zoom, a sink's drop), 1 and 0 at rest.</summary>
    private static (double Scale, double ShiftY) ImagePose(UIElement image) => image.RenderTransform switch
    {
        ScaleTransform scale => (scale.ScaleX, 0),
        TransformGroup group => (group.Children.OfType<ScaleTransform>().FirstOrDefault()?.ScaleX ?? 1, group.Children.OfType<TranslateTransform>().FirstOrDefault()?.Y ?? 0),
        _ => (1, 0),
    };

    /// <summary>The same picture (the same map) or the same clearing (the same fire): only the caption can differ.</summary>
    private static bool SameArt(PlateScene a, PlateScene b) =>
        a.IsMap == b.IsMap && (a.IsMap ? string.Equals(a.MapName, b.MapName, StringComparison.OrdinalIgnoreCase) : a.Fire == b.Fire);

    /// <summary>
    /// A new caption on the same art: the old caption leaves fast and the new one lands quickly (200 ms); the scene and
    /// whatever it is playing (a sink, a reveal, the fire's moment, the ember confirm) carry on untouched.
    /// </summary>
    private void ChangeCaption(PlateScene scene)
    {
        _current!.Scene = scene;
        Caption? oldCaption = _caption;
        if (oldCaption is not null && oldCaption.Title == scene.Title && oldCaption.Subtitle == scene.Subtitle && oldCaption.IsLobby == !scene.IsMap)
        {
            return;
        }

        if (oldCaption is not null && oldCaption.IsLobby == !scene.IsMap && _captionLand is { } land && land.CurrentTime is { } now &&
            now.TotalMilliseconds < _captionLandAt)
        {
            // the names have not landed yet (e.g. "Match ended" became "Global preset restored" while the map sinks):
            // they land with the new words, on the moment's own beat
            oldCaption.Retitle(scene);
            return;
        }

        _caption = new Caption(scene, TitleFontSize, CaptionTag);
        _captions.Children.Add(_caption);
        MotionMoment? moment = _watch.Policy.ShouldAnimate && IsLoaded ? NewMoment("MapPlate.caption") : null;
        SwapCaptions(moment, oldCaption, changed: true, 40, quick: true);
        moment?.Begin();
    }

    /// <summary>The caption lands <paramref name="delay"/> ms into <paramref name="moment"/> (forgotten when it ends, so an
    /// ended moment never keeps the scenes it animated alive).</summary>
    private void NoteCaptionLand(MotionMoment moment, double delay)
    {
        _captionLand = moment;
        _captionLandAt = _watch.Policy.ReducedMotion ? Math.Min(delay, Anim.ReducedMaxDelayMs) : delay; // (reduced motion keeps delays short)
        moment.Ended += (_, _) =>
        {
            if (ReferenceEquals(_captionLand, moment))
            {
                _captionLand = null;
            }
        };
    }

    /// <summary>CSS blur(px) → WPF BlurEffect radius (the Gaussian's spread differs).</summary>
    private static double Blur(double cssPx) => cssPx * 2.5;

    /// <summary>The scene leaves: it fades (and blurs) out from the look it has now.</summary>
    private void Leave(MotionMoment moment, SceneHost host, double ms, double blur, KeySpline ease)
    {
        double from = host.Opacity;
        double blurFrom = (host.Effect as BlurEffect)?.Radius ?? 0;
        host.Opacity = 0;
        moment.Add(host, OpacityProperty, Anim.FromTo(from, 0, ms, ease));
        if (blur > 0)
        {
            var effect = new BlurEffect { Radius = Blur(blur) };
            host.Effect = effect;
            moment.Add(effect, BlurEffect.RadiusProperty, Anim.FromTo(Math.Min(blurFrom, Blur(blur)), Blur(blur), ms, ease));
        }
    }

    private void AddReveal(MotionMoment moment, SceneHost host)
    {
        bool reduced = _watch.Policy.ReducedMotion;

        // fog clears to reveal the map: the veil thins and drifts off while the picture emerges from blur 10 → 0
        moment.Add(host.Fog, OpacityProperty, Anim.FromTo(1, host.Fog.Opacity, reduced ? 200 : 950, Ease.Out, reduced ? 0 : 60));
        if (reduced)
        {
            moment.Add(host.Art, OpacityProperty, Anim.FromTo(0, 1, 200));
            return;
        }

        moment.Add(host.BankA, TranslateTransform.XProperty, Anim.FromTo(0, 0.07 * host.BankWidth, 950, Ease.Out, 60));
        moment.Add(host.BankB, TranslateTransform.XProperty, Anim.FromTo(0, -0.10 * host.BankWidth, 950, Ease.Out, 60));
        if (host.Picture is MapPicture picture)
        {
            Image img = picture.Picture;
            var scale = new ScaleTransform(1, 1);
            img.RenderTransformOrigin = new Point(0.5, 0.5);
            img.RenderTransform = scale;
            var blur = new BlurEffect { Radius = 0 };
            img.Effect = blur;
            moment.Add(img, OpacityProperty, Anim.FromTo(0, 1, 700, Ease.Out, 140));
            moment.Add(blur, BlurEffect.RadiusProperty, Anim.FromTo(Blur(10), 0, 700, Ease.Out, 140));
            moment.Add(scale, ScaleTransform.ScaleXProperty, Anim.FromTo(1.05, 1, 700, Ease.Out, 140));
            moment.Add(scale, ScaleTransform.ScaleYProperty, Anim.FromTo(1.05, 1, 700, Ease.Out, 140));
            moment.Ended += (_, _) =>
            {
                // (unless a newer change carries this scene on as a leaving ghost, with the blur it had)
                if (!_carried.Contains(host) && ReferenceEquals(img.Effect, blur))
                {
                    img.Effect = null;
                }
            };
        }
        else
        {
            moment.Add(host.Art, OpacityProperty, Anim.FromTo(0, 1, 700, Ease.Out, 140));
        }
    }

    private void SwapCaptions(MotionMoment? moment, Caption? oldCaption, bool changed, double delay, bool quick)
    {
        if (!changed)
        {
            return;
        }

        if (oldCaption is not null)
        {
            if (moment is null)
            {
                _captions.Children.Remove(oldCaption);
            }
            else
            {
                double from = oldCaption.Opacity; // (from the look it has now)
                oldCaption.Opacity = 0;
                moment.Add(oldCaption, OpacityProperty, Anim.FromTo(from, 0, 140));
                moment.Ended += (_, _) =>
                {
                    if (!_carried.Contains(oldCaption))
                    {
                        _captions.Children.Remove(oldCaption);
                    }
                };
            }
        }

        if (moment is not null && _caption is not null)
        {
            // the new caption's shade takes over from the old one's (never both at once), then the names land
            if (oldCaption is not null)
            {
                moment.Add(_caption.Shade, OpacityProperty, Anim.FromTo(0, 1, 140));
            }

            _caption.Land(moment, delay, quick, _watch.Policy.ReducedMotion);
            NoteCaptionLand(moment, delay);
        }
    }

    private void AddEmberSweep(MotionMoment moment, double ms)
    {
        _sweep.Children.Clear();
        EmberSweep.Add(moment, _sweep, _frameGrid.ActualWidth, _frameGrid.ActualHeight, ms);
    }

    private void ApplyTone(bool animate)
    {
        if (_silent)
        {
            return;
        }

        (Color border, Color ring, double glow, double blur, Color glowColor) = ToneValues(Tone);
        var from = (_borderBrush.Color, _ringBrush.Color, _glow.Opacity, _glow.BlurRadius);
        _toneMoment?.Stop();
        _toneMoment = null;
        _borderBrush.Color = border;
        _ringBrush.Color = ring;
        _glow.Opacity = glow;
        _glow.BlurRadius = blur;
        _glow.Color = glowColor;
        if (!animate || _watch is null || !_watch.Policy.ShouldAnimate)
        {
            UpdateGlowEffect(animating: false);
            return;
        }

        var moment = new MotionMoment(this, "MapPlate.tone");
        moment.Add(_borderBrush, SolidColorBrush.ColorProperty, Anim.Colors(0, new ColorKf(0, from.Item1), new ColorKf(260, border, Ease.Out)));
        moment.Add(_ringBrush, SolidColorBrush.ColorProperty, Anim.Colors(0, new ColorKf(0, from.Item2), new ColorKf(260, ring, Ease.Out)));
        moment.Add(_glow, DropShadowEffect.OpacityProperty, Anim.FromTo(from.Item3, glow, 260));
        moment.Ended += (_, _) =>
        {
            if (ReferenceEquals(_toneMoment, moment))
            {
                _toneMoment = null;
                UpdateGlowEffect(animating: false);
            }
        };
        _toneMoment = moment;
        UpdateGlowEffect(animating: from.Item3 > 0.001 || glow > 0.001);
        moment.Begin();
    }

    /// <summary>
    /// The frame's glow (a DropShadowEffect) is attached only while it shows or animates: at opacity 0 (the neutral
    /// frame) it would still cost a shadow pass every time the window is re-composited (the fog drifts behind it).
    /// </summary>
    private void UpdateGlowEffect(bool animating)
    {
        bool show = animating || _glow.Opacity > 0.001;
        if (show && !ReferenceEquals(_glowBox.Effect, _glow))
        {
            _glowBox.Effect = _glow;
        }
        else if (!show && _glowBox.Effect is not null)
        {
            _glowBox.Effect = null;
        }
    }

    private void UpdateTag() => _caption?.SetTag(CaptionTag);

    private void UpdateFrame()
    {
        _frame.MinAspect = MinAspect;
        _frame.MaxAspect = MaxAspect;
        _frame.MaxWidthPixels = ReferencePixelWidth > 0 && MaxUpscale > 0 ? ReferencePixelWidth * MaxUpscale : double.NaN;
        _frame.MaxHeightPixels = ReferencePixelHeight > 0 && MaxUpscale > 0 ? ReferencePixelHeight * MaxUpscale : double.NaN;
        _frame.FrameAlignment = FrameAlignment;
    }

    private void ForEachScene(Action<CampfireScene> action)
    {
        foreach (SceneHost host in _scenes.Children.OfType<SceneHost>())
        {
            if (host.Campfire is CampfireScene scene)
            {
                action(scene);
            }
        }
    }

    private void ForEachCaption(Action<Caption> action)
    {
        foreach (Caption caption in _captions.Children.OfType<Caption>())
        {
            action(caption);
        }
    }

    private void OnPolicyChanged()
    {
        if (!_watch.Policy.ShouldAnimate)
        {
            StopMoments();
        }

        UpdateAmbient();
    }

    private void UpdateAmbient()
    {
        bool run = IsWaitingForFocus && IsLoaded && _watch.Policy.ShouldRunAmbient;
        if (run)
        {
            _heartbeat.Run();
        }
        else
        {
            _heartbeat.Reset();
        }
    }

    private IEnumerable<(DependencyObject, DependencyProperty, AnimationTimeline)> BuildHeartbeat()
    {
        // hbGlow: 0 % 0, 7 % .5, 17 % .14, 24 % .36, 52 %-100 % 0 (ease-out per segment), 1.2 s, first beat after 1 s
        const double p = 1200;
        DoubleAnimationUsingKeyFrames glow = Anim.Loop(p, new[]
        {
            new Kf(0, 0), new Kf(p * 0.07, 0.5, Ease.Out), new Kf(p * 0.17, 0.14, Ease.Out), new Kf(p * 0.24, 0.36, Ease.Out),
            new Kf(p * 0.52, 0, Ease.Out), new Kf(p, 0),
        });
        glow.BeginTime = TimeSpan.FromSeconds(1);
        yield return (_plateGlow, OpacityProperty, glow);
    }

    /// <summary>One scene on the plate: the picture or the campfire, with its own fog veil (two drifting banks).</summary>
    private sealed class SceneHost : Grid
    {
        private static readonly Brush[] BankABrushes =
        {
            CampfirePalette.Linear(new Point(0, 0), new Point(0, 1), (0, CampfirePalette.Argb(0.9, Color.FromRgb(27, 38, 43))), (1, CampfirePalette.Argb(0.92, Color.FromRgb(34, 46, 51)))),
            CampfirePalette.Radial(0.50, 1.10, 0.72, 0.50, (0, CampfirePalette.Argb(0.8, Color.FromRgb(96, 116, 124))), (0.76, CampfirePalette.Argb(0, Color.FromRgb(96, 116, 124)))),
            CampfirePalette.Radial(0.70, 0.30, 0.40, 0.38, (0, CampfirePalette.Argb(0.5, Color.FromRgb(112, 132, 140))), (0.70, CampfirePalette.Argb(0, Color.FromRgb(112, 132, 140)))),
            CampfirePalette.Radial(0.24, 0.68, 0.36, 0.44, (0, CampfirePalette.Argb(0.58, Color.FromRgb(126, 146, 153))), (0.70, CampfirePalette.Argb(0, Color.FromRgb(126, 146, 153)))),
        };

        private static readonly Brush[] BankBBrushes =
        {
            CampfirePalette.Radial(0.12, 0.30, 0.30, 0.24, (0, CampfirePalette.Argb(0.28, Color.FromRgb(138, 156, 162))), (0.70, CampfirePalette.Argb(0, Color.FromRgb(138, 156, 162)))),
            CampfirePalette.Radial(0.82, 0.72, 0.34, 0.26, (0, CampfirePalette.Argb(0.36, Color.FromRgb(138, 156, 162))), (0.70, CampfirePalette.Argb(0, Color.FromRgb(138, 156, 162)))),
            CampfirePalette.Radial(0.46, 0.52, 0.28, 0.30, (0, CampfirePalette.Argb(0.34, Color.FromRgb(150, 166, 172))), (0.70, CampfirePalette.Argb(0, Color.FromRgb(150, 166, 172)))),
        };

        private readonly Grid _bankGridA = new();
        private readonly Grid _bankGridB = new();

        public SceneHost(PlateScene scene, bool live, double fireX)
        {
            Scene = scene;
            ClipToBounds = true;
            if (scene.IsMap)
            {
                Picture = new MapPicture { MapName = scene.MapName, PlaceholderLabel = MapPlaceholderLabel.Unavailable };
                Art = Picture;
            }
            else
            {
                Campfire = new CampfireScene { AnimateChanges = false, FireState = scene.Fire, IsLive = live, FireX = fireX };
                Art = Campfire;
            }

            Children.Add(Art);
            foreach (Brush b in BankABrushes)
            {
                _bankGridA.Children.Add(new Rectangle { Fill = b });
            }

            foreach (Brush b in BankBBrushes)
            {
                _bankGridB.Children.Add(new Rectangle { Fill = b });
            }

            _bankGridA.RenderTransform = BankA;
            _bankGridB.RenderTransform = BankB;
            Fog = new Grid { IsHitTestVisible = false, ClipToBounds = true, Opacity = !scene.IsMap && scene.Fire == FireState.Embers ? 0.42 : 0 };
            Fog.Children.Add(_bankGridA);
            Fog.Children.Add(_bankGridB);
            Children.Add(Fog);
            SizeChanged += (_, e) => LayoutBanks(e.NewSize);
        }

        public PlateScene Scene { get; set; }

        public FrameworkElement Art { get; }

        public MapPicture? Picture { get; }

        public CampfireScene? Campfire { get; }

        public Grid Fog { get; }

        public TranslateTransform BankA { get; } = new();

        public TranslateTransform BankB { get; } = new();

        public double BankWidth { get; private set; } = 1000;

        private void LayoutBanks(Size size)
        {
            // each bank overhangs the plate: inset -12 % -22 %
            BankWidth = size.Width * 1.44;
            var margin = new Thickness(-0.22 * size.Width, -0.12 * size.Height, -0.22 * size.Width, -0.12 * size.Height);
            _bankGridA.Margin = margin;
            _bankGridB.Margin = margin;
        }
    }

    /// <summary>The caption: realm / map (or the lobby title / line) and an optional tag, over a dark fade.</summary>
    private sealed class Caption : Grid
    {
        private readonly TextBlock _title;
        private readonly TextBlock _subtitle;
        private readonly ContentPresenter _tag;
        private readonly StackPanel _stack;

        public Caption(PlateScene scene, double titleSize, object? tag)
        {
            Title = scene.Title;
            Subtitle = scene.Subtitle;
            IsLobby = !scene.IsMap;
            VerticalAlignment = VerticalAlignment.Bottom;
            IsHitTestVisible = false;
            var fade = CampfirePalette.Linear(new Point(0, 0), new Point(0, 1),
                (0, CampfirePalette.Argb(0, CampfirePalette.NightDeepColor)), (0.62, CampfirePalette.Argb(IsLobby ? 0.9 : 0.88, CampfirePalette.NightDeepColor)), (1, CampfirePalette.Argb(IsLobby ? 0.9 : 0.88, CampfirePalette.NightDeepColor)));
            var background = new Border { Background = fade };
            Shade = background;
            if (IsLobby)
            {
                // the lobby caption keeps the fire visible: its shade fades out to the right
                background.OpacityMask = CampfirePalette.Linear(new Point(0, 0), new Point(1, 0), (0, Colors.Black), (0.46, Colors.Black), (0.84, Colors.Transparent));
            }

            Children.Add(background);
            _title = new TextBlock
            {
                Text = scene.Title,
                FontFamily = CampfireFonts.Display,
                FontWeight = IsLobby ? FontWeights.SemiBold : FontWeights.ExtraBold,
                Foreground = CampfirePalette.Bone,
                TextWrapping = TextWrapping.Wrap,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };
            _subtitle = new TextBlock
            {
                Text = scene.Subtitle,
                Margin = new Thickness(0, 6, 0, 0),
                FontFamily = CampfireFonts.Body,
                FontWeight = FontWeights.Medium,
                FontSize = 15,
                Foreground = CampfirePalette.Bone,
                Opacity = 0.9,
                TextWrapping = TextWrapping.Wrap,
            };
            _tag = new ContentPresenter { Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            _stack = new StackPanel { Margin = new Thickness(20, 44, 20, 16) };
            if (IsLobby)
            {
                SizeChanged += (_, e) => _stack.MaxWidth = Math.Max(120, e.NewSize.Width * 0.6 - 20);
                _stack.HorizontalAlignment = HorizontalAlignment.Left;
            }

            _stack.Children.Add(_title);
            _stack.Children.Add(_subtitle);
            _stack.Children.Add(_tag);
            Children.Add(_stack);
            TitleSize = titleSize;
            SetTag(tag);
            AutomationProperties.SetName(this, scene.Title + ", " + scene.Subtitle);
        }

        public string Title { get; private set; }

        public string Subtitle { get; private set; }

        public Border Shade { get; }

        public bool IsLobby { get; }

        public double TitleSize
        {
            get => _title.FontSize;
            set
            {
                if (double.IsNaN(value) || value <= 0)
                {
                    // follow the window: the size comes from the PlateRealm ramp (the style's 36 / 34 until loaded)
                    _title.ClearValue(TextBlock.FontSizeProperty);
                    _title.ClearValue(TextBlock.LineHeightProperty);
                    _title.Style = CreateTitleStyle();
                    return;
                }

                _title.Style = null;
                _title.FontSize = value;
                _title.LineHeight = value * 0.95;
            }
        }

        // a style per caption (styles belong to one thread): the 1280-px size as the base value, and the ramp
        private static Style CreateTitleStyle()
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.FontSizeProperty, 36.0));
            style.Setters.Add(new Setter(TextBlock.LineHeightProperty, 34.2));
            style.Setters.Add(new Setter(CampfireType.RampProperty, TypeRamp.PlateRealm));
            return style;
        }

        /// <summary>New words for a caption that has not landed yet (same kind: map or lobby).</summary>
        public void Retitle(PlateScene scene)
        {
            Title = scene.Title;
            Subtitle = scene.Subtitle;
            _title.Text = scene.Title;
            _subtitle.Text = scene.Subtitle;
            AutomationProperties.SetName(this, scene.Title + ", " + scene.Subtitle);
        }

        public void SetTag(object? tag)
        {
            _tag.Content = tag;
            _tag.Visibility = tag is null ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>The names land: each line fades up 10 px (380 ms, quick 200 ms), 50 ms apart.</summary>
        public void Land(MotionMoment moment, double delay, bool quick, bool reduced)
        {
            int i = 0;
            foreach (UIElement child in _stack.Children)
            {
                // (the tag lands too, even when it is set a moment after the scene)
                double d = delay + i * 50;
                double ms = quick ? 200 : 380;
                moment.Add(child, OpacityProperty, Anim.FromTo(0, 1, reduced ? Math.Min(ms, 200) : ms, Ease.Out, d));
                if (!reduced)
                {
                    var move = new TranslateTransform();
                    child.RenderTransform = move;
                    moment.Add(move, TranslateTransform.YProperty, Anim.FromTo(10, 0, ms, Ease.Out, d));
                }

                i++;
            }
        }
    }
}

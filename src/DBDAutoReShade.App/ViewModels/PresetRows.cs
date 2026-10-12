using System.IO;
using System.Windows.Input;
using DBDAutoReShade.App.Infrastructure;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.App.ViewModels;

/// <summary>The dot at the end of a Presets row.</summary>
public enum RowMark
{
    /// <summary>None.</summary>
    None,

    /// <summary>The active realm (or Global, active now).</summary>
    Ember,

    /// <summary>The detected realm, waiting for game focus.</summary>
    Ring,

    /// <summary>Its shortcut needs a DBD restart.</summary>
    Red,
}

/// <summary>
/// One row of the Presets list: the pinned Global / Menu row or a realm. Rows live as long as the realm list is
/// unchanged; changes update their properties, so the row the user is clicking is never recreated under the mouse.
/// A property is raised only when its value changes: the view model refreshes on every controller event.
/// </summary>
public abstract class PresetRowBase : ObservableObject
{
    private string _presetText = string.Empty;
    private bool _isUnset = true;
    private bool _needsRestart;
    private bool _needsReshadeIni;
    private RowMark _mark;
    private bool _isVisible = true;
    private bool _isSelected;
    private string _metaText = string.Empty;
    private bool _isMetaHit;

    /// <summary>The selection key: the realm name, or <see cref="AppConfigKeys.GlobalShortcutKey"/>.</summary>
    public abstract string Key { get; }

    /// <summary>The row's title.</summary>
    public abstract string DisplayName { get; }

    /// <summary>The pinned Global / Menu row.</summary>
    public abstract bool IsGlobal { get; }

    /// <summary>The maps shown in the row's strip (empty for Global).</summary>
    public abstract IReadOnlyList<string> MapNames { get; }

    /// <summary>The second line: the preset file, or what happens without one.</summary>
    public string PresetText
    {
        get => _presetText;
        protected set
        {
            if (SetProperty(ref _presetText, value))
            {
                RaisePropertyChanged(nameof(AccessibleName));
            }
        }
    }

    /// <summary>No preset is assigned (the row sits in the fog, its text in italics).</summary>
    public bool IsUnset
    {
        get => _isUnset;
        protected set
        {
            if (SetProperty(ref _isUnset, value))
            {
                RaisePropertyChanged(nameof(AccessibleName));
            }
        }
    }

    /// <summary>The preset's shortcut needs a DBD restart (red text and dot).</summary>
    public bool NeedsRestart
    {
        get => _needsRestart;
        set
        {
            if (SetProperty(ref _needsRestart, value))
            {
                RaisePropertyChanged(nameof(AccessibleName));
            }
        }
    }

    /// <summary>
    /// The preset's shortcut can't be loaded until the right ReShade.ini is set (red text and dot): the configured one
    /// is not set or was not found, so a DBD restart alone can't help.
    /// </summary>
    public bool NeedsReshadeIni
    {
        get => _needsReshadeIni;
        set
        {
            if (SetProperty(ref _needsReshadeIni, value))
            {
                RaisePropertyChanged(nameof(AccessibleName));
            }
        }
    }

    /// <summary>The dot at the end of the row (said by <see cref="AccessibleName"/> too).</summary>
    public RowMark Mark
    {
        get => _mark;
        set
        {
            if (SetProperty(ref _mark, value))
            {
                RaisePropertyChanged(nameof(AccessibleName));
            }
        }
    }

    /// <summary>The row matches the filter.</summary>
    public bool IsVisible { get => _isVisible; set => SetProperty(ref _isVisible, value); }

    /// <summary>The row is the selected one (its detail is shown).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                RaisePropertyChanged(nameof(AccessibleName)); // (the pinned row says it: it is not a list item)
            }
        }
    }

    /// <summary>The third line: "5 maps", or the map the filter matched ("Map: Coal Tower").</summary>
    public string MetaText
    {
        get => _metaText;
        set
        {
            if (SetProperty(ref _metaText, value))
            {
                RaisePropertyChanged(nameof(AccessibleName));
            }
        }
    }

    /// <summary>The third line names the map the filter matched.</summary>
    public bool IsMetaHit { get => _isMetaHit; set => SetProperty(ref _isMetaHit, value); }

    /// <summary>The row's shortcut can't be switched to yet (a DBD restart, or the right ReShade.ini first): the red dot.</summary>
    public bool IsBlocked => NeedsRestart || NeedsReshadeIni;

    /// <summary>
    /// What a screen reader says for the row: everything the row shows, the dot included ("Coldwind Farm,
    /// 03.STX.Coldwind Farm.ini, 5 maps, active realm"; "…, restart needed"; "…, ReShade.ini needed").
    /// </summary>
    public string AccessibleName
    {
        get
        {
            var parts = new List<string>(5) { DisplayName, PresetText };
            if (!string.IsNullOrWhiteSpace(MetaText))
            {
                parts.Add(MetaText);
            }

            string? mark = Mark switch
            {
                RowMark.Ember => IsGlobal ? "active now" : "active realm",
                RowMark.Ring => "detected, waiting for game focus",
                _ => null,
            };
            if (mark is not null)
            {
                parts.Add(mark);
            }

            if (NeedsRestart)
            {
                parts.Add("restart needed");
            }

            if (NeedsReshadeIni)
            {
                parts.Add("ReShade.ini needed");
            }

            AddStateParts(parts);
            return string.Join(", ", parts);
        }
    }

    /// <summary>Adds what the row's control can't say by itself to <see cref="AccessibleName"/> (default nothing).</summary>
    protected virtual void AddStateParts(List<string> parts)
    {
    }
}

/// <summary>One realm row on the Presets tab.</summary>
public sealed class RealmRow : PresetRowBase
{
    /// <summary>Clear button tooltip while a Global preset is set.</summary>
    public const string ClearToolTipWithGlobal = "Clear (detecting this realm then switches to the Global preset)";

    /// <summary>Clear button tooltip while no Global preset is set.</summary>
    public const string ClearToolTipWithoutGlobal = "Clear (no Global preset is set, so detecting this realm then switches nothing)";

    private readonly MainViewModel _parent;
    private readonly RelayCommand _testCommand;
    private readonly RelayCommand _clearCommand;
    private IReadOnlyList<string> _maps;
    private string _presetName = "Not configured";
    private bool _hasPreset;
    private string _clearToolTip = ClearToolTipWithoutGlobal;
    private string? _presetPath;

    /// <param name="parent">The view model.</param>
    /// <param name="realmName">The realm.</param>
    /// <param name="presetPath">Its preset (null/blank: none).</param>
    /// <param name="hasGlobalPreset">Whether a Global preset is set.</param>
    /// <param name="maps">The realm's maps (database order).</param>
    public RealmRow(MainViewModel parent, string realmName, string? presetPath, bool hasGlobalPreset, IReadOnlyList<string>? maps = null)
    {
        _parent = parent;
        RealmName = realmName;
        _maps = maps ?? Array.Empty<string>();
        _testCommand = new RelayCommand(() => _parent.TestRealm(RealmName), () => HasPreset);
        _clearCommand = new RelayCommand(() => _parent.ClearRealmPreset(RealmName), () => HasPreset);
        SelectCommand = new RelayCommand(() => _parent.SelectRealmPreset(RealmName));
        MetaText = MapCountText(_maps.Count);
        Update(presetPath, hasGlobalPreset);
    }

    /// <summary>The realm.</summary>
    public string RealmName { get; }

    /// <inheritdoc />
    public override string Key => RealmName;

    /// <inheritdoc />
    public override string DisplayName => RealmName;

    /// <inheritdoc />
    public override bool IsGlobal => false;

    /// <inheritdoc />
    public override IReadOnlyList<string> MapNames => _maps;

    /// <summary>The preset's file name, or "Not configured".</summary>
    public string PresetName { get => _presetName; private set => SetProperty(ref _presetName, value); }

    /// <summary>The preset's full path (null: none).</summary>
    public string? PresetPath => _presetPath;

    /// <summary>A preset is assigned.</summary>
    public bool HasPreset => _hasPreset;

    /// <summary>Tests the preset (waits up to 15 s for DBD focus).</summary>
    public ICommand TestCommand => _testCommand;

    /// <summary>Picks a preset file for the realm.</summary>
    public ICommand SelectCommand { get; }

    /// <summary>Clears the realm's preset at once.</summary>
    public ICommand ClearCommand => _clearCommand;

    /// <summary>What clearing means: the realm falls back to Global only when one is set.</summary>
    public string ClearToolTip { get => _clearToolTip; private set => SetProperty(ref _clearToolTip, value); }

    /// <summary>"1 map" / "5 maps".</summary>
    public static string MapCountText(int count) => count == 1 ? "1 map" : $"{count} maps";

    /// <summary>
    /// Shows the realm's maps as the map database lists them now (a database update can add maps to a realm); true when
    /// they changed. The meta line is the filter's to set (it may name a matched map).
    /// </summary>
    public bool UpdateMaps(IReadOnlyList<string> maps)
    {
        ArgumentNullException.ThrowIfNull(maps);
        if (_maps.SequenceEqual(maps, StringComparer.Ordinal))
        {
            return false;
        }

        _maps = maps.ToList();
        RaisePropertyChanged(nameof(MapNames));
        return true;
    }

    /// <summary>Shows the realm's current assignment (null/blank = none) and whether a Global preset is set.</summary>
    public void Update(string? presetPath, bool hasGlobalPreset)
    {
        bool has = !string.IsNullOrWhiteSpace(presetPath);
        _presetPath = has ? presetPath : null;
        PresetName = has ? Path.GetFileName(presetPath!) : "Not configured";
        PresetText = has ? Path.GetFileName(presetPath!) : hasGlobalPreset ? "Uses Global / Menu" : "No preset; nothing switches";
        IsUnset = !has;
        ClearToolTip = hasGlobalPreset ? ClearToolTipWithGlobal : ClearToolTipWithoutGlobal;
        if (SetProperty(ref _hasPreset, has, nameof(HasPreset)))
        {
            _testCommand.RaiseCanExecuteChanged();
            _clearCommand.RaiseCanExecuteChanged();
        }
    }
}

/// <summary>The pinned Global / Menu row.</summary>
public sealed class GlobalPresetRow : PresetRowBase
{
    /// <summary>The row's title and the override list's name for it.</summary>
    public const string Label = "Global / Menu";

    private string? _presetPath;

    /// <summary>Creates the row (no preset).</summary>
    public GlobalPresetRow() => Update(null);

    /// <inheritdoc />
    public override string Key => AppConfigKeys.GlobalShortcutKey;

    /// <inheritdoc />
    public override string DisplayName => Label;

    /// <inheritdoc />
    public override bool IsGlobal => true;

    /// <inheritdoc />
    public override IReadOnlyList<string> MapNames => Array.Empty<string>();

    /// <summary>The Global preset's full path (null: none).</summary>
    public string? PresetPath => _presetPath;

    /// <summary>Shows the Global preset (null/blank = none).</summary>
    public void Update(string? presetPath)
    {
        bool has = !string.IsNullOrWhiteSpace(presetPath);
        _presetPath = has ? presetPath : null;
        PresetText = has ? Path.GetFileName(presetPath!) : "No preset";
        IsUnset = !has;
    }

    /// <summary>
    /// The pinned row is a button, not an item of the realm list, so nothing else tells assistive technology that its
    /// detail is the one shown: its name says "selected".
    /// </summary>
    protected override void AddStateParts(List<string> parts)
    {
        if (IsSelected)
        {
            parts.Add("selected");
        }
    }
}

/// <summary>One map of the selected realm (a thumbnail under the hero picture).</summary>
public sealed class MapThumb : ObservableObject
{
    private bool _isSelected;
    private bool _isDetected;

    /// <param name="name">The map.</param>
    /// <param name="index">Its index in the realm.</param>
    /// <param name="select">Shows it as the hero picture.</param>
    public MapThumb(string name, int index, Action<int> select)
    {
        Name = name;
        Index = index;
        SelectCommand = new RelayCommand(() => select(index));
    }

    /// <summary>The map.</summary>
    public string Name { get; }

    /// <summary>Its index in the realm.</summary>
    public int Index { get; }

    /// <summary>It is the hero picture.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                RaisePropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>It is the detected map (an ember dot).</summary>
    public bool IsDetected
    {
        get => _isDetected;
        set
        {
            if (SetProperty(ref _isDetected, value))
            {
                RaisePropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>
    /// What the thumbnail's look says, for assistive technology (its item status): "Shown" (the hero picture, the bone
    /// frame), "Detected map" (the ember dot), both, or nothing.
    /// </summary>
    public string StatusText => (IsSelected, IsDetected) switch
    {
        (true, true) => "Shown, detected map",
        (true, false) => "Shown",
        (false, true) => "Detected map",
        _ => string.Empty,
    };

    /// <summary>Shows it as the hero picture.</summary>
    public ICommand SelectCommand { get; }
}

/// <summary>What the preset panel's feedback area shows.</summary>
public enum FeedbackKind
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>The Test countdown: waiting for a click into the game (the 15 s ring).</summary>
    Test,

    /// <summary>"Clear the preset for …?" with Clear preset / Keep it.</summary>
    ClearConfirm,

    /// <summary>A message (the outcome of a test, an assignment, a clear).</summary>
    Message,
}

/// <summary>The preset panel's feedback for one row.</summary>
/// <param name="Kind">What is shown.</param>
/// <param name="Key">The row it belongs to.</param>
/// <param name="Text">The message or question.</param>
/// <param name="File">The tested preset's file name (Test).</param>
/// <param name="Elapsed">
/// How far into its 15 s focus wait the test is (Test), as the controller tells it whenever the feedback is shown.
/// </param>
/// <param name="Press">Which press of Test it is (Test): every press restarts the controller's wait, and the ring with it.</param>
/// <param name="Serial">
/// The controller's number for the Test this countdown follows (<see cref="Services.PendingTest.Serial"/>): its outcome
/// is the one shown when the wait ends (<see cref="Services.AppController.LastTestOutcome"/>).
/// </param>
public sealed record PresetFeedback(FeedbackKind Kind, string Key, string Text, string? File = null, TimeSpan Elapsed = default, int Press = 0, long Serial = 0)
{
    /// <summary>No feedback.</summary>
    public static PresetFeedback None { get; } = new(FeedbackKind.None, string.Empty, string.Empty);
}

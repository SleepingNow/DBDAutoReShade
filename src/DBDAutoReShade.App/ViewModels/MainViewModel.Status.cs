using System.IO;
using System.Windows.Input;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Infrastructure;
using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.App.ViewModels;

/// <summary>The Status screen: the state hero, the active preset, the detection, the map plate and the override.</summary>
public sealed partial class MainViewModel
{
    private StatusScreen _statusScreen = new();
    private string? _selectedOverrideRealm;
    private bool _isManualOverride;
    private int _userDriven;
    private RelayCommand _applyOverrideCommand = null!;
    private RelayCommand _returnToAutoCommand = null!;

    /// <summary>What the Status screen shows now (a new instance whenever anything on it changes).</summary>
    public StatusScreen StatusScreen
    {
        get => _statusScreen;
        private set
        {
            if (SetProperty(ref _statusScreen, value))
            {
                RaisePropertyChanged(nameof(BackdropMap));
            }
        }
    }

    /// <summary>
    /// True while the user's own Status command runs ("Apply override" / "Switch to Global", "Return to automatic"): the
    /// screens it brings are told as a quick, user-driven swap, not as the detection story (the prototype's
    /// quickStatusMotion). The controller answers these commands on the calling thread, so the refreshes they cause
    /// arrive while this is set.
    /// </summary>
    public bool IsUserDrivenChange => _userDriven > 0;

    /// <summary>Realm names for the override list; replaced only when the realm list itself changes.</summary>
    public List<string> AvailableRealms { get; private set; } = new();

    /// <summary>
    /// The override list: "Global / Menu" first (it switches to the Global preset now, like the Global preset hotkey),
    /// then the realms. Replaced only when the realm list itself changes (an open drop-down keeps its list).
    /// </summary>
    public List<string> OverrideChoices { get; private set; } = new() { GlobalPresetRow.Label };

    /// <summary>The realm the override would force, or "Global / Menu".</summary>
    public string? SelectedOverrideRealm
    {
        get => _selectedOverrideRealm;
        set
        {
            if (SetProperty(ref _selectedOverrideRealm, value))
            {
                _applyOverrideCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(ApplyOverrideText));
                RaisePropertyChanged(nameof(OverrideDescription));
            }
        }
    }

    /// <summary>"Apply override", or "Switch to Global" while "Global / Menu" is chosen.</summary>
    public string ApplyOverrideText => GlobalChosen ? "Switch to Global" : "Apply override";

    /// <summary>The override bar's line: what the chosen action does ("Global / Menu" forces nothing: it switches now).</summary>
    public string OverrideDescription => GlobalChosen
        ? GlobalOverrideDescription
        : RealmOverrideDescription;

    /// <summary>The override bar's line for a realm (and before anything is chosen).</summary>
    public const string RealmOverrideDescription = "Force one realm's preset until you return to automatic.";

    /// <summary>The override bar's line while "Global / Menu" is chosen.</summary>
    public const string GlobalOverrideDescription = "Switch to the Global preset now. A running scan stops; automatic switching stays on.";

    /// <summary>
    /// The map plate's line under the realm after a realm-only read: the scan read the realm but no map, so the plate
    /// shows the campfire clearing (no map picture is passed off as the detected one).
    /// </summary>
    public const string RealmOnlyPlateLine = "Realm detected — the map name wasn't read.";

    /// <summary>"Global / Menu" is chosen in the override list.</summary>
    private bool GlobalChosen => string.Equals(SelectedOverrideRealm, GlobalPresetRow.Label, StringComparison.Ordinal);

    /// <summary>A manual override is on (Return to automatic is enabled).</summary>
    public bool IsManualOverride
    {
        get => _isManualOverride;
        private set
        {
            if (SetProperty(ref _isManualOverride, value))
            {
                _returnToAutoCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Forces the selected realm's preset.</summary>
    public ICommand ApplyOverrideCommand => _applyOverrideCommand;

    /// <summary>Ends the override (enabled while one is on).</summary>
    public ICommand ReturnToAutoCommand => _returnToAutoCommand;

    /// <summary>The restart banner's action: the Presets tab, on the realm that needs the restart.</summary>
    public ICommand ShowRestartInPresetsCommand { get; private set; } = null!;

    /// <summary>
    /// Forces <see cref="SelectedOverrideRealm"/>'s preset until Return to automatic. "Global / Menu" switches to the
    /// Global preset now instead (<c>AppController.ActivateGlobalNow</c>, as the Global preset hotkey does: a running
    /// scan stops, an override ends, automatic switching stays on). A user-driven change (<see cref="IsUserDrivenChange"/>).
    /// </summary>
    public void ApplyOverride() => UserDriven(() =>
    {
        if (GlobalChosen)
        {
            _controller.ActivateGlobalNow();
            return;
        }

        _controller.ApplyManualOverride(SelectedOverrideRealm);
    });

    /// <summary>The same file (full paths compared ignoring case); false when either is blank or invalid.</summary>
    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void InitializeStatus()
    {
        _applyOverrideCommand = new RelayCommand(() => ApplyOverride(), () => !string.IsNullOrWhiteSpace(SelectedOverrideRealm));
        _returnToAutoCommand = new RelayCommand(() => UserDriven(_controller.ReturnToAutomatic), () => IsManualOverride);
        ShowRestartInPresetsCommand = new RelayCommand(() => ShowRestartInPresets());
    }

    /// <summary>Runs one of the user's own Status commands (see <see cref="IsUserDrivenChange"/>).</summary>
    private void UserDriven(Action command)
    {
        _userDriven++;
        try
        {
            command();
        }
        finally
        {
            _userDriven--;
        }
    }

    private void RefreshStatus()
    {
        IsManualOverride = _controller.IsManualOverride;
        StatusScreen = BuildStatusScreen();
    }

    /// <summary>Computes the Status screen from the controller (the prototype's states, with the app's real texts).</summary>
    private StatusScreen BuildStatusScreen()
    {
        var c = _controller;
        AppState state = c.States.State;
        bool running = c.DbdRunning;
        string hotkey = c.Config.DetectionHotkey.ToString();
        string status = c.StatusMessage;
        string detection = c.DetectionText;
        DetectionResult? detected = c.LastDetection;
        string? global = c.Config.GlobalPresetPath;
        string? active = c.ActivePresetPath;
        bool globalActive = SamePath(global, active);

        ScreenState screen = state switch
        {
            AppState.WaitingForDbd => ScreenState.Waiting,
            AppState.DetectingLoadingScreen => c.IsScanPaused ? ScreenState.Paused : ScreenState.Scanning,
            AppState.RealmIdentified or AppState.WaitingForGameplay => ScreenState.WaitingFocus,
            AppState.PresetActive => ScreenState.Active,
            AppState.ManualOverride => ScreenState.Override,
            AppState.Error => ScreenState.Error,
            _ => ScreenState.Ready,
        };
        if (!running && screen == ScreenState.Ready)
        {
            screen = ScreenState.Waiting;
        }

        string hero = screen switch
        {
            ScreenState.Waiting => "Waiting for Dead by Daylight",
            ScreenState.Scanning => "Reading the loading screen",
            ScreenState.Paused => "Scan paused",
            ScreenState.WaitingFocus => "Map found — waiting for you to click into the game",
            ScreenState.Active => "Preset active",
            ScreenState.Override => "Manual override",
            ScreenState.Error => "Something went wrong",
            _ => "Ready",
        };

        // the active preset and what follows it
        string? activeFile = string.IsNullOrWhiteSpace(active) ? null : Path.GetFileName(active);
        PresetTail tail = PresetTail.None;
        string? tailSubject = null;
        if (screen == ScreenState.Override && c.OverrideRealm is { } forced)
        {
            // "Forced for …" only once the forced file is on; until then what comes next, like waiting for focus
            string? target = c.OverridePresetPath;
            if (target is not null && SamePath(active, target))
            {
                tail = PresetTail.Override;
                tailSubject = forced;
            }
            else if (target is not null && c.HasPendingActivation)
            {
                tail = PresetTail.Pending;
                tailSubject = Path.GetFileName(target);
            }
        }
        else if (screen == ScreenState.WaitingFocus && detected?.RealmName is { } pendingRealm)
        {
            tail = PresetTail.Pending;
            IReadOnlyDictionary<string, string> presets = c.GetRealmPresets();
            string? next = presets.TryGetValue(pendingRealm, out string? realmPreset) && !string.IsNullOrWhiteSpace(realmPreset) ? realmPreset : global;
            tailSubject = string.IsNullOrWhiteSpace(next) ? null : Path.GetFileName(next);
            if (tailSubject is null)
            {
                tail = PresetTail.None;
            }
        }
        else if (activeFile is not null && c.IsActivePresetConfirmed)
        {
            tail = PresetTail.Confirmed;
        }

        // the last switch: "22:41:07" and the rest of the line
        string lastTime = string.Empty, lastText = "No preset switched yet.";
        if (c.LastActivation is { } last)
        {
            lastTime = last.At.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            string full = c.LastActivationText;
            lastText = " " + (full.StartsWith(lastTime, StringComparison.Ordinal) ? full[lastTime.Length..].TrimStart() : full);
        }

        // the plate: the map when one is known, else the campfire clearing
        bool detectionShown = detected is { Success: true } && !string.IsNullOrWhiteSpace(detected.RealmName) &&
            detection.StartsWith("Complete", StringComparison.Ordinal) &&
            state is AppState.Ready or AppState.RealmIdentified or AppState.WaitingForGameplay or AppState.PresetActive;
        PlateScene plate;
        PlateTone tone = PlateTone.Neutral;
        PlateTag tag = PlateTag.None;
        string? plateRealm = null;
        if (screen == ScreenState.Override && c.OverrideRealm is { } overrideRealm)
        {
            string? map = null;
            if (c.Database.TryGetRealm(overrideRealm, out RealmEntry? entry) && entry is not null && entry.Maps.Count > 0)
            {
                map = string.Equals(detected?.RealmName, entry.Name, StringComparison.OrdinalIgnoreCase) &&
                      detected?.MapName is { } detectedMap && entry.Maps.Contains(detectedMap, StringComparer.OrdinalIgnoreCase)
                    ? detectedMap
                    : entry.Maps[0];
            }

            plate = map is null
                ? PlateScene.Lobby(FireState.Burning, $"{overrideRealm} forced", "Automatic switching is off until you return to automatic.")
                : PlateScene.Map(entry!.Name, map);
            tone = PlateTone.Override;
            tag = PlateTag.Override;
        }
        else if (detectionShown)
        {
            // the map the scan read; a realm-only read (only the realm line was legible, or two of its maps were too
            // close to call) names no map, so the plate shows the campfire clearing with the realm instead of passing
            // one of its maps (and the backdrop) off as the detected one
            string realm = detected!.RealmName!;
            plateRealm = realm;
            plate = detected.MapName is { } map ? PlateScene.Map(realm, map) : PlateScene.Lobby(FireState.Burning, realm, RealmOnlyPlateLine);
            tone = screen switch
            {
                ScreenState.Active => PlateTone.Live,
                ScreenState.WaitingFocus => PlateTone.Waiting,
                _ => PlateTone.Neutral,
            };
            tag = screen == ScreenState.WaitingFocus ? PlateTag.WaitingForFocus : PlateTag.None;
        }
        else if (screen == ScreenState.Waiting)
        {
            plate = PlateScene.Lobby(FireState.Out, "No map detected yet", $"Start Dead by Daylight, then press {hotkey} on a loading screen.");
        }
        else if (screen == ScreenState.Scanning)
        {
            plate = PlateScene.Lobby(FireState.Burning, "Looking for the map name",
                globalActive ? "Global / Menu stays on until a realm is read." : "The current preset stays on until a realm is read.");
        }
        else if (screen == ScreenState.Paused)
        {
            plate = PlateScene.Lobby(FireState.Embers, "Scan paused", "Switch back to Dead by Daylight to resume.");
            tag = PlateTag.WaitingForFocus;
        }
        else if (screen == ScreenState.Error)
        {
            plate = PlateScene.Lobby(FireState.Embers, "Something went wrong", $"Press {hotkey} on the loading screen to try again.");
        }
        else
        {
            plate = PlateScene.Lobby(FireState.Burning, "No map detected yet",
                globalActive ? $"Global / Menu is on. Press {hotkey} on the loading screen." : $"Press {hotkey} on the loading screen.");
        }

        return new StatusScreen
        {
            State = screen,
            Hero = hero,
            HeroSize = StatusScreen.SizeFor(hero),
            Message = status,
            ActiveFile = activeFile,
            Tail = tail,
            TailSubject = tailSubject,
            Detection = detection,
            DetectionDot = screen switch
            {
                ScreenState.Scanning => DetectionDot.Ember,
                ScreenState.Paused => DetectionDot.Ring,
                _ => DetectionDot.None,
            },
            LastSwitchTime = lastTime,
            LastSwitchText = lastText,
            Plate = plate,
            Tone = tone,
            Tag = tag,
            SkillCheck = screen switch
            {
                ScreenState.Scanning => SkillCheckState.Scanning,
                ScreenState.Paused => SkillCheckState.Paused,
                _ => SkillCheckState.Hidden,
            },
            HotkeyText = hotkey,
            EmberLevel = StatusScreen.EmberFor(screen),
            Leaning = screen is ScreenState.Scanning or ScreenState.WaitingFocus,
            BackdropMap = plate.IsMap ? plate.MapName : null,
            DetectedRealm = plateRealm,
        };
    }
}

using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using DBDAutoReShade.App.Infrastructure;
using DBDAutoReShade.App.Services;
using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.App.ViewModels;

/// <summary>The Presets screen: the filterable realm list (Global / Menu pinned) and the selected row's detail.</summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<string, int> _heroIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PresetFeedback> _feedback = new(StringComparer.OrdinalIgnoreCase);
    private PresetRowBase? _selectedRow;
    private string _filterText = string.Empty;
    private bool _hasFilterMatches = true;
    private string _globalPresetName = "Not configured";
    private DetectionResult? _seenDetection;
    private string? _thumbsRealm;
    private string? _shownPresetsBackdrop;
    private int _testPresses;

    // the detail (the selected row)
    private string _detailTitle = string.Empty;
    private string _detailCountText = string.Empty;
    private RowMark _detailTagKind;
    private string _detailTagText = string.Empty;
    private string? _heroMap;
    private bool _heroIsDetected;
    private bool _detailIsLive;
    private bool _detailHasPreset;
    private string _detailPresetText = string.Empty;
    private string _detailShortcutPrefix = string.Empty;
    private string _detailShortcutValue = string.Empty;
    private bool _detailNeedsRestart;
    private string _detailFlagText = string.Empty;
    private int _thumbColumns = 1;
    private bool _globalFireBurning;
    private PresetFeedback _currentFeedback = PresetFeedback.None;
    private RelayCommand _testSelectedCommand = null!;
    private RelayCommand _clearSelectedCommand = null!;

    /// <summary>The realm rows (database order); rebuilt only when the realm list changes.</summary>
    public ObservableCollection<RealmRow> RealmRows { get; }

    /// <summary>The pinned Global / Menu row.</summary>
    public GlobalPresetRow GlobalRow { get; } = new();

    /// <summary>The Global preset's file name, or "Not configured" (the setup wizard shows it).</summary>
    public string GlobalPresetName { get => _globalPresetName; private set => SetProperty(ref _globalPresetName, value); }

    /// <summary>The selected row (its detail is shown on the right).</summary>
    public PresetRowBase? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (value is not null)
            {
                SelectRow(value);
            }
        }
    }

    /// <summary>The selected realm row (null while Global / Menu is selected); the realm list's selection.</summary>
    public RealmRow? SelectedRealmRow
    {
        get => _selectedRow as RealmRow;
        set
        {
            if (value is not null)
            {
                SelectRow(value);
            }
        }
    }

    /// <summary>"Filter by realm or map": rows that match neither their name nor a map's name are hidden.</summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>At least one realm matches the filter.</summary>
    public bool HasFilterMatches { get => _hasFilterMatches; private set => SetProperty(ref _hasFilterMatches, value); }

    /// <summary>"No realm or map matches “x”."</summary>
    public string FilterEmptyText => $"No realm or map matches “{FilterText.Trim()}”.";

    // ---- the detail ----

    /// <summary>The selected realm, or "Global / Menu".</summary>
    public string DetailTitle { get => _detailTitle; private set => SetProperty(ref _detailTitle, value); }

    /// <summary>Global / Menu is selected.</summary>
    public bool DetailIsGlobal => _selectedRow is GlobalPresetRow;

    /// <summary>"5 maps" (empty for Global).</summary>
    public string DetailCountText { get => _detailCountText; private set => SetProperty(ref _detailCountText, value); }

    /// <summary>The detail tag's dot (none: no tag).</summary>
    public RowMark DetailTagKind { get => _detailTagKind; private set => SetProperty(ref _detailTagKind, value); }

    /// <summary>"Active realm", "Detected, waiting for game focus" or "Active now".</summary>
    public string DetailTagText { get => _detailTagText; private set => SetProperty(ref _detailTagText, value); }

    /// <summary>What Global / Menu is used for.</summary>
    public string GlobalExplain => "Used in menus, for realms without a preset, after an unsure scan and when a match ends.";

    /// <summary>The hero picture's map (null for Global / Menu: the campfire clearing).</summary>
    public string? HeroMap { get => _heroMap; private set => SetProperty(ref _heroMap, value); }

    /// <summary>The hero picture is the detected map.</summary>
    public bool HeroIsDetected { get => _heroIsDetected; private set => SetProperty(ref _heroIsDetected, value); }

    /// <summary>The selected realm (or Global) is live: the hero frame glows ember.</summary>
    public bool DetailIsLive { get => _detailIsLive; private set => SetProperty(ref _detailIsLive, value); }

    /// <summary>The campfire behind Global / Menu burns (DBD runs) or is out.</summary>
    public bool GlobalFireBurning { get => _globalFireBurning; private set => SetProperty(ref _globalFireBurning, value); }

    /// <summary>The selected realm's maps under the hero picture (two or more).</summary>
    public ObservableCollection<MapThumb> Thumbs { get; } = new();

    /// <summary>Columns of the thumbnail grid (one per map).</summary>
    public int ThumbColumns { get => _thumbColumns; private set => SetProperty(ref _thumbColumns, value); }

    /// <summary>A two-map realm: compact picture + name cards.</summary>
    public bool ThumbsArePair => Thumbs.Count == 2;

    /// <summary>The selected realm has more than one map (thumbnails are shown).</summary>
    public bool HasThumbs => Thumbs.Count > 0;

    /// <summary>The selected row has a preset (Test and Clear are enabled; the file button says "Change file").</summary>
    public bool DetailHasPreset
    {
        get => _detailHasPreset;
        private set
        {
            if (SetProperty(ref _detailHasPreset, value))
            {
                RaisePropertyChanged(nameof(ChangeFileText));
                _testSelectedCommand.RaiseCanExecuteChanged();
                _clearSelectedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>The preset's file name, or what happens without one.</summary>
    public string DetailPresetText { get => _detailPresetText; private set => SetProperty(ref _detailPresetText, value); }

    /// <summary>"Shortcut", or "No shortcut".</summary>
    public string DetailShortcutPrefix { get => _detailShortcutPrefix; private set => SetProperty(ref _detailShortcutPrefix, value); }

    /// <summary>The shortcut (bold), e.g. "Ctrl+Alt+Shift+F23", or "assigned, not loaded yet".</summary>
    public string DetailShortcutValue { get => _detailShortcutValue; private set => SetProperty(ref _detailShortcutValue, value); }

    /// <summary>The selected preset's shortcut needs a DBD restart (the red flag says so).</summary>
    public bool DetailNeedsRestart { get => _detailNeedsRestart; private set => SetProperty(ref _detailNeedsRestart, value); }

    /// <summary>
    /// The red flag under the preset panel: what fixes the selected preset's blocked shortcut ("Restart Dead by Daylight
    /// to load this shortcut", or first the right ReShade.ini); empty when nothing is blocked (no flag).
    /// </summary>
    public string DetailFlagText { get => _detailFlagText; private set => SetProperty(ref _detailFlagText, value); }

    /// <summary>The flag when a DBD restart loads the shortcut.</summary>
    public const string RestartFlag = "Restart Dead by Daylight to load this shortcut";

    /// <summary>
    /// The flag when ReShade.ini is not set: ReShade can't load the shortcut, and a DBD restart alone can't help (once
    /// it is set, the flag asks for the restart).
    /// </summary>
    public const string ReshadeIniNotSetFlag = "ReShade.ini is not set: set it under Settings first";

    /// <summary>The flag when the configured ReShade.ini was not found (see <see cref="ReshadeIniNotSetFlag"/>).</summary>
    public const string ReshadeIniNotFoundFlag = "ReShade.ini was not found: set the right one under Settings first";

    /// <summary>"Change file" or "Choose file".</summary>
    public string ChangeFileText => DetailHasPreset ? "Change file" : "Choose file";

    /// <summary>
    /// The preset panel's feedback for the selected row. A Test countdown is a new value for every press of Test and
    /// whenever its row is shown again, with the time already spent in the controller's real focus wait.
    /// </summary>
    public PresetFeedback Feedback { get => _currentFeedback; private set => SetProperty(ref _currentFeedback, value); }

    /// <summary>The map behind the fog on the Presets tab: the selected realm's hero map.</summary>
    public string? PresetsBackdropMap => _selectedRow is RealmRow ? HeroMap : null;

    /// <summary>Selects a row (parameter: a <see cref="PresetRowBase"/>).</summary>
    public ICommand SelectRowCommand { get; private set; } = null!;

    /// <summary>Tests the selected preset (waits up to 15 s for DBD focus).</summary>
    public ICommand TestSelectedCommand => _testSelectedCommand;

    /// <summary>Picks a preset file for the selected row.</summary>
    public ICommand ChangeSelectedCommand { get; private set; } = null!;

    /// <summary>Asks to clear the selected row's preset.</summary>
    public ICommand ClearSelectedCommand => _clearSelectedCommand;

    /// <summary>Clears it (after <see cref="ClearSelectedCommand"/>).</summary>
    public ICommand ConfirmClearCommand { get; private set; } = null!;

    /// <summary>Closes the feedback (Keep it).</summary>
    public ICommand CloseFeedbackCommand { get; private set; } = null!;

    /// <summary>Empties the filter.</summary>
    public ICommand ClearFilterCommand { get; private set; } = null!;

    /// <summary>Picks the Global preset file.</summary>
    public ICommand ChangeGlobalCommand { get; private set; } = null!;

    // ---- actions ----

    /// <summary>Selects <paramref name="row"/> and shows its detail.</summary>
    public void SelectRow(PresetRowBase row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (ReferenceEquals(_selectedRow, row))
        {
            return;
        }

        if (_selectedRow is not null)
        {
            _selectedRow.IsSelected = false;
        }

        _selectedRow = row;
        row.IsSelected = true;
        RaisePropertyChanged(nameof(SelectedRow));
        RaisePropertyChanged(nameof(SelectedRealmRow));
        RaisePropertyChanged(nameof(DetailIsGlobal));
        UpdateDetail();
    }

    /// <summary>Shows map <paramref name="index"/> of the selected realm as the hero picture.</summary>
    public void SelectHero(int index)
    {
        if (_selectedRow is not RealmRow row || index < 0 || index >= row.MapNames.Count)
        {
            return;
        }

        _heroIndex[row.RealmName] = index;
        UpdateDetail();
    }

    /// <summary>Tests the Global preset (waits for DBD focus); only an immediate failure is shown.</summary>
    public void TestGlobal()
    {
        int pressesBefore = _testPresses;
        string? error = _controller.TestGlobalPreset();
        if (error is not null)
        {
            ShowWarning(error);
            return;
        }

        UpdateTestFeedback(started: AppConfigKeys.GlobalShortcutKey, pressesBefore);
    }

    /// <summary>Tests a realm's preset (waits for DBD focus); only an immediate failure is shown.</summary>
    public void TestRealm(string realm)
    {
        int pressesBefore = _testPresses;
        string? error = _controller.TestRealmPreset(realm);
        if (error is not null)
        {
            ShowWarning(error);
            return;
        }

        UpdateTestFeedback(started: realm, pressesBefore);
    }

    /// <summary>Picks the Global preset file (a file dialog).</summary>
    public void ChangeGlobal()
    {
        string? file = PickPresetFile();
        if (file is not null)
        {
            AssignGlobalPreset(file);
        }
    }

    /// <summary>Picks a preset file for <paramref name="realm"/> (a file dialog).</summary>
    public void SelectRealmPreset(string realm)
    {
        string? file = PickPresetFile();
        if (file is not null)
        {
            AssignRealmPreset(realm, file);
        }
    }

    /// <summary>Assigns <paramref name="file"/> to <paramref name="realm"/>; a refusal is shown as a warning.</summary>
    public void AssignRealmPreset(string realm, string file)
    {
        string? error = _controller.SetRealmPreset(realm, file);
        if (error is not null)
        {
            ShowWarning(error);
            return;
        }

        SetFeedback(new PresetFeedback(FeedbackKind.Message, realm, _controller.StatusMessage));
    }

    /// <summary>Makes <paramref name="file"/> the Global preset; a refusal is shown as a warning.</summary>
    public void AssignGlobalPreset(string file)
    {
        string? error = _controller.SetGlobalPreset(file);
        if (error is not null)
        {
            ShowWarning(error);
            return;
        }

        SetFeedback(new PresetFeedback(FeedbackKind.Message, AppConfigKeys.GlobalShortcutKey, _controller.StatusMessage));
    }

    /// <summary>Clears <paramref name="realm"/>'s preset at once.</summary>
    public void ClearRealmPreset(string realm)
    {
        string? error = _controller.SetRealmPreset(realm, null);
        if (error is not null)
        {
            ShowWarning(error);
            return;
        }

        SetFeedback(new PresetFeedback(FeedbackKind.Message, realm, _controller.StatusMessage));
    }

    /// <summary>Clears the Global preset at once.</summary>
    public void ClearGlobalPreset()
    {
        string? error = _controller.SetGlobalPreset(null);
        if (error is not null)
        {
            ShowWarning(error);
            return;
        }

        SetFeedback(new PresetFeedback(FeedbackKind.Message, AppConfigKeys.GlobalShortcutKey, _controller.StatusMessage));
    }

    /// <summary>Asks whether to clear the selected row's preset (the panel shows Clear preset / Keep it).</summary>
    public void AskClearSelected()
    {
        if (_selectedRow is null)
        {
            return;
        }

        bool hasGlobal = !string.IsNullOrWhiteSpace(_controller.Config.GlobalPresetPath);
        string question = _selectedRow switch
        {
            GlobalPresetRow => "Clear the Global / Menu preset? Menus keep whatever preset ReShade has open.",
            RealmRow realm when hasGlobal => $"Clear the preset for {realm.RealmName}? This realm will use Global / Menu.",
            RealmRow realm => $"Clear the preset for {realm.RealmName}? No Global preset is set, so detecting it then switches nothing.",
            _ => string.Empty,
        };
        SetFeedback(new PresetFeedback(FeedbackKind.ClearConfirm, _selectedRow.Key, question));
    }

    private void InitializePresets()
    {
        SelectRowCommand = new RelayCommand(p =>
        {
            if (p is PresetRowBase row)
            {
                SelectRow(row);
            }
        });
        _testSelectedCommand = new RelayCommand(() => TestSelected(), () => DetailHasPreset);
        ChangeSelectedCommand = new RelayCommand(() => ChangeSelected());
        _clearSelectedCommand = new RelayCommand(() => AskClearSelected(), () => DetailHasPreset);
        ConfirmClearCommand = new RelayCommand(() => ConfirmClear());
        CloseFeedbackCommand = new RelayCommand(() =>
        {
            if (_selectedRow is not null)
            {
                SetFeedback(PresetFeedback.None with { Key = _selectedRow.Key });
            }
        });
        ClearFilterCommand = new RelayCommand(() => FilterText = string.Empty);
        ChangeGlobalCommand = new RelayCommand(() => ChangeGlobal());
    }

    private void TestSelected()
    {
        switch (_selectedRow)
        {
            case GlobalPresetRow:
                TestGlobal();
                break;
            case RealmRow realm:
                TestRealm(realm.RealmName);
                break;
        }
    }

    private void ChangeSelected()
    {
        switch (_selectedRow)
        {
            case GlobalPresetRow:
                ChangeGlobal();
                break;
            case RealmRow realm:
                SelectRealmPreset(realm.RealmName);
                break;
        }
    }

    private void ConfirmClear()
    {
        switch (_selectedRow)
        {
            case GlobalPresetRow:
                ClearGlobalPreset();
                break;
            case RealmRow realm:
                ClearRealmPreset(realm.RealmName);
                break;
        }
    }

    private void SetFeedback(PresetFeedback feedback)
    {
        if (feedback.Kind == FeedbackKind.None)
        {
            _feedback.Remove(feedback.Key);
        }
        else
        {
            _feedback[feedback.Key] = feedback;
        }

        ShowFeedback();
    }

    /// <summary>
    /// Shows the selected row's feedback. A Test countdown already shown for the same press stays as it is (its ring
    /// runs); otherwise it is shown with the time the controller's focus wait has really run, so the ring starts there.
    /// </summary>
    private void ShowFeedback()
    {
        PresetFeedback next = _selectedRow is not null && _feedback.TryGetValue(_selectedRow.Key, out PresetFeedback? stored) ? stored : PresetFeedback.None;
        if (next.Kind == FeedbackKind.Test)
        {
            if (Feedback is { Kind: FeedbackKind.Test } shown && shown.Press == next.Press &&
                string.Equals(shown.Key, next.Key, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            PendingTest? pending = _controller.PendingTest;
            if (pending is not null && string.Equals(pending.Name, next.Key, StringComparison.OrdinalIgnoreCase))
            {
                next = next with { Elapsed = ElapsedOf(pending) };
            }
        }

        Feedback = next;
    }

    private static TimeSpan ElapsedOf(PendingTest pending)
    {
        TimeSpan elapsed = AppController.TestFocusWindow - pending.Remaining;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    /// <summary>
    /// Follows the controller's pending Test: while it waits for DBD focus its row shows the countdown ring (started
    /// <c>Elapsed</c> into the 15 s wait); once it was sent, failed or gave up, the row shows that Test's own outcome
    /// (<see cref="AppController.LastTestOutcome"/>), and once something else took over (a scan, another switch, DBD
    /// closing) it says the Test was cancelled: the status line then tells that other action, never this Test.
    /// <paramref name="started"/>: Test was just pressed for that row (a new press: the controller's wait restarted);
    /// <paramref name="pressesBefore"/>: the press count before it, so a ring the controller's own refresh already
    /// started for this press (during the call) is kept: one restart per press.
    /// </summary>
    private void UpdateTestFeedback(string? started = null, int pressesBefore = int.MaxValue)
    {
        PendingTest? pending = _controller.PendingTest;
        TestOutcome? outcome = _controller.LastTestOutcome;

        // a test that no longer waits shows how it ended
        foreach (KeyValuePair<string, PresetFeedback> shown in _feedback.Where(f => f.Value.Kind == FeedbackKind.Test).ToList())
        {
            bool stillPending = pending is not null && pending.Serial == shown.Value.Serial &&
                string.Equals(pending.Name, shown.Key, StringComparison.OrdinalIgnoreCase);
            if (!stillPending)
            {
                string text = outcome is not null && outcome.Serial == shown.Value.Serial ? DescribeTestOutcome(outcome.Message) : TestSupersededText;
                _feedback[shown.Key] = new PresetFeedback(FeedbackKind.Message, shown.Key, text);
            }
        }

        if (started is not null)
        {
            if (pending is not null && string.Equals(pending.Name, started, StringComparison.OrdinalIgnoreCase))
            {
                if (!(_feedback.TryGetValue(started, out PresetFeedback? begun) && begun.Kind == FeedbackKind.Test &&
                      begun.Serial == pending.Serial && begun.Press > pressesBefore))
                {
                    _feedback[started] = TestFeedback(pending);
                }
            }
            else
            {
                // sent at once, or failed for good: this Test's outcome (the status line said it a moment ago)
                string text = outcome is not null && string.Equals(outcome.Name, started, StringComparison.OrdinalIgnoreCase)
                    ? DescribeTestOutcome(outcome.Message)
                    : _controller.StatusMessage;
                _feedback[started] = new PresetFeedback(FeedbackKind.Message, started, text);
            }
        }
        else if (pending is not null && !(_feedback.TryGetValue(pending.Name, out PresetFeedback? shown) && shown.Kind == FeedbackKind.Test && shown.Serial == pending.Serial))
        {
            _feedback[pending.Name] = TestFeedback(pending);
        }

        ShowFeedback();
    }

    /// <summary>What a Test's row says when something else took over before DBD had focus.</summary>
    public const string TestSupersededText =
        "Test cancelled: something else took over first (a scan, another switch or Dead by Daylight closing). Press Test to try again.";

    private PresetFeedback TestFeedback(PendingTest pending) =>
        new(FeedbackKind.Test, pending.Name, $"Click into Dead by Daylight to test {pending.PresetName}…",
            pending.PresetName, ElapsedOf(pending), ++_testPresses, pending.Serial);

    private static string DescribeTestOutcome(string message) =>
        message.Contains("wasn't focused within", StringComparison.Ordinal)
            ? $"No click into the game within {(int)AppController.TestFocusWindow.TotalSeconds} s, so nothing switched. Press Test to try again."
            : message;

    private PresetRowBase? FindRowByDisplayName(string name)
    {
        if (string.Equals(name, AppConfigKeys.DisplayName(AppConfigKeys.GlobalShortcutKey), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, GlobalPresetRow.Label, StringComparison.OrdinalIgnoreCase))
        {
            return GlobalRow;
        }

        return RealmRows.FirstOrDefault(r => string.Equals(r.RealmName, name, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshPresets(BlockedPresetShortcuts blockedShortcuts)
    {
        var c = _controller;
        GlobalPresetName = string.IsNullOrWhiteSpace(c.Config.GlobalPresetPath) ? "Not configured" : Path.GetFileName(c.Config.GlobalPresetPath);

        IReadOnlyList<RealmEntry> entries = c.Database.Realms;
        List<string> realms = entries.Select(r => r.Name).ToList();
        if (!realms.SequenceEqual(AvailableRealms, StringComparer.Ordinal))
        {
            AvailableRealms = realms;
            OverrideChoices = new List<string>(realms.Count + 1) { GlobalPresetRow.Label };
            OverrideChoices.AddRange(realms);
            RaisePropertyChanged(nameof(AvailableRealms));
            RaisePropertyChanged(nameof(OverrideChoices));
        }

        IReadOnlyDictionary<string, string> presets = c.GetRealmPresets();
        bool hasGlobal = !string.IsNullOrWhiteSpace(c.Config.GlobalPresetPath);
        if (!RealmRows.Select(r => r.RealmName).SequenceEqual(realms, StringComparer.Ordinal))
        {
            string? selectedKey = _selectedRow?.Key;
            _thumbsRealm = null; // (the realm shown may have other maps now)
            RealmRows.Clear();
            foreach (RealmEntry entry in entries)
            {
                presets.TryGetValue(entry.Name, out string? preset);
                RealmRows.Add(new RealmRow(this, entry.Name, preset, hasGlobal, entry.Maps.ToList()));
            }

            if (_selectedRow is RealmRow)
            {
                _selectedRow = null;
                PresetRowBase? again = selectedKey is null ? null : RealmRows.FirstOrDefault(r => string.Equals(r.Key, selectedKey, StringComparison.OrdinalIgnoreCase));
                if (again is not null)
                {
                    SelectRow(again);
                }
            }

            ApplyFilter();
        }
        else
        {
            // the same realms: their rows stay; a database update may still have added maps to a realm
            bool mapsChanged = false;
            for (int i = 0; i < RealmRows.Count; i++)
            {
                RealmRow row = RealmRows[i];
                presets.TryGetValue(row.RealmName, out string? preset);
                row.Update(preset, hasGlobal);
                if (row.UpdateMaps(entries[i].Maps))
                {
                    mapsChanged = true;
                    if (string.Equals(_thumbsRealm, row.RealmName, StringComparison.Ordinal))
                    {
                        _thumbsRealm = null; // its thumbnails are built again
                    }
                }
            }

            if (mapsChanged)
            {
                ApplyFilter(); // the map counts, and a filter that may now match a new map
            }
        }

        GlobalRow.Update(c.Config.GlobalPresetPath);

        // marks: blocked (red: a DBD restart is needed, or first the right ReShade.ini; the controller tells the two
        // apart and so do the rows), the active realm (ember), the detected realm waiting for focus (ring)
        var needRestart = new HashSet<string>(blockedShortcuts.NeedingDbdRestart, StringComparer.OrdinalIgnoreCase);
        var needIni = new HashSet<string>(blockedShortcuts.WaitingForReshadeIni, StringComparer.OrdinalIgnoreCase);
        AppState state = c.States.State;
        string? detectedRealm = c.LastDetection is { Success: true } d ? d.RealmName : null;
        foreach (RealmRow row in RealmRows)
        {
            row.NeedsRestart = row.HasPreset && needRestart.Contains(row.RealmName);
            row.NeedsReshadeIni = row.HasPreset && needIni.Contains(row.RealmName);
            row.Mark = row.IsBlocked ? RowMark.Red
                : state == AppState.PresetActive && string.Equals(c.ActiveRealm, row.RealmName, StringComparison.OrdinalIgnoreCase) ? RowMark.Ember
                : state is AppState.WaitingForGameplay or AppState.RealmIdentified && string.Equals(detectedRealm, row.RealmName, StringComparison.OrdinalIgnoreCase) ? RowMark.Ring
                : RowMark.None;
        }

        string globalName = AppConfigKeys.DisplayName(AppConfigKeys.GlobalShortcutKey);
        GlobalRow.NeedsRestart = !GlobalRow.IsUnset && needRestart.Contains(globalName);
        GlobalRow.NeedsReshadeIni = !GlobalRow.IsUnset && needIni.Contains(globalName);
        GlobalRow.Mark = GlobalRow.IsBlocked ? RowMark.Red
            : state != AppState.PresetActive && c.ActiveRealm is null && SamePath(c.ActivePresetPath, c.Config.GlobalPresetPath) ? RowMark.Ember
            : RowMark.None;

        // a new detection selects its realm and shows its map
        if (c.LastDetection is { Success: true } detection && !ReferenceEquals(detection, _seenDetection) && detection.RealmName is { } found)
        {
            _seenDetection = detection;
            RealmRow? row = RealmRows.FirstOrDefault(r => string.Equals(r.RealmName, found, StringComparison.OrdinalIgnoreCase));
            if (row is not null)
            {
                int index = detection.MapName is null ? 0 : Math.Max(0, IndexOfMap(row, detection.MapName));
                _heroIndex[row.RealmName] = index;
                SelectRow(row);
            }
        }

        if (_selectedRow is null)
        {
            PresetRowBase? first = detectedRealm is null ? null : RealmRows.FirstOrDefault(r => string.Equals(r.RealmName, detectedRealm, StringComparison.OrdinalIgnoreCase));
            first ??= RealmRows.FirstOrDefault(r => r.IsBlocked);
            first ??= RealmRows.FirstOrDefault();
            if (first is not null)
            {
                SelectRow(first);
            }
        }

        UpdateTestFeedback();
        UpdateDetail();
    }

    private static int IndexOfMap(RealmRow row, string map)
    {
        for (int i = 0; i < row.MapNames.Count; i++)
        {
            if (string.Equals(row.MapNames[i], map, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private void ApplyFilter()
    {
        string q = Normalize(FilterText.Trim());
        int visible = 0;
        foreach (RealmRow row in RealmRows)
        {
            if (q.Length == 0)
            {
                row.IsVisible = true;
                row.IsMetaHit = false;
                row.MetaText = RealmRow.MapCountText(row.MapNames.Count);
                visible++;
                continue;
            }

            bool nameHit = Normalize(row.RealmName).Contains(q, StringComparison.Ordinal);
            string? mapHit = nameHit ? null : row.MapNames.FirstOrDefault(m => Normalize(m).Contains(q, StringComparison.Ordinal));
            row.IsVisible = nameHit || mapHit is not null;
            row.IsMetaHit = mapHit is not null;
            row.MetaText = mapHit is not null ? $"Map: {mapHit}" : RealmRow.MapCountText(row.MapNames.Count);
            if (row.IsVisible)
            {
                visible++;
            }
        }

        HasFilterMatches = visible > 0;
        RaisePropertyChanged(nameof(FilterEmptyText));
    }

    /// <summary>Case- and accent-insensitive text for the filter ("Léry's" matches "lerys").</summary>
    private static string Normalize(string text)
    {
        string decomposed = text.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decomposed.Length);
        foreach (char ch in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark || ch is '\'' or '’')
            {
                continue;
            }

            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    private void UpdateDetail()
    {
        var c = _controller;
        PresetRowBase? row = _selectedRow;
        AppState state = c.States.State;
        string? detectedRealm = c.LastDetection is { Success: true } d ? d.RealmName : null;
        string? detectedMap = c.LastDetection is { Success: true } dm ? dm.MapName : null;
        if (row is null)
        {
            DetailTitle = string.Empty;
            return;
        }

        DetailTitle = row.DisplayName;
        if (row is RealmRow realm)
        {
            int count = realm.MapNames.Count;
            DetailCountText = RealmRow.MapCountText(count);
            bool live = state == AppState.PresetActive && string.Equals(c.ActiveRealm, realm.RealmName, StringComparison.OrdinalIgnoreCase);
            bool isDetected = string.Equals(detectedRealm, realm.RealmName, StringComparison.OrdinalIgnoreCase);
            bool waiting = isDetected && state is AppState.WaitingForGameplay or AppState.RealmIdentified;
            DetailIsLive = live;
            (DetailTagKind, DetailTagText) = live ? (RowMark.Ember, "Active realm")
                : waiting ? (RowMark.Ring, "Detected, waiting for game focus")
                : (RowMark.None, string.Empty);

            if (!_heroIndex.TryGetValue(realm.RealmName, out int hero))
            {
                hero = isDetected && detectedMap is not null ? Math.Max(0, IndexOfMap(realm, detectedMap)) : 0;
            }

            hero = count == 0 ? 0 : Math.Clamp(hero, 0, count - 1);
            HeroMap = count == 0 ? null : realm.MapNames[hero];
            HeroIsDetected = isDetected && HeroMap is not null && string.Equals(HeroMap, detectedMap, StringComparison.OrdinalIgnoreCase);

            // thumbnails: rebuilt only when another realm is shown
            if (!string.Equals(_thumbsRealm, realm.RealmName, StringComparison.Ordinal))
            {
                _thumbsRealm = realm.RealmName;
                Thumbs.Clear();
                if (count > 1)
                {
                    for (int i = 0; i < count; i++)
                    {
                        Thumbs.Add(new MapThumb(realm.MapNames[i], i, SelectHero));
                    }
                }

                ThumbColumns = Math.Max(1, Thumbs.Count);
                RaisePropertyChanged(nameof(ThumbsArePair));
                RaisePropertyChanged(nameof(HasThumbs));
            }

            foreach (MapThumb thumb in Thumbs)
            {
                thumb.IsSelected = thumb.Index == hero;
                thumb.IsDetected = isDetected && string.Equals(thumb.Name, detectedMap, StringComparison.OrdinalIgnoreCase);
            }

            DetailHasPreset = realm.HasPreset;
            bool hasGlobal = !string.IsNullOrWhiteSpace(c.Config.GlobalPresetPath);
            DetailPresetText = realm.HasPreset ? realm.PresetName
                : hasGlobal ? "No preset — uses Global / Menu" : "No preset — no Global preset is set, so nothing switches";
            SetBlockedFlag(realm);
            SetShortcutLine(realm.HasPreset, realm.IsBlocked, realm.RealmName);
        }
        else
        {
            DetailCountText = string.Empty;
            bool live = GlobalRow.Mark == RowMark.Ember;
            DetailIsLive = live;
            (DetailTagKind, DetailTagText) = live ? (RowMark.Ember, "Active now") : (RowMark.None, string.Empty);
            HeroMap = null;
            HeroIsDetected = false;
            GlobalFireBurning = c.DbdRunning;
            if (_thumbsRealm is not null)
            {
                _thumbsRealm = null;
                Thumbs.Clear();
                ThumbColumns = 1;
                RaisePropertyChanged(nameof(ThumbsArePair));
                RaisePropertyChanged(nameof(HasThumbs));
            }

            DetailHasPreset = !GlobalRow.IsUnset;
            DetailPresetText = GlobalRow.IsUnset ? "No preset" : GlobalRow.PresetText;
            SetBlockedFlag(GlobalRow);
            SetShortcutLine(!GlobalRow.IsUnset, GlobalRow.IsBlocked, AppConfigKeys.GlobalShortcutKey);
        }

        // raised only on a change: this runs on every controller event
        string? backdrop = PresetsBackdropMap;
        if (!string.Equals(backdrop, _shownPresetsBackdrop, StringComparison.Ordinal))
        {
            _shownPresetsBackdrop = backdrop;
            RaisePropertyChanged(nameof(PresetsBackdropMap));
            RaisePropertyChanged(nameof(BackdropMap));
        }

        ShowFeedback();
    }

    /// <summary>
    /// The red flag under the preset panel says what fixes the selected row's blocked shortcut, as the controller tells
    /// it: a DBD restart, or (ReShade.ini not set or not found) first setting the right ReShade.ini, which a restart
    /// alone can't fix.
    /// </summary>
    private void SetBlockedFlag(PresetRowBase row)
    {
        DetailNeedsRestart = row.NeedsRestart;
        DetailFlagText = row.NeedsReshadeIni
            ? string.IsNullOrWhiteSpace(_controller.Config.ReshadeIniPath)
                ? ReshadeIniNotSetFlag
                : ReshadeIniNotFoundFlag
            : row.NeedsRestart ? RestartFlag : string.Empty;
    }

    private void SetShortcutLine(bool hasPreset, bool needsRestart, string name)
    {
        if (!hasPreset)
        {
            DetailShortcutPrefix = "No shortcut";
            DetailShortcutValue = string.Empty;
            return;
        }

        string? shortcut = null;
        try
        {
            if (_controller.Config.ReshadeShortcuts.TryGetValue(name, out ReshadeShortcutConfig? config) && config is not null)
            {
                shortcut = ReshadeConfigurationService.FormatShortcut(config);
            }
        }
        catch (Exception)
        {
            // the controller is changing the shortcuts right now; the next refresh shows them
        }

        DetailShortcutPrefix = "Shortcut";
        DetailShortcutValue = needsRestart || shortcut is null ? "assigned, not loaded yet" : shortcut;
    }
}

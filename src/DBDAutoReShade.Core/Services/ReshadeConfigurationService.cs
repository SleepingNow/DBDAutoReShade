using System.Text;
using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Core.Services;

/// <summary>One managed preset shortcut: realm → (preset file, key combo).</summary>
public sealed record PresetShortcutAssignment(string Realm, string PresetPath, ReshadeShortcutConfig Shortcut);

/// <summary>
/// A managed assignment that <see cref="ReshadeConfigurationService.SyncPresetShortcuts"/> did NOT
/// write, and why. Typically a foreign (user-owned) ReShade.ini entry already uses the same combo
/// (legacy state); the foreign entry is kept and the managed one is skipped so ReShade.ini never
/// holds two entries with the same combo.
/// </summary>
/// <param name="Name">The assignment's name (realm or <see cref="AppConfigKeys.GlobalShortcutKey"/>).</param>
/// <param name="PresetPath">The preset file that was not written.</param>
/// <param name="Shortcut">The combo that was not written.</param>
/// <param name="ConflictingPresetPath">The preset of the entry already using the combo, if that is the reason.</param>
/// <param name="Message">User-facing explanation.</param>
public sealed record ShortcutConflict(
    string Name,
    string PresetPath,
    ReshadeShortcutConfig Shortcut,
    string? ConflictingPresetPath,
    string Message);

public sealed record ShortcutSyncResult(bool Changed, int Added, int Removed, int PreservedForeign)
{
    /// <summary>Managed assignments that were not written (see <see cref="ShortcutConflict"/>). Empty when all were written.</summary>
    public IReadOnlyList<ShortcutConflict> Conflicts { get; init; } = Array.Empty<ShortcutConflict>();

    public bool HasConflicts => Conflicts.Count > 0;
}

/// <summary>
/// One shortcut entry as ReShade loads it from ReShade.ini: the key combo and
/// the preset it switches to (normalized absolute path; relative paths are
/// resolved against the ini's folder).
/// </summary>
public sealed record IniShortcutEntry(ReshadeShortcutConfig Shortcut, string PresetPath);

/// <summary>
/// Manages this app's preset shortcuts inside ReShade.ini.
///
/// Verified against the ReShade source (runtime.cpp) and official docs:
/// <c>[GENERAL] PresetPath</c> holds the current preset, while
/// <c>PresetShortcutKeys</c> (flat int list, 4 per shortcut: key,ctrl,shift,alt)
/// plus <c>PresetShortcutPaths</c> define per-preset shortcut switches; path i
/// pairs with key quad i. Both are ReShade ini lists (ini_file.cpp): a single
/// ',' separates elements and ",," is an escaped ',' inside an element; they
/// are read and written exactly that way. Pressing a shortcut switches presets in-memory;
/// ReShade does NOT poll ReShade.ini for external edits, so activation is done
/// by simulating the managed shortcut (see Desktop KeySimulator), not by
/// rewriting PresetPath. ReShade loads the shortcut lists only at startup.
///
/// An entry belongs to this app only when it is one of the app's pool combos
/// (Ctrl+Alt+Shift + F13–F24/0–9, the only combos it ever writes) AND points
/// at a preset the app manages or managed (see <see cref="IsAppOwned"/>).
/// Every other entry is foreign (the user's own) and is preserved, even when it
/// points at an assigned preset file: ReShade allows several shortcuts for one
/// preset. Malformed entries and unpaired trailing tokens are preserved as
/// well — always at positions that keep every other path paired with its own
/// key quad.
/// </summary>
public sealed class ReshadeConfigurationService
{
    public const string GeneralSection = "GENERAL";
    public const string KeysKey = "PresetShortcutKeys";
    public const string PathsKey = "PresetShortcutPaths";
    public const string CurrentPresetKey = "PresetPath";

    /// <summary>ReShade's input settings section.</summary>
    public const string InputSection = "INPUT";

    /// <summary>
    /// ReShade's "Force correct modifier keys" option. On (1, ReShade's default when the key is
    /// absent): a shortcut fires only with exactly its modifiers. Off (0): it also fires while
    /// extra modifiers are held, so Ctrl+Alt+Shift+F13 also triggers an F13 or Ctrl+F13 entry.
    /// </summary>
    public const string ForceShortcutModifiersKey = "ForceShortcutModifiers";

    /// <summary>First managed virtual key (VK_F13). F13–F24 need Ctrl+Alt+Shift and never collide with DBD bindings.</summary>
    public const int FirstFunctionKey = 0x7C; // VK_F13
    public const int LastFunctionKey = 0x87; // VK_F24

    /// <summary>Number of managed combos: Ctrl+Alt+Shift+F13..F24 (12) then Ctrl+Alt+Shift+0..9 (10).</summary>
    public const int ShortcutPoolSize = 22;

    /// <summary>Managed pool in allocation order. Every combo also needs Ctrl+Alt+Shift.</summary>
    private static readonly int[] PoolKeys = Enumerable.Range(FirstFunctionKey, LastFunctionKey - FirstFunctionKey + 1)
        .Concat(Enumerable.Range('0', 10))
        .ToArray();

    /// <summary>Serializes every read-modify-write of ReShade.ini within this process.</summary>
    private static readonly object IniWriteLock = new();

    /// <summary>Human-readable key combo, e.g. "Ctrl+Alt+Shift+F13".</summary>
    public static string FormatShortcut(ReshadeShortcutConfig shortcut)
    {
        var parts = new List<string>();
        if (shortcut.Ctrl)
        {
            parts.Add("Ctrl");
        }

        if (shortcut.Alt)
        {
            parts.Add("Alt");
        }

        if (shortcut.Shift)
        {
            parts.Add("Shift");
        }

        parts.Add(KeyName(shortcut.VirtualKey));
        return string.Join("+", parts);
    }

    internal static string KeyName(int virtualKey)
    {
        if (virtualKey >= 0x70 && virtualKey <= 0x87)
        {
            return $"F{virtualKey - 0x6F}";
        }

        if (virtualKey >= 'A' && virtualKey <= 'Z')
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey >= '0' && virtualKey <= '9')
        {
            return ((char)virtualKey).ToString();
        }

        return $"VK 0x{virtualKey:X2}";
    }

    /// <summary>True when both combos press the same key with the same modifiers. False if either is null.</summary>
    public static bool SameCombo(ReshadeShortcutConfig? a, ReshadeShortcutConfig? b) =>
        a is not null && b is not null &&
        a.VirtualKey == b.VirtualKey && a.Ctrl == b.Ctrl && a.Shift == b.Shift && a.Alt == b.Alt;

    /// <summary>True when the combo is one of the app-managed pool combos (Ctrl+Alt+Shift + F13–F24 or 0–9).</summary>
    public static bool IsInShortcutPool(ReshadeShortcutConfig? shortcut) =>
        shortcut is not null && shortcut.Ctrl && shortcut.Alt && shortcut.Shift &&
        Array.IndexOf(PoolKeys, shortcut.VirtualKey) >= 0;

    private static ReshadeShortcutConfig PoolCombo(int virtualKey) =>
        new() { VirtualKey = virtualKey, Ctrl = true, Shift = true, Alt = true };

    /// <summary>
    /// True when pressing one combo can also trigger the other in ReShade: the same combo when
    /// ReShade forces exact modifiers (<paramref name="exactModifiers"/>), otherwise the same key
    /// whatever the modifiers (with "Force correct modifier keys" off, held extra modifiers do
    /// not stop a shortcut from firing, and ReShade switches to the first matching entry).
    /// </summary>
    internal static bool CanTriggerTogether(ReshadeShortcutConfig a, ReshadeShortcutConfig b, bool exactModifiers) =>
        exactModifiers ? SameCombo(a, b) : a.VirtualKey == b.VirtualKey;

    /// <summary>
    /// True when ReShade fires <paramref name="entry"/> while <paramref name="pressed"/> is held
    /// (ReShade's input::is_key_pressed): the same key, and, when it forces exact modifiers
    /// (<paramref name="exactModifiers"/>), exactly the entry's modifiers; otherwise at least the
    /// entry's modifiers (held extra ones don't matter). Key 0 never fires.
    /// </summary>
    public static bool Fires(ReshadeShortcutConfig pressed, ReshadeShortcutConfig entry, bool exactModifiers)
    {
        ArgumentNullException.ThrowIfNull(pressed);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.VirtualKey == 0 || entry.VirtualKey != pressed.VirtualKey)
        {
            return false;
        }

        return exactModifiers
            ? entry.Ctrl == pressed.Ctrl && entry.Shift == pressed.Shift && entry.Alt == pressed.Alt
            : (!entry.Ctrl || pressed.Ctrl) && (!entry.Shift || pressed.Shift) && (!entry.Alt || pressed.Alt);
    }

    /// <summary>
    /// The entry ReShade switches to when <paramref name="pressed"/> is pressed: the first of
    /// <paramref name="loaded"/> (in the order ReShade loaded them, i.e. ReShade.ini order) that
    /// <see cref="Fires">fires</see>. Null when none does.
    /// </summary>
    public static IniShortcutEntry? EntryFiredBy(
        IEnumerable<IniShortcutEntry> loaded,
        ReshadeShortcutConfig pressed,
        bool exactModifiers)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(pressed);
        return loaded.FirstOrDefault(e => e?.Shortcut is not null && Fires(pressed, e.Shortcut, exactModifiers));
    }

    /// <summary>
    /// True when <paramref name="entry"/> is one this app wrote: well formed, one of the app's
    /// pool combos (the only combos it ever writes) and pointing at a path in
    /// <paramref name="managedOrPreviouslyManagedPaths"/> (normalized against the ini folder).
    /// Anything else is the user's own entry, even when it points at an assigned preset file.
    /// </summary>
    internal static bool IsAppOwned(ShortcutEntry entry, IReadOnlySet<string> managedOrPreviouslyManagedPaths) =>
        entry.IsWellFormed &&
        IsInShortcutPool(entry.Shortcut) &&
        managedOrPreviouslyManagedPaths.Contains(entry.NormalizedPath);

    /// <summary>
    /// Whether ReShade matches shortcut modifiers exactly (<see cref="ForceShortcutModifiersKey"/>).
    /// Conservative: true only when the key is absent (ReShade's default is on) or every occurrence
    /// in any [INPUT] section is exactly 1, the value ReShade writes for on. Anything else (0, blank,
    /// "true", garbage) counts as off, which only ever reserves more combos.
    /// </summary>
    internal static bool ForcesExactModifiers(IniDocument doc) =>
        doc.Lines
            .Where(l => l.Kind == IniDocument.IniLineKind.KeyValue &&
                        string.Equals(l.Section, InputSection, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(l.Key, ForceShortcutModifiersKey, StringComparison.OrdinalIgnoreCase))
            .All(l => string.Equals(ParseReshadeList(l.Value)[0].Trim(), "1", StringComparison.Ordinal));

    /// <summary>
    /// Fresh allocation (no existing combos, nothing reserved): names sorted
    /// ordinally → F13..F24, then Ctrl+Alt+Shift+0..9. Prefer the stable
    /// overload so assigning/removing a realm never moves other realms' keys.
    /// </summary>
    public static Dictionary<string, ReshadeShortcutConfig> AllocateShortcuts(IEnumerable<string> realmsWithPresets) =>
        AllocateShortcuts(realmsWithPresets, existing: null, reserved: null);

    /// <summary>
    /// Stable, collision-free shortcut allocation. The running ReShade keeps the
    /// shortcuts it loaded at startup, so a name's combo must never move while
    /// other names are assigned or removed:
    /// <list type="bullet">
    /// <item>A name whose combo in <paramref name="existing"/> is still valid keeps it. Valid = in the
    /// managed pool, not in <paramref name="reserved"/>, not already kept by another name (names are
    /// processed in ordinal order, so the first one keeps a duplicated combo).</item>
    /// <item>Every other name gets the lowest free pool slot (Ctrl+Alt+Shift+F13..F24, then 0..9),
    /// new names processed in ordinal order so the result is deterministic.</item>
    /// <item>Combos in <paramref name="reserved"/> (used by, or able to trigger, the user's own
    /// ReShade.ini entries, see <see cref="ReadReservedShortcuts"/>) are never handed out.</item>
    /// </list>
    /// </summary>
    /// <returns>Name (case-insensitive) → new combo instance for every distinct non-blank name.</returns>
    /// <exception cref="InvalidOperationException">The pool has fewer free combos than names need.</exception>
    public static Dictionary<string, ReshadeShortcutConfig> AllocateShortcuts(
        IEnumerable<string> names,
        IReadOnlyDictionary<string, ReshadeShortcutConfig>? existing,
        IEnumerable<ReshadeShortcutConfig>? reserved)
    {
        ArgumentNullException.ThrowIfNull(names);
        var sorted = names
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        var reservedKeys = new HashSet<int>();
        foreach (ReshadeShortcutConfig? r in reserved ?? Enumerable.Empty<ReshadeShortcutConfig>())
        {
            if (IsInShortcutPool(r))
            {
                reservedKeys.Add(r!.VirtualKey);
            }
        }

        var current = new Dictionary<string, ReshadeShortcutConfig>(StringComparer.OrdinalIgnoreCase);
        if (existing is not null)
        {
            foreach (var kv in existing.Where(kv => kv.Key is not null).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (kv.Value is not null)
                {
                    current.TryAdd(kv.Key, kv.Value);
                }
            }
        }

        var result = new Dictionary<string, ReshadeShortcutConfig>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<int>();
        var needSlot = new List<string>();
        foreach (string name in sorted)
        {
            if (current.TryGetValue(name, out ReshadeShortcutConfig? kept) &&
                IsInShortcutPool(kept) &&
                !reservedKeys.Contains(kept.VirtualKey) &&
                taken.Add(kept.VirtualKey))
            {
                result[name] = PoolCombo(kept.VirtualKey);
            }
            else
            {
                needSlot.Add(name);
            }
        }

        var free = PoolKeys.Where(vk => !taken.Contains(vk) && !reservedKeys.Contains(vk)).ToList();
        if (needSlot.Count > free.Count)
        {
            string reservedNote = reservedKeys.Count > 0
                ? $" ({reservedKeys.Count} of them are used by your own ReShade shortcuts or would also fire them)"
                : string.Empty;
            throw new InvalidOperationException(
                $"Too many presets assigned: ReShade shortcut pool ({ShortcutPoolSize}) exhausted — " +
                $"{sorted.Count} presets need a shortcut, but only {ShortcutPoolSize - reservedKeys.Count} " +
                $"Ctrl+Alt+Shift+F13–F24/0–9 combos can be used{reservedNote}. " +
                "Remove a preset assignment and try again.");
        }

        for (int i = 0; i < needSlot.Count; i++)
        {
            result[needSlot[i]] = PoolCombo(free[i]);
        }

        return result;
    }

    public static string? ReadCurrentPreset(string iniPath)
    {
        if (!File.Exists(iniPath))
        {
            return null;
        }

        return IniDocument.Parse(ReadIniText(iniPath).Text).GetValue(GeneralSection, CurrentPresetKey);
    }

    /// <summary>
    /// Reads the current preset and resolves it to an absolute path. ReShade
    /// stores PresetPath relative to its own folder, so relative values are
    /// resolved against the ini directory before comparing.
    /// </summary>
    public static string? ReadCurrentPresetAbsolute(string iniPath)
    {
        string? raw;
        try
        {
            raw = ReadCurrentPreset(iniPath);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return PresetValidationService.NormalizePresetPath(raw, IniDirectory(iniPath));
        }
        catch
        {
            return raw;
        }
    }

    /// <summary>
    /// The shortcut entries ReShade loads from this ini (path i paired with key
    /// quad i, values read the way ReShade reads them), in file order. Entries
    /// without a preset path are omitted. Use it to snapshot what a running
    /// ReShade loaded at startup.
    /// </summary>
    /// <returns>Empty when the file does not exist.</returns>
    /// <exception cref="IOException">The ini exists but cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The ini exists but cannot be read.</exception>
    public static IReadOnlyList<IniShortcutEntry> ReadShortcutAssignments(string iniPath)
    {
        if (!File.Exists(iniPath))
        {
            return Array.Empty<IniShortcutEntry>();
        }

        ShortcutLayout layout = ParseShortcutLayout(IniDocument.Parse(ReadIniText(iniPath).Text), IniDirectory(iniPath));
        return layout.Entries
            .Where(e => e.NormalizedPath.Length > 0)
            .Select(e => new IniShortcutEntry(Copy(e.Shortcut), e.NormalizedPath))
            .ToList();
    }

    /// <summary>
    /// Combos of the ini's foreign entries: every loaded entry that is not
    /// <see cref="IsAppOwned">app-owned</see> — its normalized path is not in
    /// <paramref name="managedOrPreviouslyManagedPaths"/>, or its combo is not
    /// one of the app's pool combos (the user's own shortcut for an assigned
    /// preset file stays the user's), or it is malformed.
    /// </summary>
    /// <returns>Empty when the file does not exist.</returns>
    /// <exception cref="IOException">The ini exists but cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The ini exists but cannot be read.</exception>
    public static IReadOnlyList<ReshadeShortcutConfig> ReadForeignShortcuts(
        string iniPath,
        IEnumerable<string> managedOrPreviouslyManagedPaths)
    {
        if (!File.Exists(iniPath))
        {
            return Array.Empty<ReshadeShortcutConfig>();
        }

        string? iniDir = IniDirectory(iniPath);
        ShortcutLayout layout = ParseShortcutLayout(IniDocument.Parse(ReadIniText(iniPath).Text), iniDir);
        return ForeignEntries(layout, NormalizedSet(managedOrPreviouslyManagedPaths, iniDir))
            .Select(e => Copy(e.Shortcut))
            .ToList();
    }

    /// <summary>
    /// The combos the allocation must never hand out. Pass the result as <c>reserved</c> to
    /// <see cref="AllocateShortcuts(IEnumerable{string}, IReadOnlyDictionary{string, ReshadeShortcutConfig}, IEnumerable{ReshadeShortcutConfig})"/>:
    /// the combo of every foreign entry (see <see cref="ReadForeignShortcuts"/>) and, when
    /// ReShade does not force exact modifiers (<see cref="ForceShortcutModifiersKey"/> off),
    /// also the pool combo on each foreign entry's key — sending Ctrl+Alt+Shift+<i>key</i>
    /// would then fire the user's <i>key</i> / Ctrl+<i>key</i> / … shortcut as well.
    /// </summary>
    /// <returns>Empty when the file does not exist.</returns>
    /// <exception cref="IOException">The ini exists but cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The ini exists but cannot be read.</exception>
    public static IReadOnlyList<ReshadeShortcutConfig> ReadReservedShortcuts(
        string iniPath,
        IEnumerable<string> managedOrPreviouslyManagedPaths)
    {
        if (!File.Exists(iniPath))
        {
            return Array.Empty<ReshadeShortcutConfig>();
        }

        string? iniDir = IniDirectory(iniPath);
        IniDocument doc = IniDocument.Parse(ReadIniText(iniPath).Text);
        bool exactModifiers = ForcesExactModifiers(doc);
        var reserved = new List<ReshadeShortcutConfig>();
        foreach (ShortcutEntry entry in ForeignEntries(ParseShortcutLayout(doc, iniDir), NormalizedSet(managedOrPreviouslyManagedPaths, iniDir)))
        {
            reserved.Add(Copy(entry.Shortcut));
            if (!exactModifiers && Array.IndexOf(PoolKeys, entry.Shortcut.VirtualKey) >= 0)
            {
                reserved.Add(PoolCombo(entry.Shortcut.VirtualKey));
            }
        }

        return reserved;
    }

    /// <summary>
    /// Whether the ReShade using this ini matches shortcut modifiers exactly (see
    /// <see cref="ForceShortcutModifiersKey"/>; ReShade saves the option here as soon as it is
    /// changed in its settings, and this app never writes it). False, i.e. not known to be
    /// exact, when the file does not exist.
    /// </summary>
    /// <exception cref="IOException">The ini exists but cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The ini exists but cannot be read.</exception>
    public static bool ReadForcesExactModifiers(string iniPath) =>
        File.Exists(iniPath) && ForcesExactModifiers(IniDocument.Parse(ReadIniText(iniPath).Text));

    private static IEnumerable<ShortcutEntry> ForeignEntries(ShortcutLayout layout, IReadOnlySet<string> managedOrPreviouslyManaged) =>
        layout.Entries.Where(e => !IsAppOwned(e, managedOrPreviouslyManaged));

    /// <summary>
    /// Set <c>[GENERAL] PresetPath</c> (ReShade's startup preset) to
    /// <paramref name="presetPath"/>, preserving everything else byte-for-byte.
    /// Uses the same backup-once + temp file + move write path as the sync.
    /// Only meaningful while ReShade is not running (it reads the value at startup).
    /// </summary>
    /// <returns>True if the file was changed; false if it already points at that preset.</returns>
    /// <exception cref="FileNotFoundException">ReShade.ini does not exist.</exception>
    public bool WriteCurrentPreset(string iniPath, string presetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetPath);
        if (!File.Exists(iniPath))
        {
            throw new FileNotFoundException("ReShade.ini not found.", iniPath);
        }

        lock (IniWriteLock)
        {
            string? iniDir = IniDirectory(iniPath);
            var (text, encoding) = ReadIniText(iniPath);
            var doc = IniDocument.Parse(text);
            string target = PresetValidationService.NormalizePresetPath(presetPath, iniDir);
            string? current = doc.GetValue(GeneralSection, CurrentPresetKey);
            if (!string.IsNullOrWhiteSpace(current) &&
                string.Equals(PresetValidationService.NormalizePresetPath(current, iniDir), target, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!doc.SetValue(GeneralSection, CurrentPresetKey, target))
            {
                return false;
            }

            WriteIniAtomically(iniPath, doc.ToString(), encoding);
            return true;
        }
    }

    /// <summary>
    /// Rewrite the managed entries of PresetShortcutKeys/Paths to match
    /// <paramref name="assignments"/>. Creates a .bak backup on first change.
    /// The app's own entries (<see cref="IsAppOwned"/>: a pool combo for a
    /// path it manages or managed) are rewritten, and those whose paths are no
    /// longer assigned are removed; every other entry (foreign, i.e. the
    /// user's own shortcuts — also one for an assigned preset file) and
    /// malformed entries are always preserved byte-for-byte. Paths in the ini
    /// are compared after resolving them against the ini's folder. Never
    /// writes an entry whose combo can trigger a kept entry (the same combo, or
    /// the same key when ReShade's <see cref="ForceShortcutModifiersKey"/> is
    /// off): such an assignment is skipped and reported in
    /// <see cref="ShortcutSyncResult.Conflicts"/>. With nothing to write and no
    /// existing shortcut keys, the file is left untouched. Both lists are read
    /// and written in ReShade's list syntax (",," = escaped comma); every kept
    /// entry loads in ReShade exactly as before.
    /// </summary>
    public ShortcutSyncResult SyncPresetShortcuts(
        string iniPath,
        IReadOnlyCollection<PresetShortcutAssignment> assignments,
        IEnumerable<string>? previouslyManagedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        if (assignments.Any(a => a is null || a.Shortcut is null))
        {
            throw new ArgumentException("Every shortcut assignment needs a shortcut.", nameof(assignments));
        }

        if (!File.Exists(iniPath))
        {
            throw new FileNotFoundException("ReShade.ini not found.", iniPath);
        }

        lock (IniWriteLock)
        {
            string? iniDir = IniDirectory(iniPath);
            var managedPaths = NormalizedSet(assignments.Select(a => a.PresetPath), iniDir);
            var ownedPaths = NormalizedSet(previouslyManagedPaths, iniDir);
            ownedPaths.UnionWith(managedPaths);

            var (text, encoding) = ReadIniText(iniPath);
            var doc = IniDocument.Parse(text);
            ShortcutLayout layout = ParseShortcutLayout(doc, iniDir);
            bool exactModifiers = ForcesExactModifiers(doc);

            // Entries in the order they are written: kept entries, managed entries, then
            // kept entries that can only be written last (see below).
            var written = new List<(IReadOnlyList<string> Keys, string Path)>();
            var writtenLast = new List<(IReadOnlyList<string> Keys, string Path)>();
            var kept = new List<ShortcutEntry>();
            var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int removed = 0;

            // Keep foreign and malformed entries in their original order; each contributes
            // exactly 4 key elements + 1 path element, so later pairs stay aligned. Only the
            // app's own entries are dropped (and rewritten below when still assigned).
            foreach (ShortcutEntry entry in layout.Entries)
            {
                if (IsAppOwned(entry, ownedPaths))
                {
                    existingPaths.Add(entry.NormalizedPath);
                    if (!managedPaths.Contains(entry.NormalizedPath))
                    {
                        removed++;
                    }

                    continue;
                }

                // An empty path can only be written first or last: anywhere else it would put two
                // separators next to each other (",,"), which ReShade reads as an escaped comma.
                // A parsed list holds empty paths only in those two places, so an entry that is not
                // first in the output moves behind the managed entries, i.e. to the end.
                if (entry.PathElement.Length == 0 && kept.Count > 0)
                {
                    writtenLast.Add((entry.KeyElements, entry.PathElement));
                }
                else
                {
                    written.Add((entry.KeyElements, entry.PathElement));
                }

                kept.Add(entry);
            }

            var conflicts = new List<ShortcutConflict>();
            var writtenManaged = new List<PresetShortcutAssignment>();
            int added = 0;

            foreach (PresetShortcutAssignment a in assignments.OrderBy(a => a.Realm, StringComparer.Ordinal))
            {
                string? problem = null;
                string? conflictingPath = null;
                string path = (a.PresetPath ?? string.Empty).Trim();
                if (path.Length == 0)
                {
                    problem = $"{AppConfigKeys.DisplayName(a.Realm)}: no preset path; not written to ReShade.ini.";
                }
                else if (path.Contains(','))
                {
                    problem = $"{AppConfigKeys.DisplayName(a.Realm)}: the preset path contains ',' which this app does not " +
                              "write into ReShade.ini's comma-separated shortcut list; not written. Rename the file or folder.";
                }
                else if (kept.FirstOrDefault(e => CanTriggerTogether(e.Shortcut, a.Shortcut, exactModifiers)) is { } foreign)
                {
                    conflictingPath = foreign.PathElement.Trim();
                    string where = conflictingPath.Length > 0 ? conflictingPath : "no preset";
                    problem = SameCombo(foreign.Shortcut, a.Shortcut)
                        ? $"{AppConfigKeys.DisplayName(a.Realm)}: {FormatShortcut(a.Shortcut)} is already used by " +
                          $"another ReShade shortcut ({where}); kept that entry and skipped this one."
                        : $"{AppConfigKeys.DisplayName(a.Realm)}: {FormatShortcut(a.Shortcut)} would also trigger your ReShade " +
                          $"shortcut {FormatShortcut(foreign.Shortcut)} ({where}) because ReShade's 'Force correct modifier keys' " +
                          "is off; kept that entry and skipped this one.";
                }
                else if (writtenManaged.FirstOrDefault(w => SameCombo(w.Shortcut, a.Shortcut)) is { } twin)
                {
                    conflictingPath = twin.PresetPath;
                    problem = $"{AppConfigKeys.DisplayName(a.Realm)}: {FormatShortcut(a.Shortcut)} is also allocated to " +
                              $"{AppConfigKeys.DisplayName(twin.Realm)}; skipped this one.";
                }

                if (problem is not null)
                {
                    conflicts.Add(new ShortcutConflict(a.Realm, a.PresetPath ?? string.Empty, a.Shortcut, conflictingPath, problem));
                    continue;
                }

                writtenManaged.Add(a);
                written.Add((
                    new[]
                    {
                        a.Shortcut.VirtualKey.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Bool(a.Shortcut.Ctrl),
                        Bool(a.Shortcut.Shift),
                        Bool(a.Shortcut.Alt)
                    },
                    path));
                if (!existingPaths.Contains(PresetValidationService.NormalizePresetPath(path, iniDir)))
                {
                    added++;
                }
            }

            written.AddRange(writtenLast);

            // Unpaired trailing elements (a partial key quad, quads without paths, paths without
            // quads) go last: they stay unpaired exactly as before and never shift a real pair.
            List<string> keyList = ComposeList(written.SelectMany(e => e.Keys), layout.KeyTail, layout.KeyElements, isKeyList: true);
            List<string> pathList = ComposeList(written.Select(e => e.Path), layout.PathTail, layout.PathElements, isKeyList: false);

            // ReShade reads a present but blank PresetShortcutPaths as one empty path, which would
            // pair with the first unpaired key quad; with no paths left, those quads go too.
            if (pathList.Count == 0 && keyList.Count >= 4 && doc.GetValue(GeneralSection, PathsKey) is not null)
            {
                keyList.Clear();
            }

            string keysValue = FormatReshadeList(keyList);
            string pathsValue = FormatReshadeList(pathList);
            if (!ReadsBackAs(
                    keysValue, keysValue.Length > 0 || doc.GetValue(GeneralSection, KeysKey) is not null,
                    pathsValue, pathsValue.Length > 0 || doc.GetValue(GeneralSection, PathsKey) is not null,
                    written))
            {
                // Unreachable for lists parsed from an ini; never write something ReShade would misread.
                throw new InvalidOperationException(
                    "ReShade.ini's shortcut lists could not be rewritten without changing what ReShade loads; " +
                    "the file was left unchanged. Remove the malformed PresetShortcutKeys/PresetShortcutPaths values and try again.");
            }

            bool changed = false;
            changed |= SetListValue(doc, KeysKey, keysValue);
            changed |= SetListValue(doc, PathsKey, pathsValue);

            if (changed)
            {
                WriteIniAtomically(iniPath, doc.ToString(), encoding);
            }

            return new ShortcutSyncResult(changed, added, removed, kept.Count) { Conflicts = conflicts };
        }
    }

    /// <summary>Never creates an empty key that was not there (a first sync with nothing assigned is a no-op).</summary>
    private static bool SetListValue(IniDocument doc, string key, string value)
    {
        if (value.Length == 0 && doc.GetValue(GeneralSection, key) is null)
        {
            return false;
        }

        return doc.SetValue(GeneralSection, key, value);
    }

    /// <summary>
    /// One shortcut list as it will be written: <paramref name="paired"/> elements, then the
    /// unpaired <paramref name="tail"/>. A list equal to <paramref name="original"/> is returned
    /// unchanged, so it is written back verbatim. Otherwise empty unpaired elements are dropped
    /// (they hold nothing and ReShade ignores them; in the middle they would form ",,"), and an
    /// element ReShade would misread at its new position is repaired: a key element becomes the
    /// number ReShade reads from it (always 0 there), an unpaired path is dropped.
    /// </summary>
    private static List<string> ComposeList(
        IEnumerable<string> paired,
        IReadOnlyList<string> tail,
        IReadOnlyList<string> original,
        bool isKeyList)
    {
        var list = paired.ToList();
        int tailStart = list.Count;
        if (list.Concat(tail).SequenceEqual(original, StringComparer.Ordinal))
        {
            return list.Concat(tail).ToList();
        }

        list.AddRange(tail.Where(t => t.Length > 0));
        for (int i = 1; i < list.Count; i++)
        {
            if (!IsMisreadAt(list, i))
            {
                continue;
            }

            if (isKeyList)
            {
                list[i] = ReadLikeReshade(list[i]).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (i >= tailStart)
            {
                list.RemoveAt(i);
                i--;
            }

            // A paired path that cannot be written is caught by ReadsBackAs.
        }

        return list;
    }

    /// <summary>
    /// True when ReShade would read the formatted lists back as exactly the
    /// <paramref name="entries"/> (same key values, same paths, same count).
    /// A key that is absent (not <paramref name="keysPresent"/>) reads as no elements.
    /// </summary>
    private static bool ReadsBackAs(
        string keysValue,
        bool keysPresent,
        string pathsValue,
        bool pathsPresent,
        IReadOnlyList<(IReadOnlyList<string> Keys, string Path)> entries)
    {
        List<string> keys = keysPresent ? ParseReshadeList(keysValue) : new List<string>();
        List<string> paths = pathsPresent ? ParseReshadeList(pathsValue) : new List<string>();
        if (Math.Min(keys.Count / 4, paths.Count) != entries.Count)
        {
            return false;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            if (!string.Equals(paths[i], entries[i].Path, StringComparison.Ordinal))
            {
                return false;
            }

            for (int k = 0; k < 4; k++)
            {
                if (ReadLikeReshade(keys[i * 4 + k]) != ReadLikeReshade(entries[i].Keys[k]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static HashSet<string> NormalizedSet(IEnumerable<string?>? paths, string? baseDirectory) =>
        new(
            (paths ?? Enumerable.Empty<string?>())
                .Select(p => PresetValidationService.NormalizePresetPath(p, baseDirectory))
                .Where(p => p.Length > 0),
            StringComparer.OrdinalIgnoreCase);

    internal static string? IniDirectory(string iniPath)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(iniPath));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads the ini (sharing with writers such as ReShade) and reports its encoding, BOM included.</summary>
    internal static (string Text, Encoding Encoding) ReadIniText(string iniPath)
    {
        using var fs = new FileStream(iniPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        return (text, reader.CurrentEncoding);
    }

    /// <summary>Backup once, write a flushed temp file in the same folder, then atomically replace the ini.</summary>
    private static void WriteIniAtomically(string iniPath, string content, Encoding encoding)
    {
        BackupOnce(iniPath);
        string tmp = iniPath + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(encoding.GetPreamble());
                fs.Write(encoding.GetBytes(content));
                fs.Flush(flushToDisk: true);
            }

            File.Move(tmp, iniPath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tmp);
            }
            catch
            {
                // Best effort; the original ini is untouched.
            }

            throw;
        }
    }

    private static void BackupOnce(string iniPath)
    {
        try
        {
            string bak = iniPath + ".dbdautoreshade.bak";
            if (!File.Exists(bak))
            {
                File.Copy(iniPath, bak);
            }
        }
        catch
        {
            // Backup is best-effort.
        }
    }

    private static string Bool(bool b) => b ? "1" : "0";

    private static ReshadeShortcutConfig Copy(ReshadeShortcutConfig s) =>
        new() { VirtualKey = s.VirtualKey, Ctrl = s.Ctrl, Shift = s.Shift, Alt = s.Alt };

    /// <summary>
    /// One paired shortcut entry: exactly 4 key elements and 1 path element at
    /// the same index. <see cref="Shortcut"/> is what ReShade loads (non-numeric
    /// elements read as 0, like its strtoul); <see cref="IsWellFormed"/> means
    /// all four key elements are integers and the path is non-empty — only such
    /// entries can be managed. Elements are kept as ReShade reads them (an
    /// escaped ",," already unescaped to ',') and re-escaped when written.
    /// </summary>
    internal sealed record ShortcutEntry(
        IReadOnlyList<string> KeyElements,
        string PathElement,
        ReshadeShortcutConfig Shortcut,
        bool IsWellFormed,
        string NormalizedPath);

    /// <summary>
    /// Shortcut lists split into aligned entries plus unpaired trailing elements
    /// (<see cref="KeyTail"/>: a partial quad or quads beyond the last path;
    /// <see cref="PathTail"/>: paths beyond the last full quad).
    /// <see cref="KeyElements"/>/<see cref="PathElements"/> are the complete lists
    /// as ReShade reads them.
    /// </summary>
    internal sealed record ShortcutLayout(
        IReadOnlyList<ShortcutEntry> Entries,
        IReadOnlyList<string> KeyTail,
        IReadOnlyList<string> PathTail,
        int MalformedCount,
        IReadOnlyList<string> KeyElements,
        IReadOnlyList<string> PathElements)
    {
        /// <summary>Unpaired key elements that hold something (a blank value reads as one empty element).</summary>
        public int UnpairedKeyCount => KeyTail.Count(t => t.Length > 0);

        /// <summary>Unpaired paths that hold something (a blank value reads as one empty element).</summary>
        public int UnpairedPathCount => PathTail.Count(t => t.Length > 0);
    }

    /// <summary>
    /// Split PresetShortcutKeys/Paths exactly like ReShade (see <see cref="ParseReshadeList"/>)
    /// so that path i always pairs with key quad i, resolving relative paths
    /// against <paramref name="iniDirectory"/>.
    /// </summary>
    internal static ShortcutLayout ParseShortcutLayout(IniDocument doc, string? iniDirectory)
    {
        List<string> keys = ParseReshadeList(doc.GetValue(GeneralSection, KeysKey));
        List<string> paths = ParseReshadeList(doc.GetValue(GeneralSection, PathsKey));

        int pairs = Math.Min(keys.Count / 4, paths.Count);
        var entries = new List<ShortcutEntry>(pairs);
        int malformed = 0;
        for (int i = 0; i < pairs; i++)
        {
            string[] quad = keys.GetRange(i * 4, 4).ToArray();
            string pathElement = paths[i];
            bool allInts = quad.All(t => int.TryParse(t.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out _));
            string normalized = PresetValidationService.NormalizePresetPath(pathElement, iniDirectory);
            bool wellFormed = allInts && normalized.Length > 0;
            if (!wellFormed)
            {
                malformed++;
            }

            var shortcut = new ReshadeShortcutConfig
            {
                VirtualKey = ReadLikeReshade(quad[0]),
                Ctrl = ReadLikeReshade(quad[1]) != 0,
                Shift = ReadLikeReshade(quad[2]) != 0,
                Alt = ReadLikeReshade(quad[3]) != 0
            };
            entries.Add(new ShortcutEntry(quad, pathElement, shortcut, wellFormed, normalized));
        }

        string[] keyTail = keys.Skip(pairs * 4).ToArray();
        string[] pathTail = paths.Skip(pairs).ToArray();
        return new ShortcutLayout(entries, keyTail, pathTail, malformed, keys, paths);
    }

    /// <summary>
    /// Split a list value exactly like ReShade's ini_file::load: a single ',' separates
    /// elements, while ",," is an escaped comma that stays inside the element (read as one
    /// ','). Scanning is left to right, so ",,," is an escaped comma followed by a separator.
    /// Empty elements keep their index. A present but blank value is one empty element,
    /// as in ReShade; a missing key (<see langword="null"/>) has none.
    /// </summary>
    internal static List<string> ParseReshadeList(string? value)
    {
        var elements = new List<string>();
        if (value is null)
        {
            return elements;
        }

        var current = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != ',')
            {
                current.Append(c);
            }
            else if (i + 1 < value.Length && value[i + 1] == ',')
            {
                current.Append(',');
                i++;
            }
            else
            {
                elements.Add(current.ToString());
                current.Clear();
            }
        }

        elements.Add(current.ToString());
        return elements;
    }

    /// <summary>
    /// Join elements the way ReShade's ini_file::save does: every ',' inside an element is
    /// doubled and elements are separated by a single ','. ReShade reads the result back as
    /// exactly <paramref name="elements"/> unless <see cref="IsMisreadAt"/> is true for one of
    /// them (callers repair or reject such lists first).
    /// </summary>
    internal static string FormatReshadeList(IReadOnlyList<string> elements) =>
        string.Join(",", elements.Select(e => e.Replace(",", ",,", StringComparison.Ordinal)));

    /// <summary>
    /// True when ReShade would misread element <paramref name="index"/> of a formatted list:
    /// after a separator, a leading ',' or an empty element followed by another separator
    /// forms ",,", which ReShade reads as an escaped comma, merging two elements. The first
    /// element and an empty last element are always read correctly.
    /// </summary>
    internal static bool IsMisreadAt(IReadOnlyList<string> elements, int index) =>
        index > 0 &&
        (elements[index].StartsWith(',') || (elements[index].Length == 0 && index < elements.Count - 1));

    /// <summary>Integer value of a key token as ReShade's strtoul-based reader sees it: leading digits, else 0.</summary>
    private static int ReadLikeReshade(string token)
    {
        string t = token.Trim();
        if (int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n))
        {
            return n;
        }

        int len = 0;
        while (len < t.Length && char.IsAsciiDigit(t[len]))
        {
            len++;
        }

        return len > 0 && int.TryParse(t.AsSpan(0, len), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out n)
            ? n
            : 0;
    }
}

using DBDAutoReShade.App.Controls;

namespace DBDAutoReShade.App.ViewModels;

/// <summary>The rail's sections.</summary>
public enum AppTab
{
    /// <summary>The state, the map plate and the manual override.</summary>
    Status,

    /// <summary>The realm list and the preset files.</summary>
    Presets,

    /// <summary>Hotkeys, paths, behaviour, sound, the map database, the ReShade check and reset.</summary>
    Settings,
}

/// <summary>What the Status screen tells (the prototype's states).</summary>
public enum ScreenState
{
    /// <summary>Dead by Daylight is not running.</summary>
    Waiting,

    /// <summary>DBD runs, nothing in progress.</summary>
    Ready,

    /// <summary>A scan reads the loading screen (the skill check spins).</summary>
    Scanning,

    /// <summary>The scan is paused: DBD is not in front (the skill check freezes, greyed).</summary>
    Paused,

    /// <summary>A map was found; its preset waits for DBD focus.</summary>
    WaitingFocus,

    /// <summary>The detected realm's preset is active (it stays until the user's next switch).</summary>
    Active,

    /// <summary>A manual override forces one realm's preset.</summary>
    Override,

    /// <summary>An unexpected error (see the status line).</summary>
    Error,
}

/// <summary>The state hero's size step (the prototype's xl / l / m by label length).</summary>
public enum HeroSize
{
    /// <summary>Up to 14 characters.</summary>
    XL,

    /// <summary>Up to 30 characters.</summary>
    L,

    /// <summary>Longer.</summary>
    M,
}

/// <summary>What follows the active preset's file name on the Status screen.</summary>
public enum PresetTail
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>"Confirmed by ReShade" (ember).</summary>
    Confirmed,

    /// <summary>"Next: file when the game has focus" (heartbeat ring).</summary>
    Pending,

    /// <summary>"Forced for realm until you return to automatic" (red).</summary>
    Override,
}

/// <summary>The dot in front of the detection line.</summary>
public enum DetectionDot
{
    /// <summary>None.</summary>
    None,

    /// <summary>Scanning (ember).</summary>
    Ember,

    /// <summary>Paused (a hollow ring).</summary>
    Ring,
}

/// <summary>The tag under the map plate's caption.</summary>
public enum PlateTag
{
    /// <summary>None.</summary>
    None,

    /// <summary>"Waiting for game focus" (ember, heartbeat ring).</summary>
    WaitingForFocus,

    /// <summary>The red "Override" pill.</summary>
    Override,
}

/// <summary>
/// One immutable picture of the Status screen (and the window's atmosphere), computed by the view model from the
/// controller. The view shows one at a time; while the skill check lands it keeps showing the previous one and
/// presents the newest when the dial has lifted away (the prototype's deferred render).
/// </summary>
public sealed record StatusScreen
{
    /// <summary>The state told.</summary>
    public ScreenState State { get; init; }

    /// <summary>The state hero (e.g. "Preset active").</summary>
    public string Hero { get; init; } = string.Empty;

    /// <summary>The hero's size step.</summary>
    public HeroSize HeroSize { get; init; }

    /// <summary>The status line under the hero.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>The active preset's file name; null when none was switched yet.</summary>
    public string? ActiveFile { get; init; }

    /// <summary>What follows the file name.</summary>
    public PresetTail Tail { get; init; }

    /// <summary>The pending file (<see cref="PresetTail.Pending"/>) or the forced realm (<see cref="PresetTail.Override"/>).</summary>
    public string? TailSubject { get; init; }

    /// <summary>The detection line.</summary>
    public string Detection { get; init; } = string.Empty;

    /// <summary>The detection line's dot.</summary>
    public DetectionDot DetectionDot { get; init; }

    /// <summary>The last switch's time (HH:mm:ss); empty when nothing was switched yet.</summary>
    public string LastSwitchTime { get; init; } = string.Empty;

    /// <summary>The last switch's description (what, via which key, the outcome); it starts with a space after a time.</summary>
    public string LastSwitchText { get; init; } = string.Empty;

    /// <summary>The map plate's scene.</summary>
    public PlateScene Plate { get; init; } = PlateScene.Lobby(FireState.Burning, "No map detected yet", string.Empty);

    /// <summary>The map plate's frame.</summary>
    public PlateTone Tone { get; init; }

    /// <summary>The tag under the plate's caption.</summary>
    public PlateTag Tag { get; init; }

    /// <summary>The skill check on the plate.</summary>
    public SkillCheckState SkillCheck { get; init; }

    /// <summary>The detection hotkey (the skill check's centre key).</summary>
    public string HotkeyText { get; init; } = "F8";

    /// <summary>The campfire light behind everything, 0..1.</summary>
    public double EmberLevel { get; init; }

    /// <summary>The Entity leans in (scanning, waiting for focus).</summary>
    public bool Leaning { get; init; }

    /// <summary>The map behind the fog while the Status tab is shown (null: none).</summary>
    public string? BackdropMap { get; init; }

    /// <summary>
    /// The realm of the detection the plate shows (null: it shows none). Set for a realm-only read too, whose plate is
    /// the campfire clearing (<see cref="PlateScene.IsMap"/> is false: the scan read no map).
    /// </summary>
    public string? DetectedRealm { get; init; }

    /// <summary>
    /// <paramref name="next"/> tells a state change after <paramref name="previous"/> (one orchestrated moment on screen):
    /// another state, or another scene on the plate (map or clearing, another map, the fire burning, low or out). A new
    /// caption, message, detection line or tail on the same scene is a smaller update within the current moment.
    /// </summary>
    public static bool IsStateChange(StatusScreen? previous, StatusScreen next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (previous is null)
        {
            return false;
        }

        PlateScene a = previous.Plate, b = next.Plate;
        return previous.State != next.State || a.IsMap != b.IsMap ||
               (a.IsMap ? !string.Equals(a.MapName, b.MapName, StringComparison.OrdinalIgnoreCase) : a.Fire != b.Fire);
    }

    /// <summary>The hero's size step for <paramref name="label"/> (≤ 14 characters XL, ≤ 30 L, else M).</summary>
    public static HeroSize SizeFor(string label) => label.Length <= 14 ? HeroSize.XL : label.Length <= 30 ? HeroSize.L : HeroSize.M;

    /// <summary>The campfire light for a state (the prototype's EMBER table).</summary>
    public static double EmberFor(ScreenState state) => state switch
    {
        ScreenState.Waiting => 0,
        ScreenState.Scanning => 0.55,
        ScreenState.WaitingFocus => 0.72,
        ScreenState.Active => 1,
        ScreenState.Paused => 0.22,
        ScreenState.Override => 0.72,
        ScreenState.Error => 0.22,
        _ => 0.5,
    };
}

/// <summary>One line of the ReShade check report.</summary>
public enum ReportLineKind
{
    /// <summary>"[OK]": a check passed.</summary>
    Ok,

    /// <summary>"[--]": information.</summary>
    Info,

    /// <summary>"[!!]": a problem.</summary>
    Issue,

    /// <summary>Any other line (headers, the result).</summary>
    Result,
}

/// <summary>A styled line of the ReShade check report.</summary>
/// <param name="Kind">How the line is styled.</param>
/// <param name="Text">The line without its "[OK]" / "[--]" / "[!!]" prefix.</param>
public sealed record ReportLine(ReportLineKind Kind, string Text)
{
    /// <summary>The tag shown in front of the line ("OK", "Info", "Issue"; empty for a result line).</summary>
    public string Tag => Kind switch
    {
        ReportLineKind.Ok => "OK",
        ReportLineKind.Info => "Info",
        ReportLineKind.Issue => "Issue",
        _ => string.Empty,
    };

    /// <summary>Parses one report line ("[OK] text", "[--] text", "[!!] text", or plain text).</summary>
    public static ReportLine Parse(string line)
    {
        string text = line ?? string.Empty;
        if (text.Length >= 4 && text[0] == '[' && text[3] == ']')
        {
            string tag = text.Substring(1, 2);
            string rest = text[4..].TrimStart();
            switch (tag)
            {
                case "OK":
                    return new ReportLine(ReportLineKind.Ok, rest);
                case "--":
                    return new ReportLine(ReportLineKind.Info, rest);
                case "!!":
                    return new ReportLine(ReportLineKind.Issue, rest);
            }
        }

        return new ReportLine(ReportLineKind.Result, text.Trim());
    }

    /// <summary>Parses a whole report; blank lines are skipped.</summary>
    public static IReadOnlyList<ReportLine> ParseReport(string? report) =>
        (report ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(Parse)
            .ToList();
}

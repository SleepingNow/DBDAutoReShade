using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DBDAutoReShade.App.Controls;

/// <summary>The outcome of a landing: where the needle stops and when (see <see cref="SkillCheckMath.ComputeLanding"/>).</summary>
/// <param name="StartAngle">The needle's angle when the map is found (degrees clockwise from 12 o'clock).</param>
/// <param name="TargetAngle">Where it stops: 4° into the white great block (may exceed 360; same direction).</param>
/// <param name="ZoneStart">The great block's leading edge the needle lands in (a fresh zone when <paramref name="Fresh"/>).</param>
/// <param name="Lead">Degrees still to run (0 when already in the white, or when nothing may move).</param>
/// <param name="Fresh">The read's zone was behind the needle or too far ahead: a fresh check pops in at the needle's reach.</param>
public readonly record struct SkillCheckLanding(double StartAngle, double TargetAngle, double ZoneStart, double Lead, bool Fresh)
{
    /// <summary>The hit delay: the needle runs on at <see cref="SkillCheckMath.LandingSpeed"/> until it stops.</summary>
    public TimeSpan HitDelay => TimeSpan.FromMilliseconds(Lead / SkillCheckMath.LandingSpeed);
}

/// <summary>
/// The skill check's geometry and timing, ported from the Campfire prototype (DBD's repair check): ring R 64 in a
/// 200-unit dial centred at (100, 100), zone band .93R-1.05R, the great block is the leading 12° (3 %) of a 48° zone
/// (13 %), the spindle needle crosses the ring (.62R-1.42R). Zones relocate every revolution through a deterministic
/// sequence between ~4 and ~10 o'clock. On a map found the needle runs on at a constant 0.3°/ms and stops dead 4° into
/// the white block (the hit), never slowing down.
/// </summary>
public static class SkillCheckMath
{
    /// <summary>Great block, degrees (the zone's leading end).</summary>
    public const double GreatDegrees = 12;

    /// <summary>The whole zone, degrees.</summary>
    public const double ZoneDegrees = 48;

    /// <summary>The needle stops this far into the great block.</summary>
    public const double HitDepth = 4;

    /// <summary>A hit further ahead than this (≈ 307 ms), or already passed, gives way to a fresh check.</summary>
    public const double Reach = 92;

    /// <summary>The landing's constant needle speed, degrees per millisecond.</summary>
    public const double LandingSpeed = 0.3;

    /// <summary>Zone band inner radius.</summary>
    public const double InnerRadius = 59.5;

    /// <summary>Zone band outer radius.</summary>
    public const double OuterRadius = 67.2;

    /// <summary>The ring radius.</summary>
    public const double RingRadius = 64;

    /// <summary>The spindle needle (thick in the middle, tapered at both ends), at 12 o'clock.</summary>
    public const string NeedlePath = "M100,60 Q95.4,37 100,9 Q104.6,37 100,60 Z";

    /// <summary>The needle's soft red glow, at 12 o'clock.</summary>
    public const string NeedleGlowPath = "M100,61.5 Q94.2,37 100,7 Q105.8,37 100,61.5 Z";

    private const int Seed = 13;

    /// <summary>A point on the dial at radius <paramref name="r"/> and <paramref name="degrees"/> clockwise from 12 o'clock.</summary>
    public static Point PointAt(double r, double degrees)
    {
        double a = degrees * Math.PI / 180;
        return new Point(100 + r * Math.Sin(a), 100 - r * Math.Cos(a));
    }

    /// <summary>An annular sector of the zone band from <paramref name="a0"/> to <paramref name="a1"/> degrees (clockwise), as path markup.</summary>
    public static string SectorPath(double a0, double a1, double ri = InnerRadius, double ro = OuterRadius)
    {
        int big = a1 - a0 > 180 ? 1 : 0;
        Point o0 = PointAt(ro, a0), o1 = PointAt(ro, a1), i1 = PointAt(ri, a1), i0 = PointAt(ri, a0);
        return string.Format(
            CultureInfo.InvariantCulture,
            "M{0:0.##},{1:0.##} A{2},{2} 0 {3} 1 {4:0.##},{5:0.##} L{6:0.##},{7:0.##} A{8},{8} 0 {3} 0 {9:0.##},{10:0.##} Z",
            o0.X, o0.Y, ro, big, o1.X, o1.Y, i1.X, i1.Y, ri, i0.X, i0.Y);
    }

    /// <summary>A frozen geometry from path markup.</summary>
    public static Geometry Frozen(string path)
    {
        Geometry geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// The zone start (degrees) of revolution <paramref name="revolution"/> (0-based) of a scan: a seeded sequence
    /// between 112° and 298°; a new check always sits at least 50° from the previous one. The same for every scan.
    /// </summary>
    public static double ZoneStart(int revolution)
    {
        if (revolution < 0)
        {
            revolution = 0;
        }

        long s = Seed;
        double Next()
        {
            s = s * 16807 % 2147483647;
            return s / 2147483647.0;
        }

        for (int i = 0; i < 3; i++)
        {
            Next();
        }

        double previous = double.NaN;
        double zone = 0;
        for (int k = 0; k <= revolution; k++)
        {
            zone = 112 + Next() * 186;
            if (!double.IsNaN(previous) && Math.Abs(zone - previous) < 50)
            {
                zone = 112 + ((zone - 112 + 93) % 186);
            }

            previous = zone;
        }

        return zone;
    }

    /// <summary>Where a still needle rests for revolution <paramref name="revolution"/> (reduced motion: just past that read's zone, a miss).</summary>
    public static double RestAngle(int revolution) => (ZoneStart(revolution) + ZoneDegrees + 22) % 360;

    /// <summary>A paused check that was never spinning: frozen just past the zone (the last read missed).</summary>
    public static double PausedAngle(double zoneStart)
    {
        double a = zoneStart + ZoneDegrees + 26;
        return a > 350 ? zoneStart - 34 : a;
    }

    /// <summary>
    /// The landing for a needle at <paramref name="angle"/> with the great block at <paramref name="zoneStart"/>
    /// (the prototype's skLand): already in the white → it stops right there; ahead within reach → it runs on to
    /// 4° into the block; otherwise a fresh check pops in at the needle's reach (92°, ≈ 307 ms).
    /// <paramref name="still"/> (reduced motion, animations off): no run, the needle steps into the block (delay 0).
    /// </summary>
    public static SkillCheckLanding ComputeLanding(double angle, double zoneStart, bool still = false)
    {
        double a = ((angle % 360) + 360) % 360;
        if (still)
        {
            return new SkillCheckLanding(a, zoneStart + HitDepth, zoneStart, 0, false);
        }

        double into = a - zoneStart;
        if (into >= 0 && into <= GreatDegrees - 0.5)
        {
            return new SkillCheckLanding(a, a, zoneStart, 0, false);
        }

        double lead = zoneStart + HitDepth - a;
        if (lead > 0 && lead <= Reach)
        {
            return new SkillCheckLanding(a, a + lead, zoneStart, lead, false);
        }

        return new SkillCheckLanding(a, a + Reach, a + Reach - HitDepth, Reach, true);
    }

    /// <summary>Splits a hotkey for the centre key: "Ctrl+Alt+Shift+NumPad5" → ("Ctrl+Alt+Shift", "Num 5"); "F8" → (null, "F8").</summary>
    public static (string? Modifiers, string Key) SplitHotkey(string? hotkey)
    {
        string[] parts = (hotkey ?? string.Empty).Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return (null, string.Empty);
        }

        string key = parts[^1];
        if (key.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && key.Length == 7 && char.IsDigit(key[6]))
        {
            key = "Num " + key[6];
        }

        return (parts.Length > 1 ? string.Join("+", parts[..^1]) : null, key);
    }
}

namespace DBDAutoReShade.Core.Services;

/// <summary>
/// Where ReShade.ini can be for a configured DBD executable. Shared by
/// discovery (Desktop's ReshadeDiscoveryService) and the setup check
/// (<see cref="ReshadeDiagnosticsService"/>) so both look in the same places.
/// The configured executable is either the game itself
/// (…\DeadByDaylight\Binaries\Win64\DeadByDaylight-Win64-Shipping.exe), next
/// to which ReShade is installed, or the launcher in the install root
/// (…\Dead by Daylight\DeadByDaylight.exe), from which ReShade sits in
/// DeadByDaylight\Binaries\Win64 (WinGDK for the Microsoft Store/Game Pass build).
/// </summary>
public static class ReshadeSearchLocations
{
    public const string IniFileName = "ReShade.ini";

    /// <summary>Folders walked upwards: the executable's folder plus 3 parents.</summary>
    public const int FolderLevels = 4;

    /// <summary>Game binary folders looked at below each walked folder, launcher layout first.</summary>
    private static readonly string[] GameBinarySubfolders =
    {
        Path.Combine("DeadByDaylight", "Binaries", "Win64"),
        Path.Combine("DeadByDaylight", "Binaries", "WinGDK"),
        Path.Combine("Binaries", "Win64"),
        Path.Combine("Binaries", "WinGDK"),
    };

    /// <summary>
    /// For the folder of <paramref name="exePath"/> and each of its 3 parents (nearest first):
    /// the folder itself, then its DeadByDaylight\Binaries\Win64, DeadByDaylight\Binaries\WinGDK,
    /// Binaries\Win64 and Binaries\WinGDK subfolders. Absolute paths, each listed once (first
    /// occurrence wins, case-insensitive), in that deterministic order. Folders are not checked
    /// for existence. Empty for a blank or invalid path. Never throws.
    /// </summary>
    public static IReadOnlyList<string> CandidateDirectories(string? exePath)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return result;
        }

        string? dir;
        try
        {
            dir = Path.GetDirectoryName(Path.GetFullPath(exePath.Trim().Trim('"')));
        }
        catch
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string folder)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (seen.Add(full))
            {
                result.Add(full);
            }
        }

        try
        {
            for (int level = 0; level < FolderLevels && !string.IsNullOrEmpty(dir); level++)
            {
                Add(dir);
                foreach (string sub in GameBinarySubfolders)
                {
                    Add(Path.Combine(dir, sub));
                }

                dir = Path.GetDirectoryName(dir);
            }
        }
        catch
        {
            // A path that cannot be combined further: keep what was found so far.
        }

        return result;
    }

    /// <summary>
    /// Every existing ReShade.ini among <see cref="CandidateDirectories"/>, in that order.
    /// Never throws (an unreadable folder is skipped).
    /// </summary>
    public static IReadOnlyList<string> FindIniFiles(string? exePath)
    {
        var found = new List<string>();
        foreach (string dir in CandidateDirectories(exePath))
        {
            string candidate = Path.Combine(dir, IniFileName);
            if (File.Exists(candidate))
            {
                found.Add(candidate);
            }
        }

        return found;
    }
}

using Microsoft.Win32;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>Where a DBD install was found.</summary>
public sealed record DbdLocation(string ExePath, string Source);

/// <summary>
/// Locates the Dead by Daylight installation: running process first, then
/// Steam libraries (registry + libraryfolders.vdf + appmanifest_381210.acf),
/// then Epic Games manifests. All best-effort; manual browse is the fallback.
/// </summary>
public static class DbdDiscoveryService
{
    public const string SteamAppId = "381210";

    private static readonly string[] RelativeExeCandidates =
    {
        Path.Combine("DeadByDaylight", "Binaries", "Win64", "DeadByDaylight-Win64-Shipping.exe"),
        Path.Combine("DeadByDaylight", "Binaries", "WinGDK", "DeadByDaylight-WinGDK-Shipping.exe"),
    };

    public static IReadOnlyList<DbdLocation> Discover()
    {
        var results = new List<DbdLocation>();

        var snap = DbdProcessService.Snapshot();
        if (snap.Running && !string.IsNullOrWhiteSpace(snap.ExePath) && File.Exists(snap.ExePath))
        {
            results.Add(new DbdLocation(snap.ExePath, "Running process"));
        }

        foreach (string lib in SteamLibraries())
        {
            string gameRoot = Path.Combine(lib, "steamapps", "common", "Dead by Daylight");
            foreach (string rel in RelativeExeCandidates)
            {
                string exe = Path.Combine(gameRoot, rel);
                if (File.Exists(exe))
                {
                    results.Add(new DbdLocation(exe, "Steam"));
                    break;
                }
            }

            // App manifest presence strengthens the match but is not required.
            _ = File.Exists(Path.Combine(lib, "steamapps", $"appmanifest_{SteamAppId}.acf"));
        }

        foreach (string exe in EpicCandidates())
        {
            if (File.Exists(exe))
            {
                results.Add(new DbdLocation(exe, "Epic Games"));
            }
        }

        return results
            .GroupBy(r => r.ExePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    internal static IEnumerable<string> SteamLibraries()
    {
        string? steamPath = null;
        try
        {
            steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        }
        catch
        {
            // Registry unavailable.
        }

        if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string fallback = Path.Combine(programFiles, "Steam");
            if (Directory.Exists(fallback))
            {
                steamPath = fallback;
            }
        }

        if (string.IsNullOrWhiteSpace(steamPath))
        {
            yield break;
        }

        yield return steamPath;

        // Additional libraries from libraryfolders.vdf ("path" entries).
        string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
        {
            yield break;
        }

        string content;
        try
        {
            content = File.ReadAllText(vdf);
        }
        catch
        {
            yield break;
        }

        foreach (string line in content.Split('\n'))
        {
            int idx = line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            string rest = line.Substring(idx + 6).Trim().Trim('"', ' ', '\t', '\r');
            string unescaped = rest.Replace("\\\\", "\\", StringComparison.Ordinal);
            if (Directory.Exists(unescaped) && !string.Equals(unescaped, steamPath, StringComparison.OrdinalIgnoreCase))
            {
                yield return unescaped;
            }
        }
    }

    private static IEnumerable<string> EpicCandidates()
    {
        string manifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests))
        {
            yield break;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(manifests, "*.item");
        }
        catch
        {
            yield break;
        }

        foreach (string file in files)
        {
            string json;
            try
            {
                json = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            if (!json.Contains("Dead by Daylight", StringComparison.OrdinalIgnoreCase) &&
                !json.Contains("DeadByDaylight", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? install = ExtractJsonString(json, "InstallLocation");
            string? launch = ExtractJsonString(json, "LaunchExecutable");
            if (install is null || launch is null)
            {
                continue;
            }

            yield return Path.Combine(install, launch);
        }
    }

    private static string? ExtractJsonString(string json, string property)
    {
        int idx = json.IndexOf($"\"{property}\"", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        int colon = json.IndexOf(':', idx);
        if (colon < 0)
        {
            return null;
        }

        int q1 = json.IndexOf('"', colon + 1);
        if (q1 < 0)
        {
            return null;
        }

        int q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0)
        {
            return null;
        }

        return json.Substring(q1 + 1, q2 - q1 - 1).Replace("\\\\", "\\", StringComparison.Ordinal);
    }
}

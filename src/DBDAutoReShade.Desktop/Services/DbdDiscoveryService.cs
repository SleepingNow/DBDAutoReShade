using System.Text;
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

    /// <summary>Default Steam install folder name (used when the app manifest has no "installdir").</summary>
    private const string DefaultSteamInstallDir = "Dead by Daylight";

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
            foreach (string gameRoot in SteamGameRoots(lib))
            {
                string? exe = RelativeExeCandidates
                    .Select(rel => Path.Combine(gameRoot, rel))
                    .FirstOrDefault(File.Exists);
                if (exe is not null)
                {
                    results.Add(new DbdLocation(exe, "Steam"));
                    break;
                }
            }
        }

        foreach (string exe in EpicCandidates())
        {
            if (File.Exists(exe))
            {
                results.Add(new DbdLocation(exe, "Epic Games"));
            }
        }

        return results
            .GroupBy(r => NormalizePath(r.ExePath) ?? r.ExePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>Steam root (registry SteamPath, else Program Files) plus every library from libraryfolders.vdf.</summary>
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
            return Array.Empty<string>();
        }

        string? vdfContent = null;
        try
        {
            string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                vdfContent = File.ReadAllText(vdf);
            }
        }
        catch
        {
            // Unreadable: the Steam root alone is still a library.
        }

        return SteamLibrariesFrom(steamPath, vdfContent);
    }

    /// <summary>
    /// The Steam root plus each existing "path" from libraryfolders.vdf,
    /// normalized (full path, backslashes, no trailing separator) and
    /// de-duplicated case-insensitively. The registry's SteamPath uses forward
    /// slashes ("c:/program files (x86)/steam") while the vdf lists the same
    /// main library with backslashes, so without normalization it appears twice.
    /// </summary>
    internal static IReadOnlyList<string> SteamLibrariesFrom(string steamPath, string? libraryFoldersVdf)
    {
        var libraries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path)
        {
            string? normalized = NormalizePath(path);
            if (normalized is not null && seen.Add(normalized))
            {
                libraries.Add(normalized);
            }
        }

        Add(steamPath);
        if (!string.IsNullOrEmpty(libraryFoldersVdf))
        {
            foreach (string path in ReadVdfValues(libraryFoldersVdf, "path"))
            {
                if (Directory.Exists(path))
                {
                    Add(path);
                }
            }
        }

        return libraries;
    }

    /// <summary>
    /// Candidate DBD folders inside a Steam library. When the app manifest
    /// (appmanifest_381210.acf) exists, its "installdir" names the folder and
    /// is tried first; the default "Dead by Daylight" folder is the fallback.
    /// </summary>
    internal static IEnumerable<string> SteamGameRoots(string library)
    {
        string steamApps = Path.Combine(library, "steamapps");
        string? installDir = null;
        try
        {
            string manifest = Path.Combine(steamApps, $"appmanifest_{SteamAppId}.acf");
            if (File.Exists(manifest))
            {
                installDir = ReadVdfValues(File.ReadAllText(manifest), "installdir").FirstOrDefault();
            }
        }
        catch
        {
            // Unreadable manifest: fall back to the default folder name.
        }

        // A single folder name only (no separators, no "." / "..").
        bool usable = !string.IsNullOrWhiteSpace(installDir) &&
            installDir.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
            installDir.Trim('.').Length > 0;

        var roots = new List<string>(2);
        if (usable)
        {
            roots.Add(Path.Combine(steamApps, "common", installDir!));
        }

        if (!usable || !string.Equals(installDir, DefaultSteamInstallDir, StringComparison.OrdinalIgnoreCase))
        {
            roots.Add(Path.Combine(steamApps, "common", DefaultSteamInstallDir));
        }

        return roots;
    }

    /// <summary>
    /// Values of every <c>"key" "value"</c> pair on its own line in a Valve
    /// KeyValues text file (libraryfolders.vdf, appmanifest_*.acf). Keys match
    /// case-insensitively; backslash escapes ("\\" → "\") are decoded.
    /// </summary>
    internal static IReadOnlyList<string> ReadVdfValues(string content, string key)
    {
        var values = new List<string>();
        foreach (string line in content.Split('\n'))
        {
            List<string> tokens = QuotedTokens(line);
            if (tokens.Count >= 2 && string.Equals(tokens[0], key, StringComparison.OrdinalIgnoreCase))
            {
                values.Add(tokens[1]);
            }
        }

        return values;
    }

    private static List<string> QuotedTokens(string line)
    {
        var tokens = new List<string>(2);
        int i = 0;
        while (i < line.Length)
        {
            int start = line.IndexOf('"', i);
            if (start < 0)
            {
                break;
            }

            var sb = new StringBuilder();
            int j = start + 1;
            bool closed = false;
            for (; j < line.Length; j++)
            {
                char c = line[j];
                if (c == '\\' && j + 1 < line.Length)
                {
                    sb.Append(line[++j]);
                    continue;
                }

                if (c == '"')
                {
                    closed = true;
                    break;
                }

                sb.Append(c);
            }

            if (!closed)
            {
                break;
            }

            tokens.Add(sb.ToString());
            i = j + 1;
        }

        return tokens;
    }

    /// <summary>Full path with native separators and no trailing separator; null when the path is invalid.</summary>
    internal static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch
        {
            return null;
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

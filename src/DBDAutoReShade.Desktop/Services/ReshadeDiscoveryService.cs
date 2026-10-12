using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>Where ReShade was found for a DBD install.</summary>
public sealed record ReshadeLocation(string IniPath, string? HookDllPath, string GameDirectory);

/// <summary>
/// Locates ReShade for a DBD install: ReShade.ini in one of
/// <see cref="ReshadeSearchLocations.CandidateDirectories"/> (next to or just
/// above the game executable, or the game's Binaries\Win64 / WinGDK folder when
/// the configured executable is the launcher) plus the hook DLL
/// (dxgi.dll / d3d11.dll / d3d12.dll) next to it. A ReShade.ini with a hook DLL
/// beside it wins over one without (a stray copy next to the launcher), nearest
/// first; without any hook DLL the nearest ReShade.ini is used. Filesystem only —
/// never touches the game process.
/// </summary>
public static class ReshadeDiscoveryService
{
    private static readonly string[] HookDllNames = { "dxgi.dll", "d3d11.dll", "d3d12.dll" };

    public static ReshadeLocation? Discover(string? dbdExePath)
    {
        ReshadeLocation? firstWithoutHook = null;
        foreach (string ini in ReshadeSearchLocations.FindIniFiles(dbdExePath))
        {
            string dir = Path.GetDirectoryName(ini)!;
            string? dll = HookDllNames
                .Select(n => Path.Combine(dir, n))
                .FirstOrDefault(File.Exists);
            if (dll is not null)
            {
                return new ReshadeLocation(ini, dll, dir);
            }

            firstWithoutHook ??= new ReshadeLocation(ini, null, dir);
        }

        return firstWithoutHook;
    }
}

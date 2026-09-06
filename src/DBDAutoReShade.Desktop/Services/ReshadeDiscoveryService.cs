namespace DBDAutoReShade.Desktop.Services;

/// <summary>Where ReShade was found for a DBD install.</summary>
public sealed record ReshadeLocation(string IniPath, string? HookDllPath, string GameDirectory);

/// <summary>
/// Locates ReShade for a DBD install: ReShade.ini next to (or just above)
/// the game executable plus the hook DLL (dxgi.dll / d3d11.dll / d3d12.dll).
/// Filesystem only — never touches the game process.
/// </summary>
public static class ReshadeDiscoveryService
{
    private static readonly string[] HookDllNames = { "dxgi.dll", "d3d11.dll", "d3d12.dll" };

    public static ReshadeLocation? Discover(string? dbdExePath)
    {
        if (string.IsNullOrWhiteSpace(dbdExePath))
        {
            return null;
        }

        string? dir = Path.GetDirectoryName(dbdExePath);
        for (int level = 0; level < 4 && dir is not null; level++)
        {
            string ini = Path.Combine(dir, "ReShade.ini");
            if (File.Exists(ini))
            {
                string? dll = HookDllNames
                    .Select(n => Path.Combine(dir, n))
                    .FirstOrDefault(File.Exists);
                return new ReshadeLocation(ini, dll, dir);
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}

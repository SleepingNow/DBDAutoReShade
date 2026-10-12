using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

/// <summary>Steam library parsing, normalization and de-duplication (temp folders only).</summary>
public sealed class SteamDiscoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-steam-" + Guid.NewGuid().ToString("N"));

    public SteamDiscoveryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private static string VdfEscape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    [Fact]
    public void MainLibrary_FromRegistryWithForwardSlashes_IsListedOnce()
    {
        string steam = Directory.CreateDirectory(Path.Combine(_dir, "Steam")).FullName;
        string lib2 = Directory.CreateDirectory(Path.Combine(_dir, "SteamLibrary")).FullName;
        string missing = Path.Combine(_dir, "Gone");

        // Registry style: forward slashes, lower case, trailing slash.
        string registrySteamPath = steam.Replace('\\', '/').ToLowerInvariant() + "/";
        string vdf =
            "\"libraryfolders\"\n{\n" +
            "\t\"0\"\n\t{\n" +
            $"\t\t\"path\"\t\t\"{VdfEscape(steam)}\"\n" +
            "\t\t\"label\"\t\t\"\"\n" +
            "\t\t\"apps\"\n\t\t{\n\t\t\t\"381210\"\t\t\"123\"\n\t\t}\n" +
            "\t}\n" +
            "\t\"1\"\n\t{\n" +
            $"\t\t\"path\"\t\t\"{VdfEscape(lib2)}\"\n" +
            "\t}\n" +
            "\t\"2\"\n\t{\n" +
            $"\t\t\"path\"\t\t\"{VdfEscape(missing)}\"\n" +
            "\t}\n" +
            "\t\"3\"\n\t{\n" +
            $"\t\t\"path\"\t\t\"{VdfEscape(lib2.ToUpperInvariant())}\\\\\"\n" +
            "\t}\n}\n";

        IReadOnlyList<string> libs = DbdDiscoveryService.SteamLibrariesFrom(registrySteamPath, vdf);

        Assert.Equal(2, libs.Count);
        Assert.Equal(steam, libs[0], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(lib2, libs[1], StringComparer.OrdinalIgnoreCase);
        Assert.All(libs, l => Assert.DoesNotContain('/', l));
        Assert.All(libs, l => Assert.False(Path.EndsInDirectorySeparator(l)));
    }

    [Fact]
    public void NoVdf_YieldsSteamRootOnly()
    {
        string steam = Directory.CreateDirectory(Path.Combine(_dir, "Steam")).FullName;
        IReadOnlyList<string> libs = DbdDiscoveryService.SteamLibrariesFrom(steam, null);
        Assert.Equal(new[] { steam }, libs);
    }

    [Fact]
    public void ReadVdfValues_DecodesEscapes_AndMatchesKeyCaseInsensitively()
    {
        string content =
            "\"AppState\"\n{\n" +
            "\t\"appid\"\t\t\"381210\"\n" +
            "\t\"installdir\"\t\t\"Dead by Daylight\"\n" +
            "\t\"PATH\"\t\t\"D:\\\\Games\\\\Steam\"\n" +
            "\t\"name\"\t\t\"Say \\\"hi\\\"\"\n" +
            "}\n";

        Assert.Equal(new[] { "Dead by Daylight" }, DbdDiscoveryService.ReadVdfValues(content, "installdir"));
        Assert.Equal(new[] { @"D:\Games\Steam" }, DbdDiscoveryService.ReadVdfValues(content, "path"));
        Assert.Equal(new[] { "Say \"hi\"" }, DbdDiscoveryService.ReadVdfValues(content, "name"));
        Assert.Empty(DbdDiscoveryService.ReadVdfValues(content, "missing"));
    }

    [Fact]
    public void GameRoots_UseManifestInstallDir_ThenDefault()
    {
        string lib = Directory.CreateDirectory(Path.Combine(_dir, "Lib")).FullName;
        string steamApps = Directory.CreateDirectory(Path.Combine(lib, "steamapps")).FullName;
        File.WriteAllText(
            Path.Combine(steamApps, "appmanifest_381210.acf"),
            "\"AppState\"\n{\n\t\"installdir\"\t\t\"DBD Custom\"\n}\n");

        IReadOnlyList<string> roots = DbdDiscoveryService.SteamGameRoots(lib).ToList();
        Assert.Equal(
            new[]
            {
                Path.Combine(steamApps, "common", "DBD Custom"),
                Path.Combine(steamApps, "common", "Dead by Daylight")
            },
            roots);
    }

    [Fact]
    public void GameRoots_WithoutManifest_OrUnsafeInstallDir_UseDefaultOnly()
    {
        string lib = Directory.CreateDirectory(Path.Combine(_dir, "Lib")).FullName;
        string expected = Path.Combine(lib, "steamapps", "common", "Dead by Daylight");
        Assert.Equal(new[] { expected }, DbdDiscoveryService.SteamGameRoots(lib));

        string steamApps = Directory.CreateDirectory(Path.Combine(lib, "steamapps")).FullName;
        string manifest = Path.Combine(steamApps, "appmanifest_381210.acf");

        File.WriteAllText(manifest, "\"AppState\"\n{\n\t\"installdir\"\t\t\"..\"\n}\n");
        Assert.Equal(new[] { expected }, DbdDiscoveryService.SteamGameRoots(lib));

        File.WriteAllText(manifest, "\"AppState\"\n{\n\t\"installdir\"\t\t\"..\\\\..\\\\Windows\"\n}\n");
        Assert.Equal(new[] { expected }, DbdDiscoveryService.SteamGameRoots(lib));

        File.WriteAllText(manifest, "\"AppState\"\n{\n\t\"installdir\"\t\t\"Dead by Daylight\"\n}\n");
        Assert.Equal(new[] { expected }, DbdDiscoveryService.SteamGameRoots(lib));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizePath_RejectsBlank(string? path)
    {
        Assert.Null(DbdDiscoveryService.NormalizePath(path));
    }

    [Fact]
    public void NormalizePath_KeepsDriveRoot()
    {
        Assert.Equal(@"C:\", DbdDiscoveryService.NormalizePath("C:/"));
        Assert.Equal(@"C:\Program Files (x86)\Steam", DbdDiscoveryService.NormalizePath("C:/Program Files (x86)/Steam/"));
    }
}

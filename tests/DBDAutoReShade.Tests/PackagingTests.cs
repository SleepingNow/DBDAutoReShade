using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DBDAutoReShade.Tests;

/// <summary>Runs only when <c>DBD_PUBLISH</c> names a folder to publish into (the publish takes a few minutes).</summary>
public sealed class PublishFactAttribute : FactAttribute
{
    /// <summary>Skips unless the publish folder is set.</summary>
    public PublishFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBD_PUBLISH")))
        {
            Skip = "Opt-in: set DBD_PUBLISH=<output folder> to run the README's publish command and check the package.";
        }
    }
}

/// <summary>
/// The portable package. The README's single-file publish gives the 1.0 layout: one exe that carries the WPF
/// native libraries, its symbols, and a <c>Licenses</c> folder with the credits and both SIL OFL texts. The
/// credits point only at files that ship beside them.
/// </summary>
public sealed class PackagingTests
{
    /// <summary>The WPF native libraries. A single-file publish leaves them loose next to the exe unless it bundles them.</summary>
    private static readonly string[] WpfNativeLibraries =
    [
        "D3DCompiler_47_cor3.dll",
        "PenImc_cor3.dll",
        "PresentationNative_cor3.dll",
        "vcruntime140_cor3.dll",
        "wpfgfx_cor3.dll",
    ];

    /// <summary>What the <c>Licenses</c> folder holds, sorted by name.</summary>
    private static readonly string[] LicenceFiles = ["CREDITS.md", "OFL-Barlow.txt", "OFL-BigShouldersDisplay.txt"];

    private static string AppDir => Path.Combine(RepoRoot(), "src", "DBDAutoReShade.App");

    /// <summary>The <c>Licenses</c> folder of the build output (the App's Content items flow into the test output).</summary>
    private static string ShippedLicences => Path.Combine(AppContext.BaseDirectory, "Licenses");

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "DBDAutoReShade.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string[] FileNames(string dir) =>
        Directory.GetFiles(dir).Select(f => Path.GetFileName(f)).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The source of a shipped licence file: CREDITS.md sits in Assets, the OFL texts beside the fonts.</summary>
    private static string SourceOf(string licenceFile) =>
        licenceFile == "CREDITS.md"
            ? Path.Combine(AppDir, "Assets", licenceFile)
            : Path.Combine(AppDir, "Assets", "Fonts", licenceFile);

    [Fact]
    public void TheAppProject_BundlesTheWpfNativeLibraries_IntoTheSingleFileExe()
    {
        XDocument project = XDocument.Load(Path.Combine(AppDir, "DBDAutoReShade.App.csproj"));

        // Without it, the README's PublishSingleFile command leaves wpfgfx_cor3.dll and the other WPF native
        // libraries loose next to the exe, so an exe shipped like the 1.0 zip (exe + pdbs) cannot start.
        XElement setting = Assert.Single(project.Descendants("IncludeNativeLibrariesForSelfExtract"));
        Assert.Equal("PropertyGroup", setting.Parent!.Name.LocalName);
        Assert.Null(setting.Attribute("Condition"));
        Assert.Null(setting.Parent.Attribute("Condition"));
        Assert.Equal("true", setting.Value.Trim(), ignoreCase: true);
    }

    [Fact]
    public void TheLicencesFolder_ShipsTheCreditsAndBothOflTexts_Unchanged()
    {
        Assert.True(Directory.Exists(ShippedLicences), $"No Licenses folder in the build output ({ShippedLicences}).");
        Assert.Equal(LicenceFiles, FileNames(ShippedLicences));

        foreach (string file in LicenceFiles)
        {
            Assert.Equal(File.ReadAllBytes(SourceOf(file)), File.ReadAllBytes(Path.Combine(ShippedLicences, file)));
        }

        // Each OFL text is the full licence, with its font's copyright notice.
        string barlow = File.ReadAllText(Path.Combine(ShippedLicences, "OFL-Barlow.txt"));
        string bigShoulders = File.ReadAllText(Path.Combine(ShippedLicences, "OFL-BigShouldersDisplay.txt"));
        Assert.StartsWith("Copyright 2017 The Barlow Project Authors", barlow);
        Assert.StartsWith("Copyright 2019 The Big Shoulders Project Authors", bigShoulders);
        Assert.All(new[] { barlow, bigShoulders }, text =>
        {
            Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", text);
            Assert.Contains("TERMINATION", text);
        });
    }

    [Fact]
    public void TheShippedCredits_PointOnlyAtFilesThatShipBesideThem()
    {
        string credits = File.ReadAllText(Path.Combine(ShippedLicences, "CREDITS.md"));

        // Every code span that names a file or a folder must exist next to the shipped CREDITS.md. The pictures,
        // fonts and sound are embedded in the exe, so the package has no Maps\, Fonts\ or Sounds\ folder.
        List<string> references = Regex.Matches(credits, "`([^`]+)`")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(LooksLikePath)
            .ToList();

        Assert.Contains("OFL-Barlow.txt", references);
        Assert.Contains("OFL-BigShouldersDisplay.txt", references);
        Assert.All(references, reference =>
        {
            string target = Path.Combine(ShippedLicences, reference.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(
                File.Exists(target) || Directory.Exists(target),
                $"CREDITS.md points at `{reference}`, which does not ship beside it in the Licenses folder.");
        });

        static bool LooksLikePath(string span) =>
            span.Contains('/') || span.Contains('\\') || Regex.IsMatch(span, @"\.[A-Za-z0-9]{2,4}$");
    }

    [PublishFact]
    public void TheReadmePublishCommand_GivesTheExeAndItsLicencesFolder_WithTheNativeLibrariesBundled()
    {
        string root = RepoRoot();
        const string DocumentedOutput = "-o publish/DBDAutoReShade";
        string command = ReadmePublishCommand(root);
        Assert.StartsWith("dotnet publish ", command);
        Assert.EndsWith(DocumentedOutput, command);

        // The documented command, publishing into a new folder under DBD_PUBLISH instead of the repo's publish\.
        string outDir = Path.Combine(
            Path.GetFullPath(Environment.GetEnvironmentVariable("DBD_PUBLISH")!),
            "DBDAutoReShade-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        string arguments = command["dotnet ".Length..^DocumentedOutput.Length] + $"-o \"{outDir}\"";
        (int exitCode, string output) = RunDotnet(arguments, root, TimeSpan.FromMinutes(15));
        Assert.True(exitCode == 0, $"dotnet {arguments} exited with {exitCode}:\n{output}");
        Assert.DoesNotMatch(@": warning [A-Z]+[0-9]+", output);

        // The folder is the deliverable: the exe, its symbols and Licenses\. No native library ships loose.
        string[] files = FileNames(outDir);
        Assert.Contains("DBDAutoReShade.exe", files);
        Assert.All(files, file => Assert.True(
            file == "DBDAutoReShade.exe" || file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase),
            $"{file} ships loose next to the exe."));
        Assert.Equal(new[] { "Licenses" }, Directory.GetDirectories(outDir).Select(d => Path.GetFileName(d)));
        string licences = Path.Combine(outDir, "Licenses");
        Assert.Equal(LicenceFiles, FileNames(licences));
        foreach (string file in LicenceFiles)
        {
            Assert.Equal(File.ReadAllBytes(SourceOf(file)), File.ReadAllBytes(Path.Combine(licences, file)));
        }

        // The exe carries the WPF native libraries, as the shipped 1.0 exe did; the licences stay outside it.
        IReadOnlyList<SingleFileBundle.Entry> bundled = SingleFileBundle.ReadManifest(Path.Combine(outDir, "DBDAutoReShade.exe"));
        Assert.Contains(bundled, e => e.Path == "DBDAutoReShade.dll");
        foreach (string native in WpfNativeLibraries)
        {
            Assert.Contains(bundled, e => e.Type == SingleFileBundle.NativeBinary
                && string.Equals(e.Path, native, StringComparison.OrdinalIgnoreCase));
        }

        Assert.DoesNotContain(bundled, e => LicenceFiles.Contains(Path.GetFileName(e.Path), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The portable publish command from README.md's "Build from source" section, on one line.</summary>
    private static string ReadmePublishCommand(string root)
    {
        string readme = File.ReadAllText(Path.Combine(root, "README.md"));
        // Inline code (`dotnet publish …`) or a line of a fenced code block.
        Match match = Assert.Single(Regex.Matches(readme, @"(?m)(?:`(dotnet publish [^`]+)`|^\s*(dotnet publish [^`\r\n]+)\s*$)"));
        string command = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return Regex.Replace(command, @"\s+", " ").Trim();
    }

    private static (int ExitCode, string Output) RunDotnet(string arguments, string workingDirectory, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // No MSBuild worker or compiler server outlives the publish holding the redirected output open.
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["UseSharedCompilation"] = "false";
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        var output = new StringBuilder();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet {arguments} did not finish within {timeout}.");
        }

        process.WaitForExit(); // drains the redirected output
        lock (output)
        {
            return (process.ExitCode, output.ToString());
        }
    }
}

/// <summary>
/// Reads the manifest of a .NET single-file bundle (the apphost's bundle header, as written by
/// Microsoft.NET.HostModel's Bundler): the path and type of every file bundled into the exe.
/// </summary>
internal static class SingleFileBundle
{
    /// <summary>The bundle entry type of a native library.</summary>
    public const byte NativeBinary = 2;

    /// <summary>SHA-256 of ".net core bundle": the apphost stores the bundle header's offset in the 8 bytes before it.</summary>
    private static readonly byte[] Signature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
    ];

    /// <summary>One bundled file.</summary>
    public readonly record struct Entry(byte Type, string Path);

    /// <summary>The bundled files of <paramref name="exe"/>.</summary>
    public static IReadOnlyList<Entry> ReadManifest(string exe)
    {
        byte[] data = File.ReadAllBytes(exe);
        int marker = data.AsSpan().IndexOf(Signature);
        Assert.True(marker >= 8, $"{exe} is not a single-file bundle.");
        long headerOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(marker - 8));
        Assert.InRange(headerOffset, 1, data.Length - 1);

        int p = (int)headerOffset;
        uint major = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p));
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p + 8));
        p += 12;
        _ = ReadString(data, ref p); // bundle id
        if (major >= 2)
        {
            p += (4 * sizeof(long)) + sizeof(ulong); // deps.json and runtimeconfig.json locations, flags
        }

        var entries = new List<Entry>(count);
        for (int i = 0; i < count; i++)
        {
            p += 2 * sizeof(long); // offset, size
            if (major >= 6)
            {
                p += sizeof(long); // compressed size
            }

            byte type = data[p++];
            entries.Add(new Entry(type, ReadString(data, ref p)));
        }

        return entries;
    }

    /// <summary>A string with a 7-bit encoded length prefix (BinaryWriter's format).</summary>
    private static string ReadString(byte[] data, ref int p)
    {
        int length = 0;
        for (int shift = 0; ; shift += 7)
        {
            byte b = data[p++];
            length |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }
        }

        string value = Encoding.UTF8.GetString(data, p, length);
        p += length;
        return value;
    }
}

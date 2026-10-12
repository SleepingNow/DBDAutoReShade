using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

/// <summary>Debug Mode set persistence: naming, contents and bounded retention.</summary>
public sealed class DebugCropTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-debug-" + Guid.NewGuid().ToString("N"));

    public DebugCropTests() => Directory.CreateDirectory(_dir);

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

    private static CapturedImage Img(int w, int h) => new(w, h, Enumerable.Repeat((byte)200, w * h * 4).ToArray());

    private static DetectionSampleEvidence Evidence(bool withPreprocessed = true) =>
        new(3, new IntPtr(0x1234))
        {
            MapCrop = Img(8, 4),
            RealmCrop = Img(8, 3),
            PreprocessedMapCrop = withPreprocessed ? Img(16, 8) : null,
            PreprocessedRealmCrop = withPreprocessed ? Img(16, 6) : null,
            RawMapText = "PRESS START",
            RawRealmText = string.Empty,
            PreprocessedMapText = withPreprocessed ? "PRE55 START" : null,
            PreprocessedRealmText = withPreprocessed ? "C0LDW1ND" : null,
            RawMatch = MapMatchResult.Failure("raw-diagnostic"),
            RetryMatch = withPreprocessed ? MapMatchResult.Failure("retry-diagnostic") : null,
            Match = MapMatchResult.Failure("raw-diagnostic")
        };

    private static readonly DetectionResult Failed =
        new(false, null, null, 0.0, 7, "No confident map detected — Global preset.");

    /// <summary>An existing current-format set (summary + two crops), all stamped with <paramref name="writeUtc"/>.</summary>
    private string MakeSet(DateTime writeUtc, int id)
    {
        string key = $"ocr-failure-{writeUtc:yyyyMMdd-HHmmss-fff}-{id:x8}";
        foreach (string suffix in new[] { ".txt", "-map.bmp", "-realm.bmp" })
        {
            string path = Path.Combine(_dir, key + suffix);
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, writeUtc);
        }

        return key;
    }

    private bool SetExists(string key) => Directory.GetFiles(_dir, key + "*").Length > 0;

    [Fact]
    public void Save_WritesCropsAndSummary()
    {
        using DetectionSampleEvidence evidence = Evidence();
        DebugCropSet? set = DebugCropStore.TrySave(_dir, evidence, Failed);

        Assert.NotNull(set);
        Assert.Equal(5, set.Files.Count);
        Assert.Equal(set.SummaryPath, set.Files[0]);
        Assert.All(set.Files, f => Assert.True(File.Exists(f)));
        Assert.All(set.Files, f => Assert.NotNull(DebugCropStore.TryGetSetKey(Path.GetFileName(f))));

        string summary = File.ReadAllText(set.SummaryPath);
        Assert.Contains("PRESS START", summary);
        Assert.Contains("PRE55 START", summary);
        Assert.Contains("C0LDW1ND", summary);
        Assert.Contains("raw-diagnostic", summary);
        Assert.Contains("retry-diagnostic", summary);
        Assert.Contains("No confident map detected", summary);
        Assert.Contains("sample #3", summary);
        Assert.Contains("8x4", summary);
        Assert.Contains("0x1234", summary);
        foreach (string file in set.Files.Skip(1))
        {
            Assert.Contains(Path.GetFileName(file), summary);
        }
    }

    [Fact]
    public void Save_WithoutRetry_WritesRawCropsOnly()
    {
        using DetectionSampleEvidence evidence = Evidence(withPreprocessed: false);
        DebugCropSet? set = DebugCropStore.TrySave(_dir, evidence, Failed);

        Assert.NotNull(set);
        Assert.Equal(3, set.Files.Count); // summary + raw map + raw realm
        Assert.Contains("(not run)", File.ReadAllText(set.SummaryPath));
    }

    [Fact]
    public void Retention_KeepsNewest20Sets_DeletesOldest()
    {
        var keys = new List<string>();
        for (int i = 0; i < 25; i++)
        {
            keys.Add(MakeSet(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i), i));
        }

        // An even older pre-fix single-file set, plus files that are not ours.
        string legacy = Path.Combine(_dir, "ocr-failure-20190101-000000.bmp");
        File.WriteAllText(legacy, "x");
        File.SetLastWriteTimeUtc(legacy, new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        string[] foreign =
        {
            Path.Combine(_dir, "notes.txt"),
            Path.Combine(_dir, "ocr-failure-custom.bmp"),
            Path.Combine(_dir, "ocr-failure-20180101-000000-000-0a0b0c0d-other.bmp")
        };
        foreach (string f in foreign)
        {
            File.WriteAllText(f, "keep");
            File.SetLastWriteTimeUtc(f, new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        using DetectionSampleEvidence evidence = Evidence();
        DebugCropSet? set = DebugCropStore.TrySave(_dir, evidence, Failed);

        Assert.NotNull(set);
        Assert.All(set.Files, f => Assert.True(File.Exists(f))); // the new set is never a victim
        Assert.False(File.Exists(legacy));
        Assert.All(keys.Take(6), k => Assert.False(SetExists(k))); // 6 oldest current-format sets deleted
        Assert.All(keys.Skip(6), k => Assert.True(SetExists(k))); // 19 newest kept
        Assert.All(foreign, f => Assert.True(File.Exists(f))); // unknown names are never deleted

        int sets = Directory.GetFiles(_dir)
            .Select(f => DebugCropStore.TryGetSetKey(Path.GetFileName(f)))
            .Where(k => k is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        Assert.Equal(DebugCropStore.MaxSets, sets);
    }

    [Fact]
    public void FailedSave_DeletesNothing()
    {
        for (int i = 0; i < 25; i++)
        {
            MakeSet(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i), i);
        }

        string[] before = Directory.GetFiles(_dir).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        int writes = 0;
        void FailingWriter(string path, CapturedImage image)
        {
            if (++writes == 2)
            {
                File.WriteAllText(path, "partial");
                throw new IOException("disk full");
            }

            BmpWriter.Save24(path, image, FileMode.CreateNew);
        }

        using DetectionSampleEvidence evidence = Evidence();
        DebugCropSet? set = DebugCropStore.TrySave(_dir, evidence, Failed, writeImage: FailingWriter);

        Assert.Null(set);
        string[] after = Directory.GetFiles(_dir).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(before, after); // no old set removed, no partial new set left behind
    }

    [Fact]
    public void UnwritableDirectory_ReturnsNull()
    {
        string notADirectory = Path.Combine(_dir, "file-in-the-way");
        File.WriteAllText(notADirectory, "x");

        using DetectionSampleEvidence evidence = Evidence();
        Assert.Null(DebugCropStore.TrySave(notADirectory, evidence, Failed));
        Assert.Equal("x", File.ReadAllText(notADirectory));
    }

    [Fact]
    public void SameTimestamp_ProducesCollisionFreeSets()
    {
        var now = new DateTime(2026, 10, 10, 12, 0, 0, 123);
        using DetectionSampleEvidence evidence = Evidence();
        DebugCropSet? first = DebugCropStore.TrySave(_dir, evidence, Failed, now: now);
        DebugCropSet? second = DebugCropStore.TrySave(_dir, evidence, Failed, now: now);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.SummaryPath, second.SummaryPath);
        Assert.Contains("20261010-120000-123", Path.GetFileName(first.SummaryPath));
        Assert.All(first.Files.Concat(second.Files), f => Assert.True(File.Exists(f)));
    }

    [Theory]
    [InlineData("ocr-failure-20261010-123456-789-0a1b2c3d.txt", "ocr-failure-20261010-123456-789-0a1b2c3d")]
    [InlineData("ocr-failure-20261010-123456-789-0a1b2c3d-map.bmp", "ocr-failure-20261010-123456-789-0a1b2c3d")]
    [InlineData("ocr-failure-20261010-123456-789-0a1b2c3d-realm-preprocessed.bmp", "ocr-failure-20261010-123456-789-0a1b2c3d")]
    [InlineData("ocr-failure-20261010-123456.bmp", "ocr-failure-20261010-123456")]
    [InlineData("ocr-failure-custom.bmp", null)]
    [InlineData("ocr-failure-20261010-123456-789-0a1b2c3d-other.bmp", null)]
    [InlineData("ocr-failure-20261010-123456-789-0a1b2c3d.png", null)]
    [InlineData("notes.txt", null)]
    public void SetKey_RecognizesOnlyOurNames(string fileName, string? expected)
    {
        Assert.Equal(expected, DebugCropStore.TryGetSetKey(fileName));
    }
}

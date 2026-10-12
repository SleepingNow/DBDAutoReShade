using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>Files written for one Debug Mode set: the summary (.txt) and every file of the set (summary first).</summary>
internal sealed record DebugCropSet(string SummaryPath, IReadOnlyList<string> Files);

/// <summary>
/// Debug Mode persistence for scans that ended without a confident read. Each
/// such scan writes one "set" for its most recent failed sample (or, when no
/// OCR'd sample failed, its most recent accepted-but-unconfirmed one): the map and
/// realm crops exactly as OCR'd, the preprocessed retry crops when they were
/// produced, and a small .txt with that sample's OCR text and match
/// diagnostics. Local only. Names are collision-free (millisecond timestamp
/// plus a random suffix, files created with <see cref="FileMode.CreateNew"/>).
/// Retention keeps the newest <see cref="MaxSets"/> sets and deletes the
/// oldest — and deletes nothing unless the new set was written completely.
/// Only files that follow this store's naming scheme are ever deleted.
/// </summary>
internal static class DebugCropStore
{
    /// <summary>Maximum number of sets kept (the newest, including the one just saved).</summary>
    public const int MaxSets = 20;

    public const string FilePrefix = "ocr-failure-";

    private const string MapSuffix = "-map";
    private const string RealmSuffix = "-realm";
    private const string PreprocessedMapSuffix = "-map-preprocessed";
    private const string PreprocessedRealmSuffix = "-realm-preprocessed";

    private static readonly string[] KnownSuffixes =
    {
        string.Empty, MapSuffix, RealmSuffix, PreprocessedMapSuffix, PreprocessedRealmSuffix
    };

    // Current sets: ocr-failure-yyyyMMdd-HHmmss-fff-xxxxxxxx; legacy single crops: ocr-failure-yyyyMMdd-HHmmss.
    private static readonly Regex SetKeyPattern = new(
        @"^ocr-failure-\d{8}-\d{6}(?:-\d{3}-[0-9a-f]{8})?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Save <paramref name="sample"/> as a new set in <paramref name="directory"/>
    /// and then apply retention. Never throws: returns null when nothing (or
    /// only part of the set) could be written, in which case any partial files
    /// of this set are removed and no older set is touched.
    /// </summary>
    public static DebugCropSet? TrySave(
        string directory,
        DetectionSampleEvidence sample,
        DetectionResult scanResult,
        int maxSets = MaxSets,
        Action<string, CapturedImage>? writeImage = null,
        DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || sample is null || scanResult is null)
        {
            return null;
        }

        writeImage ??= static (path, image) => BmpWriter.Save24(path, image, FileMode.CreateNew);
        DateTime stamp = now ?? DateTime.Now;
        string setKey = FilePrefix
            + stamp.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N")[..8];

        var created = new List<string>();
        try
        {
            Directory.CreateDirectory(directory);

            var images = new List<(string FileName, CapturedImage Image, string Description)>();
            AddImage(images, setKey + MapSuffix, sample.MapCrop, "map-name crop, as OCR'd");
            AddImage(images, setKey + RealmSuffix, sample.RealmCrop, "realm-name crop, as OCR'd");
            AddImage(images, setKey + PreprocessedMapSuffix, sample.PreprocessedMapCrop, "map-name crop after preprocessing (retry)");
            AddImage(images, setKey + PreprocessedRealmSuffix, sample.PreprocessedRealmCrop, "realm-name crop after preprocessing (retry)");

            var files = new List<string>();
            foreach (var (fileName, image, _) in images)
            {
                string path = Path.Combine(directory, fileName);
                WriteNewFile(path, created, p => writeImage(p, image));
                files.Add(path);
            }

            string summaryPath = Path.Combine(directory, setKey + ".txt");
            string summary = BuildSummary(sample, scanResult, stamp, images);
            WriteNewFile(summaryPath, created, p =>
            {
                using var fs = new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(summary);
            });
            files.Insert(0, summaryPath);

            // Only now that the whole set exists may older sets be removed.
            ApplyRetention(directory, setKey, maxSets);
            return new DebugCropSet(summaryPath, files);
        }
        catch
        {
            foreach (string path in created)
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Best effort: a stray partial file is harmless.
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Keep the newest <paramref name="maxSets"/> sets (always including
    /// <paramref name="keepSetKey"/>) and delete the files of older ones.
    /// Sets are ordered by their newest file's last-write time, then by name.
    /// Best effort; never throws.
    /// </summary>
    internal static void ApplyRetention(string directory, string keepSetKey, int maxSets)
    {
        try
        {
            var sets = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.EnumerateFiles(directory, FilePrefix + "*"))
            {
                string? key = TryGetSetKey(Path.GetFileName(path));
                if (key is null)
                {
                    continue;
                }

                if (!sets.TryGetValue(key, out List<FileInfo>? list))
                {
                    list = new List<FileInfo>();
                    sets[key] = list;
                }

                list.Add(new FileInfo(path));
            }

            var expired = sets
                .Where(kv => !string.Equals(kv.Key, keepSetKey, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(kv => kv.Value.Max(f => f.LastWriteTimeUtc))
                .ThenByDescending(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Skip(Math.Max(0, maxSets - 1));

            foreach (var set in expired)
            {
                foreach (FileInfo file in set.Value)
                {
                    try
                    {
                        file.Delete();
                    }
                    catch
                    {
                        // Best effort.
                    }
                }
            }
        }
        catch
        {
            // Retention must never break detection.
        }
    }

    /// <summary>
    /// The set a debug file belongs to, or null for any file that does not
    /// follow this store's naming scheme (such files are never deleted).
    /// </summary>
    internal static string? TryGetSetKey(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        string extension = Path.GetExtension(fileName);
        if (!extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        Match match = SetKeyPattern.Match(stem);
        if (!match.Success)
        {
            return null;
        }

        string rest = stem[match.Length..];
        return KnownSuffixes.Any(s => string.Equals(rest, s, StringComparison.OrdinalIgnoreCase))
            ? match.Value
            : null;
    }

    private static void AddImage(
        List<(string FileName, CapturedImage Image, string Description)> images,
        string stem, CapturedImage? image, string description)
    {
        if (image is not null)
        {
            images.Add((stem + ".bmp", image, description));
        }
    }

    private static void WriteNewFile(string path, List<string> created, Action<string> write)
    {
        bool existedBefore = File.Exists(path);
        try
        {
            write(path);
        }
        finally
        {
            // Track only files this call created, so a failure never removes someone else's file.
            if (!existedBefore && File.Exists(path))
            {
                created.Add(path);
            }
        }
    }

    internal static string BuildSummary(
        DetectionSampleEvidence sample,
        DetectionResult scanResult,
        DateTime stamp,
        IReadOnlyList<(string FileName, CapturedImage Image, string Description)> images)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("DBD Auto ReShade — Debug Mode: scan ended without a confident read");
        sb.AppendLine(inv, $"Saved:        {stamp:yyyy-MM-dd HH:mm:ss.fff} (local time)");
        sb.AppendLine(scanResult.WasStopped
            ? "Scan outcome: stopped (user stop, DBD exit or shutdown) before a confident read"
            : "Scan outcome: failed — " + (scanResult.FailureReason ?? "no confident read"));
        string which = sample.Accepted
            ? "the most recent sample (accepted but not confirmed; no OCR'd sample failed)"
            : "the most recent failed one";
        sb.AppendLine(inv, $"Samples:      {scanResult.SampleCount} taken; this set is sample #{sample.Index}, {which}");
        sb.AppendLine(inv, $"Window:       0x{sample.Window.ToInt64():X}");
        sb.AppendLine();

        sb.AppendLine("Files:");
        if (images.Count == 0)
        {
            sb.AppendLine("  (no crops were captured)");
        }

        foreach (var (fileName, image, description) in images)
        {
            sb.AppendLine(inv, $"  {fileName}  {image.Width}x{image.Height}  {description}");
        }

        sb.AppendLine();
        AppendText(sb, "OCR text — map crop (raw)", sample.RawMapText);
        AppendText(sb, "OCR text — realm crop (raw)", sample.RawRealmText);
        AppendText(sb, "OCR text — map crop (preprocessed retry)", sample.PreprocessedMapText);
        AppendText(sb, "OCR text — realm crop (preprocessed retry)", sample.PreprocessedRealmText);

        sb.AppendLine(inv, $"Match (raw):                {Describe(sample.RawMatch)}");
        sb.AppendLine(inv, $"Match (preprocessed retry): {Describe(sample.RetryMatch)}");
        sb.AppendLine(inv, $"Sample verdict:             {Describe(sample.Match)}");
        return sb.ToString();
    }

    private static void AppendText(StringBuilder sb, string title, string? text)
    {
        sb.AppendLine(title + ":");
        if (text is null)
        {
            sb.AppendLine("  (not run)");
        }
        else if (string.IsNullOrWhiteSpace(text))
        {
            sb.AppendLine("  (no text recognized)");
        }
        else
        {
            foreach (string line in text.Split('\n'))
            {
                sb.Append("  ").AppendLine(line.TrimEnd('\r'));
            }
        }

        sb.AppendLine();
    }

    private static string Describe(MapMatchResult? match)
    {
        if (match is null)
        {
            return "(not run)";
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{(match.Success ? "match" : "no match")}; realm={match.RealmName ?? "-"}; map={match.MapName ?? "-"}; " +
            $"confidence={match.Confidence:0.0000}; method={match.Method}; diagnostic={match.Diagnostic ?? "-"}");
    }
}

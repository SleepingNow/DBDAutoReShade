using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Core.Services;

/// <summary>Expected shape of the update manifest (endpoint is user-configurable; none is hard-coded).</summary>
public sealed class DatabaseUpdateManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("databaseVersion")]
    public int DatabaseVersion { get; set; }

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}

public sealed record UpdateCheckResult(bool UpdateAvailable, DatabaseUpdateManifest? Manifest, string? Message);

public sealed record DatabaseInstallResult(bool Success, int InstalledVersion, string? Message);

/// <summary>
/// Optional map-database updater: async HTTPS manifest check with a short
/// timeout (never blocks startup), size-capped download, SHA-256 verification,
/// schema and version validation, atomic replacement and last-known-good
/// rollback. Fully offline-capable: any failure keeps the current database.
/// Caller cancellation is propagated as <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class DatabaseUpdateService
{
    public const int ManifestSchemaVersion = 1;

    /// <summary>Largest manifest accepted (it is a few hundred bytes of JSON).</summary>
    public const int MaxManifestBytes = 64 * 1024;

    /// <summary>Largest database download accepted; anything bigger is rejected unread.</summary>
    public const int MaxDatabaseBytes = 5 * 1024 * 1024;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    public static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromSeconds(30);

    private readonly Action<string>? _afterActiveReplaced;

    public DatabaseUpdateService()
    {
    }

    /// <summary>
    /// Test seam: <paramref name="afterActiveReplaced"/> runs with the active path right after
    /// the new file replaced it and before the installed bytes are verified.
    /// </summary>
    internal DatabaseUpdateService(Action<string>? afterActiveReplaced)
    {
        _afterActiveReplaced = afterActiveReplaced;
    }

    /// <summary>
    /// Validates a user-configured manifest URL. Null or blank means "updates disabled" and is
    /// acceptable; anything else must be an absolute https:// URI.
    /// </summary>
    public static bool IsAcceptableManifestUrl(string? url, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        if (!IsHttpsUri(url))
        {
            error = "The update manifest URL must be an absolute https:// address.";
            return false;
        }

        return true;
    }

    private static bool IsHttpsUri(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrEmpty(uri.Host);

    /// <summary>Fetch + parse the manifest; returns "no update" (not an error) when offline.</summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(
        HttpClient httpClient,
        string manifestUrl,
        int currentVersion,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (httpClient is null)
        {
            throw new ArgumentNullException(nameof(httpClient));
        }

        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            return new UpdateCheckResult(false, null, "No update endpoint configured.");
        }

        if (!IsAcceptableManifestUrl(manifestUrl, out string? urlError))
        {
            return new UpdateCheckResult(false, null, urlError);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout ?? DefaultTimeout);

        BoundedDownload download;
        try
        {
            download = await DownloadBoundedAsync(httpClient, manifestUrl.Trim(), MaxManifestBytes, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            // The caller cancelled: that is not "offline" and not a timeout.
            throw new OperationCanceledException("The update check was cancelled.", ex, ct);
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(false, null, "Manifest request timed out.");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, null, $"Update check failed (offline?): {ex.Message}");
        }

        if (download.StatusCode is int status)
        {
            return new UpdateCheckResult(false, null, $"Manifest request failed: {status}.");
        }

        if (download.Bytes is null)
        {
            return new UpdateCheckResult(false, null, $"Manifest is larger than {MaxManifestBytes / 1024} KB; rejected.");
        }

        DatabaseUpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DatabaseUpdateManifest>(DecodeUtf8(download.Bytes), DatabaseJson.Options);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, null, $"Manifest is not valid JSON: {ex.Message}");
        }

        if (manifest is null || manifest.SchemaVersion != ManifestSchemaVersion)
        {
            return new UpdateCheckResult(false, null, "Manifest schema is unsupported.");
        }

        if (string.IsNullOrWhiteSpace(manifest.DownloadUrl) || string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            return new UpdateCheckResult(false, null, "Manifest is missing downloadUrl/sha256.");
        }

        if (!IsHttpsUri(manifest.DownloadUrl))
        {
            return new UpdateCheckResult(false, null, "Manifest downloadUrl must use HTTPS.");
        }

        if (manifest.DatabaseVersion <= currentVersion)
        {
            return new UpdateCheckResult(false, null, "Database is up to date.");
        }

        return new UpdateCheckResult(true, manifest, $"Database v{manifest.DatabaseVersion} available.");
    }

    /// <summary>
    /// Download, hash-verify, schema-validate and atomically install. The "current version"
    /// is taken from the database already installed at <paramref name="activePath"/> (0 when
    /// none); prefer the overload that takes the version actually in use.
    /// </summary>
    public Task<DatabaseInstallResult> DownloadAndInstallAsync(
        HttpClient httpClient,
        DatabaseUpdateManifest manifest,
        string activePath,
        string lastGoodPath,
        TimeSpan? timeout = null,
        CancellationToken ct = default) =>
        DownloadAndInstallAsync(httpClient, manifest, activePath, lastGoodPath,
            ReadInstalledVersion(activePath), timeout, ct);

    /// <summary>
    /// Download (size-capped), hash-verify, schema-validate and atomically install.
    /// The downloaded file's own version must equal <c>manifest.databaseVersion</c> and be
    /// greater than <paramref name="currentVersion"/> (no downgrades, no reinstall loops).
    /// The current active file is snapshotted to <paramref name="lastGoodPath"/> first; if the
    /// snapshot fails nothing is touched. A failure after the active file was replaced rolls
    /// back to the snapshot taken in this run only (never to an older, stale last-good file).
    /// The timeout covers the download only; the local write is never interrupted half-way.
    /// </summary>
    public async Task<DatabaseInstallResult> DownloadAndInstallAsync(
        HttpClient httpClient,
        DatabaseUpdateManifest manifest,
        string activePath,
        string lastGoodPath,
        int currentVersion,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(activePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastGoodPath);

        if (!IsHttpsUri(manifest.DownloadUrl))
        {
            return new DatabaseInstallResult(false, 0, "Manifest downloadUrl must use HTTPS.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            return new DatabaseInstallResult(false, 0, "Manifest is missing sha256.");
        }

        BoundedDownload download;
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            linked.CancelAfter(timeout ?? DefaultDownloadTimeout);
            try
            {
                download = await DownloadBoundedAsync(httpClient, manifest.DownloadUrl.Trim(), MaxDatabaseBytes, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException("The database download was cancelled.", ex, ct);
            }
            catch (OperationCanceledException)
            {
                return new DatabaseInstallResult(false, 0, "Download timed out.");
            }
            catch (Exception ex)
            {
                return new DatabaseInstallResult(false, 0, $"Download failed (offline?): {ex.Message}");
            }
        }

        if (download.StatusCode is int status)
        {
            return new DatabaseInstallResult(false, 0, $"Download failed: {status}.");
        }

        if (download.Bytes is not byte[] bytes)
        {
            return new DatabaseInstallResult(false, 0, $"Downloaded database is larger than {MaxDatabaseBytes / (1024 * 1024)} MB; rejected.");
        }

        string actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new DatabaseInstallResult(false, 0, "Downloaded database failed SHA-256 verification; rejected.");
        }

        if (!RealmDatabaseService.TryParse(DecodeUtf8(bytes), out MapDatabaseFile? parsed, out string? error))
        {
            return new DatabaseInstallResult(false, 0, $"Downloaded database failed validation ({error}); rejected.");
        }

        int version = parsed!.Version;
        if (version != manifest.DatabaseVersion)
        {
            return new DatabaseInstallResult(false, 0,
                $"Downloaded database is v{version} but the manifest announced v{manifest.DatabaseVersion}; rejected.");
        }

        if (version <= currentVersion)
        {
            return new DatabaseInstallResult(false, 0,
                $"Downloaded database v{version} is not newer than the current v{currentVersion}; rejected.");
        }

        // Last point where the caller may cancel; from here the local install runs to completion.
        ct.ThrowIfCancellationRequested();
        return Install(bytes, version, activePath, lastGoodPath);
    }

    /// <summary>Snapshot → durable temp write → atomic replace → verify; rolls back only from this run's snapshot.</summary>
    private DatabaseInstallResult Install(byte[] bytes, int version, string activePath, string lastGoodPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(activePath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch (Exception ex)
        {
            return new DatabaseInstallResult(false, 0, $"Install failed: could not create the database folder ({ex.Message}).");
        }

        // Snapshot the current file as last-known-good. If that fails, abort before touching it:
        // whatever sits in lastGoodPath is then stale and must never be restored.
        bool snapshotTaken = false;
        if (File.Exists(activePath))
        {
            try
            {
                File.Copy(activePath, lastGoodPath, overwrite: true);
                snapshotTaken = true;
            }
            catch (Exception ex)
            {
                return new DatabaseInstallResult(false, 0,
                    $"Install aborted: could not back up the current database ({ex.Message}). Nothing was changed.");
            }
        }

        string tmp = activePath + ".tmp";
        bool activeReplaced = false;
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tmp, activePath, overwrite: true);
            activeReplaced = true;

            _afterActiveReplaced?.Invoke(activePath);
            byte[] written = File.ReadAllBytes(activePath);
            if (!written.AsSpan().SequenceEqual(bytes))
            {
                throw new IOException("The installed database file does not match the downloaded data.");
            }

            return new DatabaseInstallResult(true, version, $"Installed database v{version}.");
        }
        catch (Exception ex)
        {
            TryDeleteFile(tmp);
            if (!activeReplaced)
            {
                return new DatabaseInstallResult(false, 0, $"Install failed ({ex.Message}); the current database was left untouched.");
            }

            return new DatabaseInstallResult(false, 0, $"Install failed ({ex.Message}); {RollBack(activePath, lastGoodPath, snapshotTaken)}");
        }
    }

    private static string RollBack(string activePath, string lastGoodPath, bool snapshotTaken)
    {
        try
        {
            if (snapshotTaken)
            {
                File.Copy(lastGoodPath, activePath, overwrite: true);
                return "rolled back to the previous database.";
            }

            // There was no active file before this run: remove ours so the built-in database is used.
            File.Delete(activePath);
            return "removed the new file; the built-in database will be used.";
        }
        catch (Exception ex)
        {
            return $"rollback failed ({ex.Message}).";
        }
    }

    /// <summary>Version of the valid database at <paramref name="path"/>, or 0 when missing, unreadable or invalid.</summary>
    private static int ReadInstalledVersion(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return 0;
            }

            return RealmDatabaseService.TryParse(File.ReadAllText(path), out MapDatabaseFile? db, out _) ? db!.Version : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Decode UTF-8, dropping a byte-order mark (Encoding.UTF8.GetString would keep it as U+FEFF).</summary>
    private static string DecodeUtf8(byte[] bytes)
    {
        ReadOnlySpan<byte> span = bytes;
        ReadOnlySpan<byte> bom = Encoding.UTF8.Preamble;
        if (span.StartsWith(bom))
        {
            span = span[bom.Length..];
        }

        return Encoding.UTF8.GetString(span);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>
    /// GET <paramref name="url"/> reading at most <paramref name="maxBytes"/>.
    /// <see cref="BoundedDownload.StatusCode"/> is set for a non-success response;
    /// <see cref="BoundedDownload.Bytes"/> is null when the body is too large.
    /// </summary>
    private static async Task<BoundedDownload> DownloadBoundedAsync(HttpClient httpClient, string url, int maxBytes, CancellationToken ct)
    {
        using HttpResponseMessage response = await httpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new BoundedDownload(null, (int)response.StatusCode);
        }

        if (response.Content.Headers.ContentLength is long declared && declared > maxBytes)
        {
            return new BoundedDownload(null, null);
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return new BoundedDownload(null, null);
            }

            buffer.Write(chunk, 0, read);
        }

        return new BoundedDownload(buffer.ToArray(), null);
    }

    private readonly record struct BoundedDownload(byte[]? Bytes, int? StatusCode);
}

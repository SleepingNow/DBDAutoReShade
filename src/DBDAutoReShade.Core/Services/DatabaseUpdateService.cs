using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

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
/// timeout (never blocks startup), SHA-256 verification, schema validation,
/// atomic replacement and last-known-good rollback. Fully offline-capable:
/// any failure keeps the current database.
/// </summary>
public sealed class DatabaseUpdateService
{
    public const int ManifestSchemaVersion = 1;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

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

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout ?? DefaultTimeout);

        string json;
        try
        {
            using var response = await httpClient.GetAsync(manifestUrl, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(false, null, $"Manifest request failed: {(int)response.StatusCode}.");
            }

            json = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new UpdateCheckResult(false, null, "Manifest request timed out.");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, null, $"Update check failed (offline?): {ex.Message}");
        }

        DatabaseUpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DatabaseUpdateManifest>(json, DatabaseJson.Options);
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

        if (!manifest.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
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
    /// Download, hash-verify, schema-validate and atomically install.
    /// Keeps a last-known-good copy; any failure rolls back to it.
    /// </summary>
    public async Task<DatabaseInstallResult> DownloadAndInstallAsync(
        HttpClient httpClient,
        DatabaseUpdateManifest manifest,
        string activePath,
        string lastGoodPath,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(manifest);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));

        byte[] bytes;
        try
        {
            using var response = await httpClient.GetAsync(manifest.DownloadUrl, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new DatabaseInstallResult(false, 0, $"Download failed: {(int)response.StatusCode}.");
            }

            bytes = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DatabaseInstallResult(false, 0, "Download timed out.");
        }
        catch (Exception ex)
        {
            return new DatabaseInstallResult(false, 0, $"Download failed (offline?): {ex.Message}");
        }

        string actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new DatabaseInstallResult(false, 0, "Downloaded database failed SHA-256 verification; rejected.");
        }

        string json = System.Text.Encoding.UTF8.GetString(bytes);
        if (!RealmDatabaseService.TryParse(json, out var parsed, out string? error))
        {
            return new DatabaseInstallResult(false, 0, $"Downloaded database failed validation ({error}); rejected.");
        }

        try
        {
            string? dir = Path.GetDirectoryName(activePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Snapshot current as last-known-good before replacing.
            if (File.Exists(activePath))
            {
                File.Copy(activePath, lastGoodPath, overwrite: true);
            }

            string tmp = activePath + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, linked.Token).ConfigureAwait(false);
            File.Move(tmp, activePath, overwrite: true);
            return new DatabaseInstallResult(true, parsed!.Version, $"Installed database v{parsed.Version}.");
        }
        catch (Exception ex)
        {
            // Roll back to last-known-good.
            try
            {
                if (File.Exists(lastGoodPath))
                {
                    File.Copy(lastGoodPath, activePath, overwrite: true);
                }
            }
            catch
            {
                // Best effort.
            }

            return new DatabaseInstallResult(false, 0, $"Install failed and was rolled back: {ex.Message}");
        }
    }
}

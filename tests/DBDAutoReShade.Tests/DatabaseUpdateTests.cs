using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class DatabaseUpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-update-" + Guid.NewGuid().ToString("N"));
    private string Active => Path.Combine(_dir, "mapDatabase.json");
    private string LastGood => Path.Combine(_dir, "mapDatabase.last-good.json");

    public DatabaseUpdateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.GetFiles(_dir))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private const string ValidDb = """{"schemaVersion": 1, "version": 2, "realms": [{"name": "Coldwind Farm", "maps": ["Fractured Cowshed"]}]}""";

    private static string Db(int version) =>
        $$"""{"schemaVersion": 1, "version": {{version}}, "realms": [{"name": "Coldwind Farm", "maps": ["Fractured Cowshed"]}]}""";

    private static string Sha256Of(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static DatabaseUpdateManifest ManifestFor(string body, int databaseVersion) => new()
    {
        SchemaVersion = 1,
        DatabaseVersion = databaseVersion,
        DownloadUrl = "https://example.test/db.json",
        Sha256 = Sha256Of(body)
    };

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new LambdaHandler(handler));

    private static HttpClient Serving(string body) =>
        Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    private sealed class LambdaHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(fn(request));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>Body of unknown length (no Content-Length), so only the streaming cap can stop it.</summary>
    private sealed class UnknownLengthContent(int length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[length]).AsTask();

        protected override bool TryComputeLength(out long len)
        {
            len = 0;
            return false;
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern DatabaseUpdateService WithAfterReplaceHook(Action<string>? afterActiveReplaced);

    [Fact]
    public async Task ValidNewerDatabase_IsInstalled()
    {
        string url = "https://example.test/db.json";
        var manifest = new { schemaVersion = 1, databaseVersion = 2, downloadUrl = url, sha256 = Sha256Of(ValidDb) };
        var svc = new DatabaseUpdateService();
        using var http = Client(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                req.RequestUri!.AbsoluteUri == url ? ValidDb : JsonSerializer.Serialize(manifest),
                Encoding.UTF8, "application/json")
        });

        UpdateCheckResult check = await svc.CheckForUpdateAsync(http, "https://example.test/manifest.json", currentVersion: 1);
        Assert.True(check.UpdateAvailable);
        DatabaseInstallResult installed = await svc.DownloadAndInstallAsync(http, check.Manifest!, Active, LastGood);
        Assert.True(installed.Success);
        Assert.Equal(2, installed.InstalledVersion);
        Assert.True(RealmDatabaseService.TryParse(await File.ReadAllTextAsync(Active), out _, out _));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task OlderDatabase_IsNotOffered()
    {
        var manifest = new { schemaVersion = 1, databaseVersion = 1, downloadUrl = "https://example.test/db.json", sha256 = "AA" };
        var svc = new DatabaseUpdateService();
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(manifest), Encoding.UTF8, "application/json")
        });

        UpdateCheckResult check = await svc.CheckForUpdateAsync(http, "https://example.test/manifest.json", currentVersion: 1);
        Assert.False(check.UpdateAvailable);
    }

    [Fact]
    public async Task WrongHash_IsRejected_ActiveUntouched()
    {
        await File.WriteAllTextAsync(Active, ValidDb);
        var manifest = new DatabaseUpdateManifest
        {
            SchemaVersion = 1,
            DatabaseVersion = 3,
            DownloadUrl = "https://example.test/db.json",
            Sha256 = new string('0', 64)
        };
        var svc = new DatabaseUpdateService();
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ValidDb, Encoding.UTF8, "application/json")
        });

        DatabaseInstallResult r = await svc.DownloadAndInstallAsync(http, manifest, Active, LastGood);
        Assert.False(r.Success);
        Assert.Contains("SHA-256", r.Message);
        Assert.Equal(ValidDb, await File.ReadAllTextAsync(Active));
    }

    [Fact]
    public async Task InvalidSchema_IsRejected()
    {
        const string bad = """{"schemaVersion": 1, "version": 1, "realms": []}""";
        var manifest = new DatabaseUpdateManifest
        {
            SchemaVersion = 1,
            DatabaseVersion = 5,
            DownloadUrl = "https://example.test/db.json",
            Sha256 = Sha256Of(bad)
        };
        var svc = new DatabaseUpdateService();
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(bad, Encoding.UTF8, "application/json")
        });

        DatabaseInstallResult r = await svc.DownloadAndInstallAsync(http, manifest, Active, LastGood);
        Assert.False(r.Success);
        Assert.False(File.Exists(Active));
    }

    [Fact]
    public async Task OfflineCheck_FailsGracefully()
    {
        var svc = new DatabaseUpdateService();
        using var http = new HttpClient(new LambdaHandler(_ => throw new HttpRequestException("no network")));
        UpdateCheckResult check = await svc.CheckForUpdateAsync(http, "https://example.test/manifest.json", currentVersion: 1);
        Assert.False(check.UpdateAvailable);
        Assert.NotNull(check.Message);
    }

    [Fact]
    public async Task NonHttpsDownload_IsRefused()
    {
        var manifest = new { schemaVersion = 1, databaseVersion = 9, downloadUrl = "http://example.test/db.json", sha256 = "AA" };
        var svc = new DatabaseUpdateService();
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(manifest), Encoding.UTF8, "application/json")
        });

        UpdateCheckResult check = await svc.CheckForUpdateAsync(http, "https://example.test/manifest.json", currentVersion: 1);
        Assert.False(check.UpdateAvailable);
    }

    // ---- B3: manifest URL must be HTTPS ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.test/manifest.json")]
    [InlineData("HTTPS://Example.Test/manifest.json")]
    [InlineData("  https://example.test/manifest.json  ")]
    public void AcceptableManifestUrls(string? url)
    {
        Assert.True(DatabaseUpdateService.IsAcceptableManifestUrl(url, out string? error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("http://example.test/manifest.json")]
    [InlineData("ftp://example.test/manifest.json")]
    [InlineData("file:///C:/manifest.json")]
    [InlineData("example.test/manifest.json")]
    [InlineData("/manifest.json")]
    [InlineData("https://")]
    [InlineData("not a url")]
    public void UnacceptableManifestUrls(string url)
    {
        Assert.False(DatabaseUpdateService.IsAcceptableManifestUrl(url, out string? error));
        Assert.Contains("https", error);
    }

    [Fact]
    public async Task NonHttpsManifestUrl_IsRefused_WithoutAnyRequest()
    {
        int requests = 0;
        var manifest = new { schemaVersion = 1, databaseVersion = 9, downloadUrl = "https://example.test/db.json", sha256 = "AA" };
        var svc = new DatabaseUpdateService();
        using var http = Client(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(manifest), Encoding.UTF8, "application/json")
            };
        });

        UpdateCheckResult check = await svc.CheckForUpdateAsync(http, "http://example.test/manifest.json", currentVersion: 1);

        Assert.False(check.UpdateAvailable);
        Assert.Null(check.Manifest);
        Assert.Contains("https", check.Message);
        Assert.Equal(0, requests);
    }

    // ---- B4: version integrity ----

    [Fact]
    public async Task DownloadedVersion_MustMatchManifest()
    {
        string body = Db(2);
        var svc = new DatabaseUpdateService();
        using var http = Serving(body);

        DatabaseInstallResult r = await svc.DownloadAndInstallAsync(http, ManifestFor(body, databaseVersion: 5), Active, LastGood, currentVersion: 1);

        Assert.False(r.Success);
        Assert.Contains("v2", r.Message);
        Assert.Contains("v5", r.Message);
        Assert.False(File.Exists(Active));
    }

    [Fact]
    public async Task DownloadedVersion_NotNewerThanCurrent_IsRejected()
    {
        string body = Db(3);
        var svc = new DatabaseUpdateService();
        using var http = Serving(body);

        DatabaseInstallResult r = await svc.DownloadAndInstallAsync(http, ManifestFor(body, databaseVersion: 3), Active, LastGood, currentVersion: 4);

        Assert.False(r.Success);
        Assert.Contains("not newer", r.Message);
        Assert.False(File.Exists(Active));
    }

    [Fact]
    public async Task LegacyOverload_UsesInstalledVersion_NoDowngradeOrReinstall()
    {
        File.WriteAllText(Active, Db(3));
        var svc = new DatabaseUpdateService();

        foreach (int offered in new[] { 2, 3 })
        {
            string body = Db(offered);
            using var http = Serving(body);
            DatabaseInstallResult r = await svc.DownloadAndInstallAsync(http, ManifestFor(body, offered), Active, LastGood);
            Assert.False(r.Success);
            Assert.Equal(Db(3), File.ReadAllText(Active));
        }

        string newer = Db(4);
        using var http4 = Serving(newer);
        DatabaseInstallResult ok = await svc.DownloadAndInstallAsync(http4, ManifestFor(newer, 4), Active, LastGood);
        Assert.True(ok.Success);
        Assert.Equal(4, ok.InstalledVersion);
    }

    // ---- B5: BOM ----

    [Fact]
    public async Task DatabaseWithUtf8Bom_IsAccepted()
    {
        byte[] body = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(ValidDb)];
        var manifest = new DatabaseUpdateManifest
        {
            SchemaVersion = 1,
            DatabaseVersion = 2,
            DownloadUrl = "https://example.test/db.json",
            Sha256 = Sha256Of(body)
        };
        var svc = new DatabaseUpdateService();
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });

        DatabaseInstallResult r = await svc.DownloadAndInstallAsync(http, manifest, Active, LastGood, currentVersion: 1);

        Assert.True(r.Success, r.Message);
        Assert.Equal(2, r.InstalledVersion);
        var db = new RealmDatabaseService();
        await db.LoadAsync(Active);
        Assert.True(db.LoadedFromOverride);
        Assert.Equal(2, db.Version);
    }

    [Fact]
    public async Task ManifestWithUtf8Bom_IsAccepted()
    {
        string json = JsonSerializer.Serialize(new { schemaVersion = 1, databaseVersion = 2, downloadUrl = "https://example.test/db.json", sha256 = "AA" });
        byte[] body = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(json)];
        var svc = new DatabaseUpdateService();
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });

        UpdateCheckResult check = await svc.CheckForUpdateAsync(http, "https://example.test/manifest.json", currentVersion: 1);

        Assert.True(check.UpdateAvailable, check.Message);
    }

    // ---- B6: size cap, cancellation, install/rollback ----

    [Fact]
    public async Task OversizedDownload_IsRejected_ByDeclaredLength()
    {
        var manifest = ManifestFor(ValidDb, 2);
        var svc = new DatabaseUpdateService();
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[DatabaseUpdateService.MaxDatabaseBytes + 1])
        });

        DatabaseInstallResult r = await svc.DownloadAndInstallAsync(http, manifest, Active, LastGood, currentVersion: 1);

        Assert.False(r.Success);
        Assert.Contains("larger than", r.Message);
        Assert.False(File.Exists(Active));
    }

    [Fact]
    public async Task OversizedDownload_IsRejected_WhileStreaming()
    {
        var manifest = ManifestFor(ValidDb, 2);
        var svc = new DatabaseUpdateService();
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(DatabaseUpdateService.MaxDatabaseBytes + 1)
        });

        DatabaseInstallResult r = await svc.DownloadAndInstallAsync(http, manifest, Active, LastGood, currentVersion: 1);

        Assert.False(r.Success);
        Assert.Contains("larger than", r.Message);
        Assert.False(File.Exists(Active));
    }

    [Fact]
    public async Task OversizedManifest_IsRejected()
    {
        var svc = new DatabaseUpdateService();
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(DatabaseUpdateService.MaxManifestBytes + 1)
        });

        UpdateCheckResult check = await svc.CheckForUpdateAsync(http, "https://example.test/manifest.json", currentVersion: 1);

        Assert.False(check.UpdateAvailable);
        Assert.Contains("larger than", check.Message);
    }

    [Fact]
    public async Task CallerCancellation_OfCheck_IsPropagated_NotReportedAsOffline()
    {
        var svc = new DatabaseUpdateService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var http = new HttpClient(new HangingHandler());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.CheckForUpdateAsync(http, "https://example.test/manifest.json", 1, TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task CallerCancellation_OfDownload_IsPropagated_NotReportedAsOffline()
    {
        var svc = new DatabaseUpdateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        using var http = new HttpClient(new HangingHandler());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.DownloadAndInstallAsync(http, ManifestFor(ValidDb, 2), Active, LastGood, 1, TimeSpan.FromSeconds(30), cts.Token));
        Assert.False(File.Exists(Active));
    }

    [Fact]
    public async Task Timeout_IsReportedAsTimeout_NotThrown()
    {
        var svc = new DatabaseUpdateService();
        using var http = new HttpClient(new HangingHandler());

        UpdateCheckResult check = await svc.CheckForUpdateAsync(http, "https://example.test/manifest.json", 1, TimeSpan.FromMilliseconds(50));
        DatabaseInstallResult install = await svc.DownloadAndInstallAsync(http, ManifestFor(ValidDb, 2), Active, LastGood, 1, TimeSpan.FromMilliseconds(50));

        Assert.Contains("timed out", check.Message);
        Assert.Contains("timed out", install.Message);
    }

    [Fact]
    public async Task SnapshotFailure_AbortsBeforeTouchingActive_AndNeverRestoresStaleLastGood()
    {
        File.WriteAllText(Active, Db(2));
        File.WriteAllText(LastGood, Db(1)); // stale snapshot from an earlier run
        File.SetAttributes(LastGood, FileAttributes.ReadOnly); // so this run's snapshot copy fails
        string body = Db(3);
        var svc = new DatabaseUpdateService();
        using var http = Serving(body);

        DatabaseInstallResult r = await svc.DownloadAndInstallAsync(http, ManifestFor(body, 3), Active, LastGood, currentVersion: 2);

        Assert.False(r.Success);
        Assert.Contains("Nothing was changed", r.Message);
        Assert.Equal(Db(2), File.ReadAllText(Active)); // not replaced by the stale v1
        Assert.Equal(Db(1), File.ReadAllText(LastGood));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task FirstInstall_WriteFailure_DoesNotResurrectStaleLastGood()
    {
        File.WriteAllText(LastGood, Db(1)); // stale; no active file exists
        Directory.CreateDirectory(Active + ".tmp"); // the temp write fails
        string body = Db(3);
        var svc = new DatabaseUpdateService();
        using var http = Serving(body);

        DatabaseInstallResult r = await svc.DownloadAndInstallAsync(http, ManifestFor(body, 3), Active, LastGood, currentVersion: 1);

        Assert.False(r.Success);
        Assert.False(File.Exists(Active));
    }

    [Fact]
    public async Task ReplaceFailure_LeavesActiveUntouched_AndRemovesTempFile()
    {
        File.WriteAllText(Active, Db(2));
        string body = Db(3);
        var svc = new DatabaseUpdateService();
        using var http = Serving(body);

        DatabaseInstallResult r;
        // Open without FILE_SHARE_DELETE so the atomic replace cannot happen.
        using (new FileStream(Active, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            r = await svc.DownloadAndInstallAsync(http, ManifestFor(body, 3), Active, LastGood, currentVersion: 2);
        }

        Assert.False(r.Success);
        Assert.Contains("left untouched", r.Message);
        Assert.Equal(Db(2), File.ReadAllText(Active));
        Assert.Equal(Db(2), File.ReadAllText(LastGood)); // this run's snapshot
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task FailureAfterReplace_RollsBackToThisRunsSnapshot_NotStaleLastGood()
    {
        File.WriteAllText(Active, Db(2));
        File.WriteAllText(LastGood, Db(1)); // stale; must be overwritten by this run's snapshot first
        string body = Db(3);
        var svc = WithAfterReplaceHook(path => File.WriteAllText(path, "corrupted on disk"));
        using var http = Serving(body);

        DatabaseInstallResult r = await svc.DownloadAndInstallAsync(http, ManifestFor(body, 3), Active, LastGood, currentVersion: 2);

        Assert.False(r.Success);
        Assert.Contains("rolled back", r.Message);
        Assert.Equal(Db(2), File.ReadAllText(Active));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task FailureAfterReplace_WithoutPreviousActive_RemovesNewFile_IgnoringStaleLastGood()
    {
        File.WriteAllText(LastGood, Db(1)); // stale; no active file exists
        string body = Db(3);
        var svc = WithAfterReplaceHook(_ => throw new IOException("simulated disk error"));
        using var http = Serving(body);

        DatabaseInstallResult r = await svc.DownloadAndInstallAsync(http, ManifestFor(body, 3), Active, LastGood, currentVersion: 1);

        Assert.False(r.Success);
        Assert.Contains("simulated disk error", r.Message);
        Assert.False(File.Exists(Active)); // neither the new file nor the stale v1
        Assert.Equal(Db(1), File.ReadAllText(LastGood));
    }

    [Fact]
    public async Task SuccessfulInstall_SnapshotsPreviousActive()
    {
        File.WriteAllText(Active, Db(2));
        string body = Db(3);
        var svc = new DatabaseUpdateService();
        using var http = Serving(body);

        DatabaseInstallResult r = await svc.DownloadAndInstallAsync(http, ManifestFor(body, 3), Active, LastGood, currentVersion: 2);

        Assert.True(r.Success, r.Message);
        Assert.Equal(body, File.ReadAllText(Active));
        Assert.Equal(Db(2), File.ReadAllText(LastGood));
    }
}

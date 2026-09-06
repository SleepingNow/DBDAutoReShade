using System.Net;
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
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private const string ValidDb = """{"schemaVersion": 1, "version": 2, "realms": [{"name": "Coldwind Farm", "maps": ["Fractured Cowshed"]}]}""";

    private static string Sha256Of(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new LambdaHandler(handler));

    private sealed class LambdaHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(fn(request));
    }

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
}

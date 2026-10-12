using System.Security.Cryptography;
using System.Text;

namespace DBDAutoReShade.App.Infrastructure;

/// <summary>
/// One running instance per user session, via a named mutex. The mutex is
/// only held open (never owned), so it has no thread affinity: it exists as
/// long as the first instance keeps this guard, and Windows removes it when
/// that process exits, even after a crash.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>Per-session name (Local\): another user's session may run its own instance.</summary>
    public const string DefaultName = @"Local\DBDAutoReShade.SingleInstance";

    private readonly Mutex _mutex;

    private SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// The mutex name for a data folder: <see cref="DefaultName"/> for the
    /// normal one (<paramref name="dataDirOverride"/> null), otherwise a name
    /// derived from the development override folder (see
    /// <c>AppConfigService.DataDirEnvironmentVariable</c>), so a test instance
    /// on a temp folder runs next to the user's real one while two instances on
    /// the same folder (compared case-insensitively) still exclude each other.
    /// </summary>
    public static string NameFor(string? dataDirOverride)
    {
        if (string.IsNullOrWhiteSpace(dataDirOverride))
        {
            return DefaultName;
        }

        string folder = dataDirOverride.Trim().TrimEnd('\\', '/').ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(folder));
        return $"{DefaultName}.{Convert.ToHexString(hash, 0, 8)}";
    }

    /// <summary>
    /// Returns the guard when this is the first instance, or null when another
    /// instance already holds <paramref name="name"/>. A name that exists but
    /// can't be opened counts as held: an elevated instance's mutex denies a
    /// normal one access (UnauthorizedAccessException), and another kind of
    /// object may have taken the name (WaitHandleCannotBeOpenedException).
    /// </summary>
    public static SingleInstanceGuard? TryAcquire(string name = DefaultName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name, out createdNew);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return null;
        }

        if (createdNew)
        {
            return new SingleInstanceGuard(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose() => _mutex.Dispose();
}

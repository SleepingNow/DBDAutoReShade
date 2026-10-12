using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// Real RegisterHotKey round-trips; the only tests that register system-wide
/// hotkeys (the controller tests use <see cref="ControllerHarness.FakeHotkeyService"/>).
/// The combos, Ctrl+Alt+F21 and Ctrl+Alt+F22, are outside this app's own
/// ReShade shortcut pool (Ctrl+Alt+Shift + F13–F24/0–9, which the running app
/// sends to DBD; a registration only catches its exact modifiers) and use keys
/// ordinary keyboards don't have, so a test run neither swallows a preset
/// switch nor takes a hotkey the user is likely to have bound. Test processes
/// running at the same time (an IDE run next to a command-line run) take turns
/// through <see cref="RealHotkeyTurn"/>. Regression tests for "Start returns
/// before registration" and "Stop doesn't wait for unregister".
/// </summary>
public sealed class GlobalHotkeyServiceTests : IDisposable
{
    private readonly RealHotkeyTurn _turn = RealHotkeyTurn.Take();
    private readonly List<GlobalHotkeyService> _services = new();

    private static DetectionHotkeyConfig Combo(string key) =>
        new() { Key = key, Ctrl = true, Alt = true };

    private GlobalHotkeyService NewService()
    {
        var svc = new GlobalHotkeyService();
        _services.Add(svc);
        return svc;
    }

    public void Dispose()
    {
        try
        {
            foreach (GlobalHotkeyService svc in _services)
            {
                svc.Dispose();
            }
        }
        finally
        {
            _turn.Dispose(); // only once nothing is registered any more
        }
    }

    [Fact]
    public void TestCombos_AreOutsideTheAppsShortcutPool()
    {
        foreach (string key in new[] { "F21", "F22" })
        {
            Assert.True(HotkeyParser.TryParse(Combo(key), out int vk, out int mods));
            Assert.Equal(HotkeyParser.ModControl | HotkeyParser.ModAlt, mods); // no Shift: never a pool combo
            Assert.False(ReshadeConfigurationService.IsInShortcutPool(
                new ReshadeShortcutConfig { VirtualKey = vk, Ctrl = true, Alt = true, Shift = false }));
        }
    }

    [Fact]
    public async Task Turn_KeepsOtherTestRunsOut_UntilReleased()
    {
        // Same named mutex, another owner (as another test process would be).
        Assert.False(await Task.Run(() => TryTakeAndRelease(RealHotkeyTurn.MutexName, TimeSpan.Zero)));

        _turn.Dispose();
        Assert.True(await Task.Run(() => TryTakeAndRelease(RealHotkeyTurn.MutexName, TimeSpan.FromSeconds(30))));
    }

    private static bool TryTakeAndRelease(string name, TimeSpan wait)
    {
        using var mutex = new Mutex(false, name);
        bool owned;
        try
        {
            owned = mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        if (owned)
        {
            mutex.ReleaseMutex();
        }

        return owned;
    }

    [Fact]
    public void ControllerHarness_RegistersNoHotkeyWithWindows()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dbd-hotkey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (ControllerHarness h = ControllerHarness.Create(
                       dir, new ControllerHarnessOptions { Configure = c => c.DetectionHotkey = Combo("F21") }))
            {
                Assert.Equal("F21", h.Hotkey.RegisteredHotkey?.Key); // registered as far as the controller knows…
                Assert.Null(h.Controller.HotkeyError);
                Assert.Null(NewService().Start(Combo("F21"))); // …but Windows still has the combo free
            }
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    [Fact]
    public void RegistrationModifiers_AlwaysIncludeNoRepeat()
    {
        Assert.Equal(0x4000u, GlobalHotkeyService.BuildRegistrationModifiers(0));
        Assert.Equal(
            (uint)(HotkeyParser.ModControl | HotkeyParser.ModAlt | 0x4000),
            GlobalHotkeyService.BuildRegistrationModifiers(HotkeyParser.ModControl | HotkeyParser.ModAlt));
    }

    [Fact]
    public void Start_ReportsRegistrationResultSynchronously()
    {
        GlobalHotkeyService owner = NewService();
        Assert.Null(owner.Start(Combo("F21")));
        Assert.True(owner.IsRegistered);
        Assert.Equal("F21", owner.RegisteredHotkey?.Key);

        // A second registration of the same combo must fail right away, with
        // the error returned by Start (not only in LastError later).
        GlobalHotkeyService other = NewService();
        string? error = other.Start(Combo("F21"));
        Assert.Equal(GlobalHotkeyService.AlreadyInUseError, error);
        Assert.False(other.IsRegistered);
        Assert.Null(other.RegisteredHotkey);
        Assert.Equal(error, other.LastError);
    }

    [Fact]
    public void ReRegisteringSameCombo_TwiceInARow_Succeeds()
    {
        GlobalHotkeyService svc = NewService();
        Assert.Null(svc.Start(Combo("F22")));
        Assert.Null(svc.Start(Combo("F22")));
        Assert.Null(svc.Start(Combo("F22")));
        Assert.True(svc.IsRegistered);

        // Windows really holds it for us: nobody else can take it.
        Assert.NotNull(NewService().Start(Combo("F22")));
    }

    [Fact]
    public void Stop_UnregistersBeforeReturning()
    {
        GlobalHotkeyService first = NewService();
        Assert.Null(first.Start(Combo("F22")));

        first.Stop();
        Assert.False(first.IsRegistered);
        Assert.Null(first.RegisteredHotkey);

        // Immediately available to another registration.
        GlobalHotkeyService second = NewService();
        Assert.Null(second.Start(Combo("F22")));
        Assert.True(second.IsRegistered);
    }

    [Fact]
    public void ParseFailure_KeepsCurrentHotkeyRegistered()
    {
        GlobalHotkeyService svc = NewService();
        Assert.Null(svc.Start(Combo("F22")));

        string? error = svc.Start(new DetectionHotkeyConfig { Key = "W" });
        Assert.Equal(HotkeyParser.ModifierRequiredError, error);
        Assert.True(svc.IsRegistered);
        Assert.Equal("F22", svc.RegisteredHotkey?.Key);
        Assert.Null(svc.LastError);

        // Still registered with Windows.
        Assert.NotNull(NewService().Start(Combo("F22")));
    }

    [Fact]
    public void Dispose_ReleasesHotkey()
    {
        var svc = new GlobalHotkeyService();
        Assert.Null(svc.Start(Combo("F21")));
        svc.Dispose();
        Assert.False(svc.IsRegistered);
        Assert.Throws<ObjectDisposedException>(() => svc.Start(Combo("F21")));

        Assert.Null(NewService().Start(Combo("F21")));
    }
}

/// <summary>
/// A turn for tests that register real system-wide hotkeys, shared by every
/// test process of this suite on the machine (a named mutex in the session).
/// A mutex belongs to the thread that took it, and xUnit may run a test's
/// constructor and Dispose on different threads, so a dedicated thread holds it.
/// </summary>
internal sealed class RealHotkeyTurn : IDisposable
{
    internal const string MutexName = @"Local\DBDAutoReShade.Tests.RealHotkeys";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(2);

    private readonly ManualResetEventSlim _done = new(false);
    private readonly Thread _holder;
    private int _disposed;

    private RealHotkeyTurn(Thread holder) => _holder = holder;

    /// <summary>Waits for the turn (at most two minutes); a turn left by a crashed process is taken over.</summary>
    public static RealHotkeyTurn Take()
    {
        using var taken = new ManualResetEventSlim(false);
        Exception? failure = null;
        RealHotkeyTurn? turn = null;
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(false, MutexName);
            bool owned;
            try
            {
                owned = mutex.WaitOne(WaitLimit);
            }
            catch (AbandonedMutexException)
            {
                owned = true; // its holder died mid-test: the turn is ours now
            }
            catch (Exception ex)
            {
                failure = ex;
                taken.Set();
                return;
            }

            if (!owned)
            {
                failure = new TimeoutException($"Another test run held '{MutexName}' for over {WaitLimit.TotalMinutes} minutes.");
                taken.Set();
                return;
            }

            taken.Set();
            turn!._done.Wait();
            mutex.ReleaseMutex();
        })
        {
            IsBackground = true,
            Name = "RealHotkeyTurn"
        };
        turn = new RealHotkeyTurn(holder);
        holder.Start();
        taken.Wait();
        if (failure is not null)
        {
            throw new InvalidOperationException("Could not take the real-hotkey test turn.", failure);
        }

        return turn;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _done.Set();
        if (_holder.Join(TimeSpan.FromSeconds(5)))
        {
            _done.Dispose();
        }
    }
}

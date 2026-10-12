using DBDAutoReShade.Desktop.Services;
using ProcessQuery = DBDAutoReShade.Desktop.Services.DbdProcessService.ProcessQuery;
using ProcessSighting = DBDAutoReShade.Desktop.Services.DbdProcessService.ProcessSighting;

namespace DBDAutoReShade.Tests;

/// <summary>
/// DBD process monitor: query-limited path lookup (never MainModule), path
/// cached per PID, PID-change events, re-entrancy guard.
/// </summary>
public sealed class DbdProcessServiceTests
{
    /// <summary>Scriptable process table standing in for GetProcessesByName + OpenProcess.</summary>
    private sealed class FakeProcesses
    {
        public List<ProcessSighting> Live { get; } = new();
        public Dictionary<int, string?> Paths { get; } = new();
        public HashSet<int> Dead { get; } = new();
        public List<int> Queried { get; } = new();
        public int FindCalls;
        public Func<bool>? BeforeFind { get; set; }

        public ProcessSighting? Find(Func<int, bool> accept)
        {
            Interlocked.Increment(ref FindCalls);
            BeforeFind?.Invoke();
            foreach (ProcessSighting s in Live)
            {
                if (accept(s.ProcessId))
                {
                    return s;
                }
            }

            return null;
        }

        public ProcessQuery Query(int pid)
        {
            Queried.Add(pid);
            return new ProcessQuery(!Dead.Contains(pid), Paths.GetValueOrDefault(pid));
        }

        public DbdProcessService Create() => new(60_000, Find, Query);
    }

    [Fact]
    public void ImagePath_OfOwnProcess_MatchesProcessPath()
    {
        string? path = DbdProcessService.GetProcessImagePath(Environment.ProcessId);
        Assert.NotNull(path);
        Assert.Equal(Environment.ProcessPath, path, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImagePath_OfInvalidPid_IsNull()
    {
        Assert.Null(DbdProcessService.GetProcessImagePath(0));
        Assert.Null(DbdProcessService.GetProcessImagePath(-5));
        Assert.Null(DbdProcessService.GetProcessImagePath(0x7FFFFFF0));
    }

    [Fact]
    public void Snapshot_DoesNotThrow()
    {
        var snap = DbdProcessService.Snapshot();
        Assert.Equal(snap.Running, snap.ProcessId is not null);
    }

    [Fact]
    public void PidChange_WithSameWindow_RaisesStatusChangedWithNewPid()
    {
        var fake = new FakeProcesses();
        fake.Live.Add(new ProcessSighting(100, new IntPtr(0x10)));
        fake.Paths[100] = @"C:\Games\DBD\old.exe";
        fake.Paths[200] = @"C:\Games\DBD\new.exe";
        using DbdProcessService svc = fake.Create();
        var events = new List<DbdStatusEventArgs>();
        svc.StatusChanged += (_, e) => events.Add(e);

        svc.PollOnce();
        Assert.Single(events);
        Assert.Equal(100, events[0].ProcessId);

        // Crash-and-restart between polls: same window handle value, new PID.
        fake.Live[0] = new ProcessSighting(200, new IntPtr(0x10));
        svc.PollOnce();

        Assert.Equal(2, events.Count);
        Assert.True(events[1].IsRunning);
        Assert.Equal(200, events[1].ProcessId);
        Assert.Equal(@"C:\Games\DBD\new.exe", events[1].ExePath);
        Assert.Equal(200, svc.ProcessId);
    }

    [Fact]
    public void ExePath_IsReadOncePerPid_NotEveryPoll()
    {
        var fake = new FakeProcesses();
        fake.Live.Add(new ProcessSighting(100, new IntPtr(0x10)));
        fake.Paths[100] = @"C:\Games\DBD\dbd.exe";
        using DbdProcessService svc = fake.Create();

        for (int i = 0; i < 5; i++)
        {
            svc.PollOnce();
        }

        Assert.Equal(new[] { 100 }, fake.Queried);
        Assert.Equal(@"C:\Games\DBD\dbd.exe", svc.ExePath);

        fake.Live[0] = new ProcessSighting(300, new IntPtr(0x10));
        svc.PollOnce();
        svc.PollOnce();
        Assert.Equal(new[] { 100, 300 }, fake.Queried);
    }

    [Fact]
    public void UnchangedPoll_RaisesNothing_WindowChangeRaises()
    {
        var fake = new FakeProcesses();
        fake.Live.Add(new ProcessSighting(100, new IntPtr(0x10)));
        using DbdProcessService svc = fake.Create();
        int count = 0;
        svc.StatusChanged += (_, _) => count++;

        svc.PollOnce();
        svc.PollOnce();
        Assert.Equal(1, count);

        fake.Live[0] = new ProcessSighting(100, new IntPtr(0x20));
        svc.PollOnce();
        Assert.Equal(2, count);
        Assert.Equal(new IntPtr(0x20), svc.MainWindowHandle);
    }

    [Fact]
    public void ProcessGone_ClearsState()
    {
        var fake = new FakeProcesses();
        fake.Live.Add(new ProcessSighting(100, new IntPtr(0x10)));
        fake.Paths[100] = @"C:\Games\DBD\dbd.exe";
        using DbdProcessService svc = fake.Create();
        DbdStatusEventArgs? last = null;
        svc.StatusChanged += (_, e) => last = e;

        svc.PollOnce();
        fake.Live.Clear();
        svc.PollOnce();

        Assert.NotNull(last);
        Assert.False(last.IsRunning);
        Assert.Null(last.ProcessId);
        Assert.Null(last.ExePath);
        Assert.False(svc.IsRunning);
        Assert.Null(svc.ExePath);
        Assert.Null(svc.ProcessId);
    }

    [Fact]
    public void ExitedCandidate_IsSkipped()
    {
        var fake = new FakeProcesses();
        fake.Live.Add(new ProcessSighting(100, new IntPtr(0x10)));
        fake.Live.Add(new ProcessSighting(200, new IntPtr(0x20)));
        fake.Dead.Add(100);
        using DbdProcessService svc = fake.Create();

        svc.PollOnce();
        Assert.Equal(200, svc.ProcessId);
    }

    [Fact]
    public async Task ConcurrentPoll_IsSkipped_WhileOneIsRunning()
    {
        var fake = new FakeProcesses();
        fake.Live.Add(new ProcessSighting(100, new IntPtr(0x10)));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fake.BeforeFind = () =>
        {
            entered.Set();
            return release.Wait(TimeSpan.FromSeconds(10));
        };
        using DbdProcessService svc = fake.Create();
        int events = 0;
        svc.StatusChanged += (_, _) => Interlocked.Increment(ref events);

        var first = Task.Run(svc.PollOnce);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        svc.PollOnce(); // overlaps the in-flight poll: must return without probing
        Assert.Equal(1, Volatile.Read(ref fake.FindCalls));

        release.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, events);

        fake.BeforeFind = null;
        svc.PollOnce(); // guard released afterwards
        Assert.Equal(2, Volatile.Read(ref fake.FindCalls));
    }

    [Fact]
    public void ThrowingSubscriber_DoesNotBreakMonitor()
    {
        var fake = new FakeProcesses();
        fake.Live.Add(new ProcessSighting(100, new IntPtr(0x10)));
        using DbdProcessService svc = fake.Create();
        svc.StatusChanged += (_, _) => throw new InvalidOperationException("subscriber bug");

        svc.PollOnce();
        Assert.True(svc.IsRunning);
        fake.Live.Clear();
        svc.PollOnce();
        Assert.False(svc.IsRunning);
    }

    [Fact]
    public void Disposed_PollRaisesNothing()
    {
        var fake = new FakeProcesses();
        fake.Live.Add(new ProcessSighting(100, new IntPtr(0x10)));
        DbdProcessService svc = fake.Create();
        int count = 0;
        svc.StatusChanged += (_, _) => count++;

        svc.Dispose();
        svc.PollOnce();
        Assert.Equal(0, count);
        Assert.Throws<ObjectDisposedException>(svc.Start);
    }
}

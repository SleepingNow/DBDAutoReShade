using System.Diagnostics;
using System.Runtime.InteropServices;
using DBDAutoReShade.Desktop.Interop;
using Microsoft.Win32.SafeHandles;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>
/// Lightweight DBD lifecycle monitor: polls the process list every 2s.
/// Near-zero CPU; no capture, no OCR, no window hooks while idle.
/// Process access is query-limited only: the exe path comes from
/// QueryFullProcessImageName (PROCESS_QUERY_LIMITED_INFORMATION), read once
/// per new PID and cached — never from the module list, which would open the
/// game with PROCESS_VM_READ.
/// </summary>
public sealed class DbdProcessService : IDbdMonitor
{
    /// <summary>Known DBD process names (Steam / WinGDK layouts).</summary>
    public static readonly string[] ProcessNames =
    {
        "DeadByDaylight-Win64-Shipping",
        "DeadByDaylight",
        "DeadByDaylight-WinGDK-Shipping"
    };

    /// <summary>A live DBD process found by enumeration (PID + main window; no handle kept).</summary>
    internal readonly record struct ProcessSighting(int ProcessId, IntPtr MainWindow);

    /// <summary>Result of one query-limited probe of a PID.</summary>
    internal readonly record struct ProcessQuery(bool Alive, string? ImagePath);

    /// <summary>Immutable monitor state, swapped as a whole so readers on any thread see a consistent snapshot.</summary>
    private sealed record MonitorState(bool Running, IntPtr Window, string? ExePath, int? ProcessId);

    private static readonly MonitorState NotRunning = new(false, IntPtr.Zero, null, null);

    private readonly System.Timers.Timer _timer;
    private readonly Func<Func<int, bool>, ProcessSighting?> _find;
    private readonly Func<int, ProcessQuery> _query;
    private volatile MonitorState _state = NotRunning;
    private volatile bool _disposed;
    private int _polling;

    public DbdProcessService(double pollIntervalMs = 2000)
        : this(pollIntervalMs, FindFirstProcess, QueryProcess)
    {
    }

    /// <summary>Test seam: <paramref name="find"/> enumerates candidates in priority order and returns the first one the predicate accepts.</summary>
    internal DbdProcessService(
        double pollIntervalMs,
        Func<Func<int, bool>, ProcessSighting?> find,
        Func<int, ProcessQuery> query)
    {
        _find = find ?? throw new ArgumentNullException(nameof(find));
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _timer = new System.Timers.Timer(pollIntervalMs) { AutoReset = true };
        _timer.Elapsed += (_, _) => PollOnce();
    }

    public bool IsRunning => _state.Running;

    public IntPtr MainWindowHandle => _state.Window;

    public string? ExePath => _state.ExePath;

    public int? ProcessId => _state.ProcessId;

    public event EventHandler<DbdStatusEventArgs>? StatusChanged;

    public void Start()
    {
        ThrowIfDisposed();
        PollOnce();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    /// <summary>Immediate synchronous snapshot (used by discovery and tests).</summary>
    public static (bool Running, IntPtr Window, string? ExePath, int? ProcessId) Snapshot()
    {
        MonitorState state = Resolve(FindFirstProcess, QueryProcess, NotRunning);
        return (state.Running, state.Window, state.ExePath, state.ProcessId);
    }

    /// <summary>
    /// Full Win32 image path of a process via a query-limited handle, or null
    /// when the process is gone or access is denied. Never reads process memory.
    /// </summary>
    public static string? GetProcessImagePath(int processId) =>
        processId > 0 ? QueryProcess(processId).ImagePath : null;

    /// <summary>
    /// One poll. Guarded against re-entrancy: if the previous poll (timer tick
    /// or <see cref="Start"/>) is still running, this one is skipped.
    /// </summary>
    internal void PollOnce()
    {
        if (_disposed || Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
        {
            return;
        }

        try
        {
            MonitorState previous = _state;
            MonitorState next = Resolve(_find, _query, previous);
            _state = next;

            // A PID change with the same running flag is a crash-and-restart
            // between polls: subscribers must treat it as a new process.
            bool changed = next.Running != previous.Running ||
                next.Window != previous.Window ||
                next.ProcessId != previous.ProcessId;
            if (changed && !_disposed)
            {
                try
                {
                    StatusChanged?.Invoke(this, new DbdStatusEventArgs(next.Running, next.Window, next.ExePath, next.ProcessId));
                }
                catch
                {
                    // Event subscribers must never break the monitor.
                }
            }
        }
        catch
        {
            // Enumeration failures are transient; the next tick retries.
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    /// <summary>
    /// Picks the first live DBD process. The PID already known from
    /// <paramref name="previous"/> is accepted without opening it again (its
    /// path is cached); any other PID is probed once with a query-limited handle.
    /// </summary>
    private static MonitorState Resolve(
        Func<Func<int, bool>, ProcessSighting?> find,
        Func<int, ProcessQuery> query,
        MonitorState previous)
    {
        string? path = null;
        ProcessSighting? found = find(pid =>
        {
            if (previous.Running && previous.ProcessId == pid)
            {
                path = previous.ExePath;
                return true;
            }

            ProcessQuery probe = query(pid);
            if (!probe.Alive)
            {
                return false;
            }

            // Path unreadable (e.g. access denied): keep the last known one.
            path = probe.ImagePath ?? previous.ExePath;
            return true;
        });

        return found is { } sighting
            ? new MonitorState(true, sighting.MainWindow, path, sighting.ProcessId)
            : NotRunning;
    }

    /// <summary>
    /// Enumerates DBD processes by name (priority order) and returns the first
    /// one <paramref name="accept"/> approves. Every <see cref="Process"/>
    /// object is disposed, including the ones after an early return.
    /// </summary>
    private static ProcessSighting? FindFirstProcess(Func<int, bool> accept)
    {
        foreach (string name in ProcessNames)
        {
            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            try
            {
                foreach (Process p in procs)
                {
                    try
                    {
                        int pid = p.Id;
                        if (!accept(pid))
                        {
                            continue;
                        }

                        // MainWindowHandle enumerates top-level windows; it does not open the process.
                        return new ProcessSighting(pid, p.MainWindowHandle);
                    }
                    catch
                    {
                        // Race: process exited during inspection.
                    }
                }
            }
            finally
            {
                foreach (Process p in procs)
                {
                    p.Dispose();
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Probes a PID with PROCESS_QUERY_LIMITED_INFORMATION only: liveness via
    /// GetExitCodeProcess and the image path via QueryFullProcessImageName.
    /// </summary>
    private static ProcessQuery QueryProcess(int processId)
    {
        try
        {
            using SafeProcessHandle handle = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
            if (handle.IsInvalid)
            {
                // ERROR_INVALID_PARAMETER: no such process. Anything else (access
                // denied) still means it exists; presence is enough.
                bool gone = Marshal.GetLastPInvokeError() == NativeMethods.ERROR_INVALID_PARAMETER;
                return new ProcessQuery(!gone, null);
            }

            if (NativeMethods.GetExitCodeProcess(handle, out uint exitCode) && exitCode != NativeMethods.STILL_ACTIVE)
            {
                return new ProcessQuery(false, null);
            }

            return new ProcessQuery(true, ReadImagePath(handle));
        }
        catch
        {
            return new ProcessQuery(true, null);
        }
    }

    private static string? ReadImagePath(SafeProcessHandle handle)
    {
        foreach (int capacity in new[] { 260, 1024, 32768 })
        {
            var buffer = new char[capacity];
            uint size = (uint)capacity;
            if (NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                return size == 0 ? null : new string(buffer, 0, (int)size);
            }

            if (Marshal.GetLastPInvokeError() != NativeMethods.ERROR_INSUFFICIENT_BUFFER)
            {
                return null;
            }
        }

        return null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DbdProcessService));
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _timer.Stop();
            _timer.Dispose();
        }
    }
}

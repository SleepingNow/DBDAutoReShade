using System.Diagnostics;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>
/// Lightweight DBD lifecycle monitor: polls the process list every 2s.
/// Near-zero CPU; no capture, no OCR, no window hooks while idle.
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

    private readonly System.Timers.Timer _timer;
    private bool _disposed;
    private bool _running;
    private IntPtr _mainWindow;
    private string? _exePath;
    private int? _processId;

    public DbdProcessService(double pollIntervalMs = 2000)
    {
        _timer = new System.Timers.Timer(pollIntervalMs) { AutoReset = true };
        _timer.Elapsed += (_, _) => Poll();
    }

    public bool IsRunning => _running;

    public IntPtr MainWindowHandle => _mainWindow;

    public string? ExePath => _exePath;

    public int? ProcessId => _processId;

    public event EventHandler<DbdStatusEventArgs>? StatusChanged;

    public void Start()
    {
        ThrowIfDisposed();
        Poll();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    /// <summary>Immediate synchronous snapshot (used by discovery and tests).</summary>
    public static (bool Running, IntPtr Window, string? ExePath, int? ProcessId) Snapshot()
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

            foreach (Process p in procs)
            {
                try
                {
                    if (p.HasExited)
                    {
                        continue;
                    }

                    string? path = null;
                    try
                    {
                        path = p.MainModule?.FileName;
                    }
                    catch
                    {
                        // Access denied (different integrity level); process presence is enough.
                    }

                    return (true, p.MainWindowHandle, path, p.Id);
                }
                catch
                {
                    // Race: process exited during inspection.
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        return (false, IntPtr.Zero, null, null);
    }

    private void Poll()
    {
        if (_disposed)
        {
            return;
        }

        var (running, window, exe, pid) = Snapshot();
        bool changed = running != _running || window != _mainWindow;
        _running = running;
        _mainWindow = window;
        _processId = pid;
        if (exe is not null)
        {
            _exePath = exe;
        }
        else if (!running)
        {
            _exePath = null;
        }

        if (changed)
        {
            try
            {
                StatusChanged?.Invoke(this, new DbdStatusEventArgs(running, window, _exePath, _processId));
            }
            catch
            {
                // Event subscribers must never break the monitor.
            }
        }
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

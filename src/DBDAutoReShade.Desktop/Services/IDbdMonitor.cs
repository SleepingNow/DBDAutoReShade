namespace DBDAutoReShade.Desktop.Services;

/// <summary>Status snapshot of the DBD process (carried by <see cref="IDbdMonitor"/>).</summary>
public sealed class DbdStatusEventArgs(bool running, IntPtr window, string? exePath, int? processId) : EventArgs
{
    public bool IsRunning { get; } = running;
    public IntPtr MainWindowHandle { get; } = window;
    public string? ExePath { get; } = exePath;

    /// <summary>
    /// PID of the DBD process this snapshot describes (null when not running).
    /// A PID change while <see cref="IsRunning"/> stays true means DBD was
    /// restarted between polls (a new process), not just a new window.
    /// </summary>
    public int? ProcessId { get; } = processId;
}

/// <summary>Abstraction over DBD process monitoring (test seam; production: <see cref="DbdProcessService"/>).</summary>
public interface IDbdMonitor : IDisposable
{
    bool IsRunning { get; }

    IntPtr MainWindowHandle { get; }

    string? ExePath { get; }

    int? ProcessId { get; }

    /// <summary>
    /// Raised when the running flag, the main window handle or the PID
    /// changes. May fire on a background (timer) thread.
    /// </summary>
    event EventHandler<DbdStatusEventArgs>? StatusChanged;

    void Start();

    void Stop();
}

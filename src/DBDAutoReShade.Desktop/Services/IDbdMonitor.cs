namespace DBDAutoReShade.Desktop.Services;

/// <summary>Status snapshot of the DBD process (carried by <see cref="IDbdMonitor"/>).</summary>
public sealed class DbdStatusEventArgs(bool running, IntPtr window, string? exePath, int? processId) : EventArgs
{
    public bool IsRunning { get; } = running;
    public IntPtr MainWindowHandle { get; } = window;
    public string? ExePath { get; } = exePath;
    public int? ProcessId { get; } = processId;
}

/// <summary>Abstraction over DBD process monitoring (test seam; production: <see cref="DbdProcessService"/>).</summary>
public interface IDbdMonitor : IDisposable
{
    bool IsRunning { get; }

    IntPtr MainWindowHandle { get; }

    string? ExePath { get; }

    int? ProcessId { get; }

    event EventHandler<DbdStatusEventArgs>? StatusChanged;

    void Start();

    void Stop();
}

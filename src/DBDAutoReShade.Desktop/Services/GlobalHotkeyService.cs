using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Interop;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>
/// Global detection hotkey via RegisterHotKey on the calling thread's message
/// queue (no window handle, no hooks, no polling). A dedicated background
/// thread pumps messages; the callback fires on that thread.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0xD8D0;

    private readonly object _gate = new();
    private Thread? _pumpThread;
    private uint _pumpThreadId;
    private DetectionHotkeyConfig? _registered;
    private bool _disposed;

    public bool IsRegistered => _registered is not null;

    public DetectionHotkeyConfig? RegisteredHotkey => _registered;

    public event EventHandler? HotkeyPressed;

    /// <summary>Register (or re-register) the global hotkey. Returns an error message on failure.</summary>
    public string? Start(DetectionHotkeyConfig hotkey)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!HotkeyParser.TryParse(hotkey, out int vk, out int mods))
            {
                return $"Unsupported hotkey '{hotkey}'. Use F1–F24, A–Z or 0–9 with optional Ctrl/Shift/Alt.";
            }

            StopLocked();

            _pumpThread = new Thread(() => Pump(vk, mods))
            {
                IsBackground = true,
                Name = "DBDReShade-HotkeyPump"
            };
            _pumpThread.Start();

            // Wait briefly for the pump thread to register.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (_pumpThreadId == 0 && sw.ElapsedMilliseconds < 3000)
            {
                Thread.Sleep(10);
            }

            _registered = new DetectionHotkeyConfig { Key = hotkey.Key, Ctrl = hotkey.Ctrl, Shift = hotkey.Shift, Alt = hotkey.Alt };
            return null;
        }
    }

    /// <summary>The pump thread itself reports registration failure via this callback-fed field.</summary>
    public string? LastError { get; private set; }

    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        if (_pumpThread is not null && _pumpThreadId != 0)
        {
            try
            {
                NativeMethods.PostThreadMessage(_pumpThreadId, 0x0012 /*WM_QUIT*/, UIntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                // Best effort.
            }
        }

        _pumpThread = null;
        _pumpThreadId = 0;
        _registered = null;
        LastError = null;
    }

    private void Pump(int vk, int mods)
    {
        _pumpThreadId = NativeMethods.GetCurrentThreadId();
        bool ok = false;
        try
        {
            ok = NativeMethods.RegisterHotKey(IntPtr.Zero, HotkeyId, (uint)mods, (uint)vk);
            if (!ok)
            {
                LastError = "Could not register the global hotkey (it may be in use by another program).";
                return;
            }

            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.Message == WinMsg.WmHotkey && msg.WParam == (UIntPtr)HotkeyId)
                {
                    try
                    {
                        HotkeyPressed?.Invoke(this, EventArgs.Empty);
                    }
                    catch
                    {
                        // Subscriber errors must not kill the pump.
                    }
                }
            }
        }
        finally
        {
            if (ok)
            {
                try
                {
                    NativeMethods.UnregisterHotKey(IntPtr.Zero, HotkeyId);
                }
                catch
                {
                    // Best effort.
                }
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GlobalHotkeyService));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                StopLocked();
            }
        }
    }
}

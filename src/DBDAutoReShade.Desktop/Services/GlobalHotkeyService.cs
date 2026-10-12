using System.Runtime.InteropServices;
using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Interop;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>
/// A global hotkey via RegisterHotKey on a dedicated thread's message queue (no
/// window handle, no hooks, no polling). The callback fires on that pump
/// thread. The app runs two instances side by side (the detection hotkey and
/// the Global preset hotkey), each with its own pump thread and hotkey id.
/// <para>
/// Synchronous contract: <see cref="Start"/> returns only after the pump
/// thread has actually called RegisterHotKey, with its real outcome (on
/// failure nothing stays registered and the thread has exited).
/// <see cref="Stop"/> joins the pump thread, so UnregisterHotKey has completed
/// when it returns and the same combo can be registered again immediately.
/// </para>
/// </summary>
public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    /// <summary>The error <see cref="Start"/> returns when another program already holds the combo.</summary>
    public const string AlreadyInUseError = "Could not register the global hotkey: it is already in use by another program.";

    // RegisterHotKey ids: each service registers under its own, so the app's
    // two hotkeys (detection and Global preset) never share an id.
    private const int FirstHotkeyId = 0xD8D0;
    private static int _instanceCount = -1;
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    /// <summary>One pump thread and the registration it owns.</summary>
    private sealed class PumpSession(int virtualKey, uint modifiers, DetectionHotkeyConfig hotkey)
    {
        public int VirtualKey { get; } = virtualKey;
        public uint Modifiers { get; } = modifiers;
        public DetectionHotkeyConfig Hotkey { get; } = hotkey;

        /// <summary>Set by the pump thread once RegisterHotKey has returned.</summary>
        public ManualResetEventSlim Completed { get; } = new(false);

        public Thread? Thread { get; set; }

        /// <summary>Published only after the thread's message queue exists (so WM_QUIT can't be lost).</summary>
        public volatile uint ThreadId;

        /// <summary>True while the hotkey is registered by this session's thread.</summary>
        public volatile bool IsActive;

        // Written by the pump thread before Completed.Set(); guarded by lock(this).
        public bool Succeeded;
        public int Win32Error;
        public bool Abandoned;

        // Pump-thread only: UnregisterHotKey already ran (Stop called from a HotkeyPressed subscriber).
        public bool UnregisteredEarly;
    }

    private readonly object _gate = new();
    private readonly int _hotkeyId = FirstHotkeyId + (Interlocked.Increment(ref _instanceCount) & 0xFF);
    private volatile PumpSession? _session;
    private bool _disposed;

    /// <summary>True only while a hotkey is actually registered with Windows.</summary>
    public bool IsRegistered => _session?.IsActive == true;

    public DetectionHotkeyConfig? RegisteredHotkey => _session is { IsActive: true } s ? s.Hotkey : null;

    public event EventHandler? HotkeyPressed;

    /// <summary>
    /// Register (or re-register) the global hotkey. Returns null on success or
    /// a user-facing error. A hotkey that fails to PARSE leaves the currently
    /// registered one untouched; a hotkey that fails to REGISTER leaves nothing
    /// registered (the previous one was already released to make room).
    /// </summary>
    public string? Start(DetectionHotkeyConfig hotkey)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!HotkeyParser.TryParse(hotkey, out int vk, out int mods, out string? parseError))
            {
                return parseError ?? $"Unsupported hotkey '{hotkey}'.";
            }

            StopLocked();

            var copy = new DetectionHotkeyConfig { Key = hotkey.Key, Ctrl = hotkey.Ctrl, Shift = hotkey.Shift, Alt = hotkey.Alt };
            var session = new PumpSession(vk, BuildRegistrationModifiers(mods), copy);
            var thread = new Thread(() => Pump(session))
            {
                IsBackground = true,
                Name = "DBDReShade-HotkeyPump"
            };
            session.Thread = thread;
            thread.Start();

            if (!session.Completed.Wait(RegistrationTimeout))
            {
                lock (session)
                {
                    // The thread checks this flag before reporting; if it registers
                    // late it unregisters again and exits instead of lingering.
                    session.Abandoned = !session.Completed.IsSet;
                }
            }

            string? error;
            lock (session)
            {
                error = session.Abandoned
                    ? "Timed out registering the global hotkey."
                    : session.Succeeded ? null : DescribeRegistrationError(session.Win32Error);
            }

            if (error is null)
            {
                _session = session;
                LastError = null;
                return null;
            }

            // Failed: the pump thread exits on its own; make sure it is gone.
            thread.Join(StopTimeout);
            LastError = error;
            return error;
        }
    }

    /// <summary>
    /// Error from the most recent registration attempt (also returned
    /// synchronously by <see cref="Start"/>); null while registered or stopped.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>Unregisters the hotkey and joins the pump thread (bounded wait).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    /// <summary>RegisterHotKey modifiers: the parsed Ctrl/Alt/Shift bits plus MOD_NOREPEAT (no auto-repeat while held).</summary>
    internal static uint BuildRegistrationModifiers(int modifiers) =>
        (uint)(modifiers | HotkeyParser.ModNoRepeat);

    private void StopLocked()
    {
        PumpSession? session = _session;
        _session = null;
        LastError = null;
        if (session is null)
        {
            return;
        }

        Thread? thread = session.Thread;
        bool onPumpThread = ReferenceEquals(thread, Thread.CurrentThread);
        if (onPumpThread)
        {
            // Called from a HotkeyPressed subscriber: we ARE the registering
            // thread, so unregister synchronously (joining ourselves would hang).
            TryUnregister(_hotkeyId);
            session.IsActive = false;
            session.UnregisteredEarly = true;
        }

        uint threadId = session.ThreadId;
        if (threadId != 0)
        {
            try
            {
                NativeMethods.PostThreadMessage(threadId, WinMsg.WmQuit, UIntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                // Best effort.
            }
        }

        if (!onPumpThread && thread is not null)
        {
            try
            {
                thread.Join(StopTimeout);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private void Pump(PumpSession session)
    {
        // Create this thread's message queue BEFORE publishing the id, so a
        // WM_QUIT posted by Stop can never be lost.
        NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE);
        session.ThreadId = NativeMethods.GetCurrentThreadId();

        bool ok = NativeMethods.RegisterHotKey(IntPtr.Zero, _hotkeyId, session.Modifiers, (uint)session.VirtualKey);
        int error = ok ? 0 : Marshal.GetLastPInvokeError();
        lock (session)
        {
            if (session.Abandoned)
            {
                if (ok)
                {
                    TryUnregister(_hotkeyId);
                }

                return;
            }

            session.Succeeded = ok;
            session.Win32Error = error;
            session.IsActive = ok;
            session.Completed.Set();
        }

        if (!ok)
        {
            return;
        }

        try
        {
            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.Message == WinMsg.WmHotkey && msg.WParam == (UIntPtr)_hotkeyId)
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
            session.IsActive = false;
            if (!session.UnregisteredEarly)
            {
                TryUnregister(_hotkeyId);
            }
        }
    }

    private static void TryUnregister(int hotkeyId)
    {
        try
        {
            NativeMethods.UnregisterHotKey(IntPtr.Zero, hotkeyId);
        }
        catch
        {
            // Best effort.
        }
    }

    private static string DescribeRegistrationError(int win32Error) =>
        win32Error == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED
            ? AlreadyInUseError
            : $"Could not register the global hotkey (Windows error {win32Error}).";

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

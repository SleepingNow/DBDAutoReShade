using System.Runtime.InteropServices;
using DBDAutoReShade.Desktop.Interop;
using Microsoft.Win32.SafeHandles;

namespace DBDAutoReShade.App.Infrastructure;

/// <summary>
/// When a process was created, through a query-limited handle
/// (PROCESS_QUERY_LIMITED_INFORMATION + GetProcessTimes). Never reads process
/// memory. Together with the PID it identifies one process: Windows reuses
/// PIDs, never (PID, creation time) pairs.
/// </summary>
public static class ProcessStartTime
{
    /// <summary>The process's creation time (UTC), or null when it is gone or can't be queried.</summary>
    public static DateTime? TryGet(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            using SafeProcessHandle handle = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
            if (handle.IsInvalid ||
                !GetProcessTimes(handle, out long created, out _, out _, out _) ||
                created <= 0)
            {
                return null;
            }

            return DateTime.FromFileTimeUtc(created);
        }
        catch
        {
            return null; // best effort
        }
    }

    // FILETIMEs as 64-bit values (same layout: low then high DWORD).
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);
}

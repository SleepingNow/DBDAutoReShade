using System.Runtime.InteropServices;
using DBDAutoReShade.Desktop.Interop;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>Windows mandatory integrity level of a process.</summary>
public enum IntegrityLevel
{
    Unknown,
    Untrusted,
    Low,
    Medium,
    High,
    System
}

/// <summary>
/// Reads process integrity levels via documented token APIs (process metadata
/// only — the same category as process enumeration; never game memory).
/// Used to explain blocked SendInput keystrokes (UIPI): a normal app cannot
/// send input to an elevated game.
/// </summary>
public static class ProcessIntegrity
{
    // SECURITY_MANDATORY_*_RID values.
    private const uint UntrustedRid = 0x1000;
    private const uint LowRid = 0x2000;
    private const uint MediumRid = 0x3000;
    private const uint HighRid = 0x4000;
    private const uint SystemRid = 0x5000;

    public static IntegrityLevel GetForCurrentProcess()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return GetForProcess(process.Id);
        }
        catch
        {
            return IntegrityLevel.Unknown;
        }
    }

    public static IntegrityLevel GetForProcess(int pid)
    {
        if (pid <= 0)
        {
            return IntegrityLevel.Unknown;
        }

        IntPtr process = IntPtr.Zero;
        IntPtr token = IntPtr.Zero;
        try
        {
            process = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (process == IntPtr.Zero)
            {
                return IntegrityLevel.Unknown;
            }

            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out token) ||
                token == IntPtr.Zero)
            {
                return IntegrityLevel.Unknown;
            }

            uint needed = 0;
            // First call yields the required size (fails with insufficient buffer).
            NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel,
                IntPtr.Zero, 0, out needed);
            if (needed == 0 || needed > 1024)
            {
                return IntegrityLevel.Unknown;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel,
                    buffer, needed, out _))
                {
                    return IntegrityLevel.Unknown;
                }

                var label = Marshal.PtrToStructure<NativeMethods.TOKEN_MANDATORY_LABEL>(buffer);
                IntPtr sid = label.Label.Sid;
                if (sid == IntPtr.Zero)
                {
                    return IntegrityLevel.Unknown;
                }

                IntPtr countPtr = NativeMethods.GetSidSubAuthorityCount(sid);
                if (countPtr == IntPtr.Zero)
                {
                    return IntegrityLevel.Unknown;
                }

                byte count = Marshal.ReadByte(countPtr);
                if (count == 0)
                {
                    return IntegrityLevel.Unknown;
                }

                IntPtr ridPtr = NativeMethods.GetSidSubAuthority(sid, (uint)(count - 1));
                if (ridPtr == IntPtr.Zero)
                {
                    return IntegrityLevel.Unknown;
                }

                uint rid = (uint)Marshal.ReadInt32(ridPtr);
                if (rid >= SystemRid)
                {
                    return IntegrityLevel.System;
                }

                if (rid >= HighRid)
                {
                    return IntegrityLevel.High;
                }

                if (rid >= MediumRid)
                {
                    return IntegrityLevel.Medium;
                }

                if (rid >= LowRid)
                {
                    return IntegrityLevel.Low;
                }

                if (rid >= UntrustedRid)
                {
                    return IntegrityLevel.Untrusted;
                }

                return IntegrityLevel.Unknown;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return IntegrityLevel.Unknown;
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(token);
            }

            if (process != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(process);
            }
        }
    }

    public static string Describe(IntegrityLevel level) => level switch
    {
        IntegrityLevel.Low => "Low",
        IntegrityLevel.Medium => "Medium (normal user)",
        IntegrityLevel.High => "High (Administrator)",
        IntegrityLevel.System => "System",
        IntegrityLevel.Untrusted => "Untrusted",
        _ => "unknown"
    };
}

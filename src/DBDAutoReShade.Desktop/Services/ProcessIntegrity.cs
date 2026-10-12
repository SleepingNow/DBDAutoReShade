using System.Runtime.InteropServices;
using DBDAutoReShade.Desktop.Interop;
using Microsoft.Win32.SafeHandles;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>Windows mandatory integrity level of a process.</summary>
public enum IntegrityLevel
{
    Unknown,
    Untrusted,
    Low,
    Medium,
    High,
    System,
    Protected
}

/// <summary>
/// Reads process integrity levels via documented token APIs (process metadata
/// only — the same category as process enumeration; never game memory).
/// Used to explain blocked SendInput keystrokes (UIPI): a normal app cannot
/// send input to an elevated game.
/// </summary>
public static class ProcessIntegrity
{
    // SECURITY_MANDATORY_*_RID values (winnt.h). MediumPlus (0x2100) sits
    // between Medium and High and counts as Medium.
    internal const uint UntrustedRid = 0x0000;
    internal const uint LowRid = 0x1000;
    internal const uint MediumRid = 0x2000;
    internal const uint MediumPlusRid = 0x2100;
    internal const uint HighRid = 0x3000;
    internal const uint SystemRid = 0x4000;
    internal const uint ProtectedRid = 0x5000;

    public static IntegrityLevel GetForCurrentProcess()
    {
        try
        {
            return GetForProcess(Environment.ProcessId);
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

        try
        {
            using SafeProcessHandle process = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (process.IsInvalid)
            {
                return IntegrityLevel.Unknown;
            }

            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out SafeAccessTokenHandle token))
            {
                token.Dispose();
                return IntegrityLevel.Unknown;
            }

            using (token)
            {
                if (token.IsInvalid)
                {
                    return IntegrityLevel.Unknown;
                }

                return ReadTokenIntegrity(token);
            }
        }
        catch
        {
            return IntegrityLevel.Unknown;
        }
    }

    /// <summary>
    /// Maps a mandatory-label RID to a level. Pure (unit-tested against the
    /// real winnt.h values): every RID at or above a level's base value up to
    /// the next level's base belongs to that level.
    /// </summary>
    internal static IntegrityLevel FromRid(uint rid)
    {
        if (rid >= ProtectedRid)
        {
            return IntegrityLevel.Protected;
        }

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
            // Includes MediumPlus (0x2100).
            return IntegrityLevel.Medium;
        }

        if (rid >= LowRid)
        {
            return IntegrityLevel.Low;
        }

        return IntegrityLevel.Untrusted;
    }

    private static IntegrityLevel ReadTokenIntegrity(SafeAccessTokenHandle token)
    {
        // First call yields the required size (fails with insufficient buffer).
        NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel,
            IntPtr.Zero, 0, out uint needed);
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

            return FromRid((uint)Marshal.ReadInt32(ridPtr));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string Describe(IntegrityLevel level) => level switch
    {
        IntegrityLevel.Low => "Low",
        IntegrityLevel.Medium => "Medium (normal user)",
        IntegrityLevel.High => "High (Administrator)",
        IntegrityLevel.System => "System",
        IntegrityLevel.Protected => "Protected",
        IntegrityLevel.Untrusted => "Untrusted",
        _ => "unknown"
    };
}

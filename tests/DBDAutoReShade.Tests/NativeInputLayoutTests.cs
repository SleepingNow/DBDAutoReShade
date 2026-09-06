using System.Runtime.InteropServices;
using DBDAutoReShade.Desktop.Interop;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// Guards the native SendInput layout: INPUT must marshal to the exact native
/// size (40 bytes on x64, 28 on x86) or Windows rejects every call.
/// </summary>
public sealed class NativeInputLayoutTests
{
    [Fact]
    public void Input_MarshalsToNativeSize()
    {
        int expected = IntPtr.Size == 8 ? 40 : 28;
        Assert.Equal(expected, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    [Fact]
    public void Union_IsSizedByLargestMember()
    {
        Assert.True(Marshal.SizeOf<NativeMethods.MOUSEINPUT>() >= Marshal.SizeOf<NativeMethods.KEYBDINPUT>());
        Assert.Equal(Marshal.SizeOf<NativeMethods.MOUSEINPUT>(), Marshal.SizeOf<NativeMethods.INPUTUNION>());
    }

    [Fact]
    public void OwnProcess_HasKnownIntegrityLevel()
    {
        IntegrityLevel level = ProcessIntegrity.GetForCurrentProcess();
        Assert.NotEqual(IntegrityLevel.Unknown, level);
    }

    [Fact]
    public void InvalidPid_ReturnsUnknown()
    {
        Assert.Equal(IntegrityLevel.Unknown, ProcessIntegrity.GetForProcess(-1));
        Assert.Equal(IntegrityLevel.Unknown, ProcessIntegrity.GetForProcess(0));
    }

    [Fact]
    public void BlockedHint_NamesElevationMismatch()
    {
        string hint = PresetActivationService.BuildBlockedHint(IntegrityLevel.Medium, IntegrityLevel.High);
        Assert.Contains("Administrator", hint);
        Assert.Contains("same way", hint);
    }

    [Fact]
    public void BlockedHint_GenericWhenLevelsUnknown()
    {
        string hint = PresetActivationService.BuildBlockedHint(IntegrityLevel.Medium, IntegrityLevel.Unknown);
        Assert.Contains("blocked", hint);
        Assert.DoesNotContain("same way", hint);
    }

    [Fact]
    public void BlockedHint_GenericWhenLevelsEqual()
    {
        string hint = PresetActivationService.BuildBlockedHint(IntegrityLevel.Medium, IntegrityLevel.Medium);
        Assert.Contains("blocked", hint);
    }
}

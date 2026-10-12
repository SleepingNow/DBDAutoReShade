using System.Security.Principal;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

/// <summary>Mandatory-label RID mapping against the real winnt.h values.</summary>
public sealed class ProcessIntegrityTests
{
    [Theory]
    [InlineData(0x0000u, IntegrityLevel.Untrusted)]
    [InlineData(0x0FFFu, IntegrityLevel.Untrusted)]
    [InlineData(0x1000u, IntegrityLevel.Low)]
    [InlineData(0x2000u, IntegrityLevel.Medium)]
    [InlineData(0x2100u, IntegrityLevel.Medium)] // MediumPlus
    [InlineData(0x3000u, IntegrityLevel.High)]
    [InlineData(0x4000u, IntegrityLevel.System)]
    [InlineData(0x5000u, IntegrityLevel.Protected)]
    public void FromRid_MapsWindowsValues(uint rid, IntegrityLevel expected)
    {
        Assert.Equal(expected, ProcessIntegrity.FromRid(rid));
    }

    [Fact]
    public void OwnProcess_LevelMatchesElevation()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        IntegrityLevel expected = identity.IsSystem
            ? IntegrityLevel.System
            : new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
                ? IntegrityLevel.High
                : IntegrityLevel.Medium;

        Assert.Equal(expected, ProcessIntegrity.GetForCurrentProcess());
    }

    [Fact]
    public void ElevatedGame_IsAboveNormalApp()
    {
        // Ordering drives the UIPI mismatch hint (gameLevel > ownLevel).
        Assert.True(ProcessIntegrity.FromRid(0x3000) > ProcessIntegrity.FromRid(0x2000));
        Assert.True(ProcessIntegrity.FromRid(0x2000) > ProcessIntegrity.FromRid(0x1000));
        Assert.Equal("Protected", ProcessIntegrity.Describe(IntegrityLevel.Protected));
    }
}

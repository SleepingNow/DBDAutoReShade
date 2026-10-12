using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

/// <summary>Typed activation failures: what callers wait on (focus) vs. what is final.</summary>
public sealed class PresetActivationServiceTests : IDisposable
{
    private sealed class FakeKeys : IKeySimulator
    {
        public bool Result { get; set; } = true;
        public bool Throw { get; set; }
        public int Calls { get; private set; }

        public bool SendShortcut(ReshadeShortcutConfig shortcut)
        {
            Calls++;
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            return Result;
        }
    }

    private static readonly IntPtr Window = new(0x1234);
    private static readonly ReshadeShortcutConfig Shortcut = new() { VirtualKey = 0x7C };

    private readonly string _preset = Path.Combine(Path.GetTempPath(), "dbd-act-" + Guid.NewGuid().ToString("N") + ".ini");

    public PresetActivationServiceTests() => File.WriteAllText(_preset, "[General]\nTechniques=A\n");

    public void Dispose()
    {
        try
        {
            File.Delete(_preset);
        }
        catch
        {
            // Best effort.
        }
    }

    [Fact]
    public void Success_HasNoFailure()
    {
        var keys = new FakeKeys();
        ActivationResult result = new PresetActivationService(keys, _ => true).Activate(_preset, Shortcut, Window, true);
        Assert.True(result.Success);
        Assert.Equal(ActivationFailure.None, result.Failure);
        Assert.Equal(1, keys.Calls);
    }

    [Fact]
    public void NotFocused_IsTransient_AndSendsNothing()
    {
        var keys = new FakeKeys();
        var service = new PresetActivationService(keys, _ => false);
        Assert.Equal(ActivationFailure.NotFocused, service.Activate(_preset, Shortcut, Window, true).Failure);
        Assert.Equal(ActivationFailure.NotFocused, service.Activate(_preset, Shortcut, IntPtr.Zero, true).Failure); // no window yet
        Assert.Equal(0, keys.Calls);
    }

    [Fact]
    public void FinalProblems_AreReportedBeforeFocus()
    {
        var keys = new FakeKeys();
        var unfocused = new PresetActivationService(keys, _ => false);
        string missing = _preset + ".missing.ini";

        Assert.Equal(ActivationFailure.NoPreset, unfocused.CheckReady(" ", Shortcut, Window, true).Failure);
        Assert.Equal(ActivationFailure.FileMissing, unfocused.CheckReady(missing, Shortcut, Window, true).Failure);
        Assert.Equal(ActivationFailure.NoShortcut, unfocused.CheckReady(_preset, null, Window, true).Failure);
        Assert.Equal(ActivationFailure.NotRunning, unfocused.CheckReady(_preset, Shortcut, Window, false).Failure);
        Assert.Equal(0, keys.Calls);
    }

    [Fact]
    public void CheckReady_NeverSends()
    {
        var keys = new FakeKeys();
        ActivationResult ready = new PresetActivationService(keys, _ => true).CheckReady(_preset, Shortcut, Window, true);
        Assert.True(ready.Success);
        Assert.Equal(0, keys.Calls);
    }

    [Fact]
    public void RejectedOrThrowingSend_IsSendBlocked()
    {
        var keys = new FakeKeys { Result = false };
        var service = new PresetActivationService(keys, _ => true, _ => IntegrityLevel.Unknown);
        ActivationResult rejected = service.Activate(_preset, Shortcut, Window, true);
        Assert.Equal(ActivationFailure.SendBlocked, rejected.Failure);
        Assert.Contains("blocked", rejected.Message);

        keys.Throw = true;
        Assert.Equal(ActivationFailure.SendBlocked, service.Activate(_preset, Shortcut, Window, true).Failure);
    }

    [Fact]
    public void TwoArgumentConstructor_StaysCompatible()
    {
        var result = new ActivationResult(true, "ok");
        Assert.True(result.Success);
        Assert.Equal(ActivationFailure.None, result.Failure);
        Assert.Equal(ActivationFailure.RestartRequired,
            ActivationResult.Fail(ActivationFailure.RestartRequired, "restart").Failure);
    }
}

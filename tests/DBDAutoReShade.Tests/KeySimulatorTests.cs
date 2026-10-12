using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Desktop.Interop;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

public sealed class KeySimulatorTests
{
    [Fact]
    public void FullCombo_ProducesDownUpSequence()
    {
        var shortcut = new ReshadeShortcutConfig { VirtualKey = 0x7C, Ctrl = true, Shift = true, Alt = true };
        SendInputKeySimulator.BuildPress(shortcut, out NativeMethods.INPUT[] downs, out NativeMethods.INPUT[] ups);

        // Down-phase: 3 modifiers + key. Up-phase: key + 3 modifiers (reverse order).
        Assert.Equal(4, downs.Length);
        Assert.Equal(4, ups.Length);
        Assert.All(downs.Concat(ups), i => Assert.Equal(NativeMethods.INPUT_KEYBOARD, i.type));
        Assert.All(downs, i => Assert.Equal(0u, i.u.ki.dwFlags));
        Assert.All(ups, i => Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, i.u.ki.dwFlags));

        Assert.Equal(0x7Cu, downs[3].u.ki.wVk); // VK_F13 down last
        Assert.Equal(0x7Cu, ups[0].u.ki.wVk); // VK_F13 up first
        Assert.Equal(0x12u, ups[1].u.ki.wVk); // Alt up next
        Assert.Equal(0x11u, ups[3].u.ki.wVk); // Ctrl up last
    }

    [Fact]
    public void BareKey_ProducesSinglePress()
    {
        var shortcut = new ReshadeShortcutConfig { VirtualKey = 0x77, Ctrl = false, Shift = false, Alt = false };
        SendInputKeySimulator.BuildPress(shortcut, out NativeMethods.INPUT[] downs, out NativeMethods.INPUT[] ups);
        Assert.Single(downs);
        Assert.Single(ups);
    }

    // ----- S8: never leave modifiers stuck -----

    private static readonly ReshadeShortcutConfig FullCombo =
        new() { VirtualKey = 0x7C, Ctrl = true, Shift = true, Alt = true };

    /// <summary>Records every SendInput batch and answers with scripted insert counts (default: all).</summary>
    private sealed class FakeInjector
    {
        private readonly Queue<int> _results;

        public FakeInjector(params int[] results) => _results = new Queue<int>(results);

        public List<NativeMethods.INPUT[]> Batches { get; } = new();

        public List<int> Sleeps { get; } = new();

        public uint Inject(NativeMethods.INPUT[] inputs)
        {
            Batches.Add(inputs);
            return (uint)(_results.Count > 0 ? _results.Dequeue() : inputs.Length);
        }

        public SendInputKeySimulator Create() => new(Inject, ms => Sleeps.Add(ms));
    }

    private static ushort[] Vks(NativeMethods.INPUT[] batch) => batch.Select(i => i.u.ki.wVk).ToArray();

    [Fact]
    public void FullSuccess_DownHoldUp_ReturnsTrue()
    {
        var fake = new FakeInjector();
        Assert.True(fake.Create().SendShortcut(FullCombo, 80));

        Assert.Equal(2, fake.Batches.Count);
        Assert.Equal(new ushort[] { 0x11, 0x10, 0x12, 0x7C }, Vks(fake.Batches[0]));
        Assert.Equal(new ushort[] { 0x7C, 0x12, 0x10, 0x11 }, Vks(fake.Batches[1]));
        Assert.All(fake.Batches[1], i => Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, i.u.ki.dwFlags));
        Assert.Equal(new[] { 80 }, fake.Sleeps);
    }

    [Fact]
    public void PartialDownBatch_ReleasesTheKeysThatWentDown_AndFails()
    {
        // Only Ctrl and Shift were inserted before SendInput stopped.
        var fake = new FakeInjector(2);
        Assert.False(fake.Create().SendShortcut(FullCombo, 80));

        Assert.Equal(2, fake.Batches.Count);
        NativeMethods.INPUT[] release = fake.Batches[1];
        Assert.Equal(new ushort[] { 0x10, 0x11 }, Vks(release)); // Shift up, then Ctrl up
        Assert.All(release, i => Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, i.u.ki.dwFlags));
        Assert.Empty(fake.Sleeps); // no hold after a failed press
    }

    [Fact]
    public void NothingWentDown_SendsNoRelease_AndFails()
    {
        var fake = new FakeInjector(0);
        Assert.False(fake.Create().SendShortcut(FullCombo, 80));
        Assert.Single(fake.Batches);
    }

    [Fact]
    public void ShortUpBatch_RetriesTheRemainingKeysOnce()
    {
        // Down OK (4), up inserts only the main key (1), retry releases the rest.
        var fake = new FakeInjector(4, 1, 3);
        Assert.True(fake.Create().SendShortcut(FullCombo, 0));

        Assert.Equal(3, fake.Batches.Count);
        Assert.Equal(new ushort[] { 0x12, 0x10, 0x11 }, Vks(fake.Batches[2]));
        Assert.All(fake.Batches[2], i => Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, i.u.ki.dwFlags));
    }

    [Fact]
    public void UpBatchFailingTwice_ReturnsFalse_WithExactlyOneRetry()
    {
        var fake = new FakeInjector(4, 0, 0);
        Assert.False(fake.Create().SendShortcut(FullCombo, 0));
        Assert.Equal(3, fake.Batches.Count);
        Assert.Equal(new ushort[] { 0x7C, 0x12, 0x10, 0x11 }, Vks(fake.Batches[2]));
    }

    [Fact]
    public void PartialDown_ThenShortRelease_StillRetriesRelease()
    {
        var fake = new FakeInjector(3, 1, 2);
        Assert.False(fake.Create().SendShortcut(FullCombo, 0));
        Assert.Equal(3, fake.Batches.Count);
        Assert.Equal(new ushort[] { 0x12, 0x10, 0x11 }, Vks(fake.Batches[1]));
        Assert.Equal(new ushort[] { 0x10, 0x11 }, Vks(fake.Batches[2]));
    }

    [Fact]
    public void ThrowingInjector_IsTreatedAsFailure()
    {
        var sim = new SendInputKeySimulator(_ => throw new InvalidOperationException("boom"), _ => { });
        Assert.False(sim.SendShortcut(FullCombo, 0));
    }

    [Theory]
    [InlineData(0, new ushort[0])]
    [InlineData(1, new ushort[] { 0x11 })]
    [InlineData(3, new ushort[] { 0x12, 0x10, 0x11 })]
    [InlineData(4, new ushort[] { 0x7C, 0x12, 0x10, 0x11 })]
    [InlineData(9, new ushort[] { 0x7C, 0x12, 0x10, 0x11 })]
    public void BuildRelease_ReleasesPressedPrefixInReverse(int pressed, ushort[] expected)
    {
        SendInputKeySimulator.BuildPress(FullCombo, out NativeMethods.INPUT[] downs, out _);
        Assert.Equal(expected, Vks(SendInputKeySimulator.BuildRelease(downs, pressed)));
    }
}

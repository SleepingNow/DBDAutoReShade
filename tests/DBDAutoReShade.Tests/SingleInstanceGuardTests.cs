using DBDAutoReShade.App.Infrastructure;

namespace DBDAutoReShade.Tests;

/// <summary>C20: only one instance runs per session (unique mutex names per test).</summary>
public sealed class SingleInstanceGuardTests
{
    private static string UniqueName() => $@"Local\DBDAutoReShade.Test.{Guid.NewGuid():N}";

    [Fact]
    public void SecondInstance_IsRefused_UntilTheFirstEnds()
    {
        string name = UniqueName();
        SingleInstanceGuard? first = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(first);

        Assert.Null(SingleInstanceGuard.TryAcquire(name));
        Assert.Null(SingleInstanceGuard.TryAcquire(name)); // a refused attempt holds nothing

        first.Dispose();
        using SingleInstanceGuard? next = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task Guard_HasNoThreadAffinity()
    {
        string name = UniqueName();
        SingleInstanceGuard? guard = await Task.Run(() => SingleInstanceGuard.TryAcquire(name));
        Assert.NotNull(guard);

        Assert.Null(SingleInstanceGuard.TryAcquire(name));
        guard.Dispose(); // released from another thread than the one that created it
        using SingleInstanceGuard? again = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(again);
    }

    [Fact]
    public void NameThatCannotBeOpened_CountsAsHeld_InsteadOfThrowing()
    {
        // Like an elevated instance's mutex seen from a normal one (access
        // denied), the name exists but can't be opened as a mutex here.
        string name = UniqueName();
        using var squatter = new EventWaitHandle(false, EventResetMode.ManualReset, name);

        Assert.Null(SingleInstanceGuard.TryAcquire(name));
    }

    [Fact]
    public void DefaultName_IsPerSession()
    {
        Assert.StartsWith(@"Local\", SingleInstanceGuard.DefaultName);
    }
}

using System.Collections.Concurrent;
using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// Controller ↔ detection focus gate (F2): a scan only reads frames of DBD
/// itself. The capture copies the screen at DBD's position, so while another
/// window is in front (borderless DBD covered by a browser, Discord or this
/// app's own Presets list of realm names) nothing is captured, read or counted,
/// and the status line says why. A DBD that is not the foreground window but
/// whose loading-screen text no other window covers (in plain view on one
/// monitor while the user works on another) is still read.
/// </summary>
public sealed partial class ControllerActivationTests
{
    private const string ScanningStatusF8 = "Reading the loading screen… press F8 again to stop.";

    [Fact]
    public async Task Scan_WhileDbdIsNotInFront_ReadsNothingAndSaysWhy_ThenReadsOnceBackInTheGame()
    {
        ControllerHarness h = Harness();
        h.Focused = false; // the default OCR text would confirm Coldwind Farm on the first read

        Task scan = h.Controller.TriggerDetectionAsync();
        await ControllerHarness.WaitUntilAsync(
            () => h.Controller.StatusMessage.Contains("not the active window"), "the paused status");

        Assert.StartsWith(DetectionOrchestrator.NotForegroundDiagnostic, h.Controller.StatusMessage);
        Assert.Contains("switch back to the game", h.Controller.StatusMessage);
        Assert.Contains("press F8 again to stop", h.Controller.StatusMessage);
        Assert.Contains("not the active window", h.Controller.DetectionText);
        Assert.Equal(AppState.DetectingLoadingScreen, h.States.State);
        Assert.False(scan.IsCompleted); // keeps waiting
        Assert.Equal(0, h.Capture.RegionCalls); // nothing captured…
        Assert.Equal(0, h.Ocr.Calls); // …nothing read…
        Assert.Null(h.Controller.LastDetection); // …nothing detected
        Assert.Equal(0, h.Keys.CallCount);

        h.Focused = true; // back in the game: the loading screen is read
        await scan.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(h.Controller.LastDetection?.Success);
        Assert.Equal(1, h.Controller.LastDetection!.SampleCount); // the paused ticks were no samples
        Assert.Equal(AppState.PresetActive, h.States.State);
        Assert.Equal(new[] { "Coldwind Farm" }, h.SentNames());
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);
    }

    [Fact]
    public async Task ScanPause_IsSaidOnce_AndTheScanningTextReturnsWhenReadingResumes()
    {
        ControllerHarness h = Harness();
        h.Ocr.Text = "PRESS START"; // reads, but never confirms
        h.Focused = false;
        int paused = 0;
        h.Detector.SampleEvaluated += (_, i) =>
        {
            if (i.Paused)
            {
                Interlocked.Increment(ref paused); // runs after the controller's handler for the same tick
            }
        };
        int changes = 0;
        h.Controller.Changed += (_, _) => Interlocked.Increment(ref changes);

        Task scan = h.Controller.TriggerDetectionAsync();
        await ControllerHarness.WaitUntilAsync(() => Volatile.Read(ref paused) >= 1, "a paused tick");
        string notice = h.Controller.StatusMessage;
        Assert.StartsWith(DetectionOrchestrator.NotForegroundDiagnostic, notice);
        int changesAfterNotice = Volatile.Read(ref changes);

        await ControllerHarness.WaitUntilAsync(() => Volatile.Read(ref paused) >= 4, "more paused ticks");
        Assert.Equal(changesAfterNotice, Volatile.Read(ref changes)); // the repeats are quiet
        Assert.Equal(notice, h.Controller.StatusMessage);

        h.Focused = true;
        await ControllerHarness.WaitUntilAsync(
            () => h.Controller.DetectionText.Contains("— last read:"), "a real sample");
        Assert.Equal(ScanningStatusF8, h.Controller.StatusMessage);
        Assert.True(h.Ocr.Calls > 0);

        await h.Controller.TriggerDetectionAsync(); // stop
        await scan;
    }

    [Fact]
    public async Task ScanPause_NeverOverwritesALaterUnrelatedStatus()
    {
        ControllerHarness h = Harness();
        h.Ocr.Text = "PRESS START";
        h.Focused = false;
        int paused = 0;
        h.Detector.SampleEvaluated += (_, i) =>
        {
            if (i.Paused)
            {
                Interlocked.Increment(ref paused);
            }
        };

        Task scan = h.Controller.TriggerDetectionAsync();
        await ControllerHarness.WaitUntilAsync(() => Volatile.Read(ref paused) >= 1, "a paused tick");
        Assert.StartsWith(DetectionOrchestrator.NotForegroundDiagnostic, h.Controller.StatusMessage);

        h.Controller.ReturnToAutomatic(); // something unrelated the user does in this app meanwhile
        int seen = Volatile.Read(ref paused);
        await ControllerHarness.WaitUntilAsync(() => Volatile.Read(ref paused) >= seen + 3, "more paused ticks");
        Assert.Equal("Automatic mode is already active.", h.Controller.StatusMessage);

        h.Focused = true; // reading resumes; the later message stays
        await ControllerHarness.WaitUntilAsync(
            () => h.Controller.DetectionText.Contains("— last read:"), "a real sample");
        Assert.Equal("Automatic mode is already active.", h.Controller.StatusMessage);

        await h.Controller.TriggerDetectionAsync(); // stop
        await scan;
    }

    [Fact]
    public async Task Scan_ReadsDbdInPlainViewOnAnotherMonitor_WhileTheUserWorksElsewhere()
    {
        // Multi-monitor: DBD's loading screen stays uncovered on its monitor while
        // the user clicks into Discord on the other one during the loading screen.
        ControllerHarness h = Harness();
        h.Focused = false;
        h.Uncovered = true;
        var statuses = new ConcurrentQueue<string>();
        h.Controller.Changed += (_, _) => statuses.Enqueue(h.Controller.StatusMessage);

        await h.Controller.TriggerDetectionAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(h.Controller.LastDetection?.Success);
        Assert.Equal("Coldwind Farm", h.Controller.LastDetection!.RealmName);
        Assert.Equal(1, h.Controller.LastDetection.SampleCount);
        Assert.Equal(2, h.Ocr.Calls); // one frame (map + realm)
        Assert.DoesNotContain(statuses, s => s.Contains("not the active window")); // never paused
        Assert.NotEmpty(h.UncoveredAsked);
        Assert.All(h.UncoveredAsked, hwnd => Assert.Equal(h.Dbd.MainWindowHandle, hwnd));

        // The switch itself still waits for focus: keystrokes go to the foreground window.
        Assert.Equal(0, h.Keys.CallCount);
        Assert.Equal(AppState.WaitingForGameplay, h.States.State);
        Assert.Contains("Waiting for DBD focus to switch to", h.Controller.StatusMessage);

        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.Equal(new[] { "Coldwind Farm" }, h.SentNames());
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);
    }

    [Fact]
    public async Task ScanPausedByACoveringWindow_ResumesOnceDbdIsUncovered_WithoutFocus()
    {
        ControllerHarness h = Harness();
        h.Focused = false; // a browser covers DBD's loading screen
        int paused = 0;
        h.Detector.SampleEvaluated += (_, i) =>
        {
            if (i.Paused)
            {
                Interlocked.Increment(ref paused);
            }
        };

        Task scan = h.Controller.TriggerDetectionAsync();
        await ControllerHarness.WaitUntilAsync(() => Volatile.Read(ref paused) >= 2, "paused ticks");
        Assert.StartsWith(DetectionOrchestrator.NotForegroundDiagnostic, h.Controller.StatusMessage);
        Assert.Equal(0, h.Capture.RegionCalls);
        Assert.Equal(0, h.Ocr.Calls);
        Assert.NotEmpty(h.UncoveredAsked); // the occlusion check was asked, and said "covered"

        h.Uncovered = true; // the browser is moved to the other monitor; DBD stays unfocused
        await scan.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(h.Controller.LastDetection?.Success);
        Assert.Equal(1, h.Controller.LastDetection!.SampleCount); // the paused ticks were no samples
        Assert.False(h.Focused);
        Assert.Equal(0, h.Keys.CallCount); // the switch waits for focus
        Assert.Equal(AppState.WaitingForGameplay, h.States.State);
    }

    [Fact]
    public async Task ScanStoppedDuringAPausedTick_NeverSaysPausedAfterTheStop()
    {
        ControllerHarness h = Harness();
        h.Focused = false; // covered
        Task? stop = null;
        h.UncoveredCheck = _ =>
        {
            // The user's stop (F8) lands while this very tick finds DBD covered.
            stop ??= h.Controller.TriggerDetectionAsync();
            return false;
        };
        var statuses = new ConcurrentQueue<string>();
        var details = new ConcurrentQueue<string>();
        h.Controller.Changed += (_, _) =>
        {
            statuses.Enqueue(h.Controller.StatusMessage);
            details.Enqueue(h.Controller.DetectionText);
        };

        await h.Controller.TriggerDetectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(stop);
        await stop!;

        Assert.True(h.Controller.LastDetection?.WasStopped);
        Assert.Equal(0, h.Ocr.Calls);
        Assert.DoesNotContain(statuses, s => s.Contains("not the active window"));
        Assert.DoesNotContain(details, s => s.Contains("not the active window"));
        Assert.Contains("Scan stopped.", h.Controller.StatusMessage);
    }
}

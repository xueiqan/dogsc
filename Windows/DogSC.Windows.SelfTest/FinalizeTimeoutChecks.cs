using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace DogSC.Windows.SelfTest;

internal static class FinalizeTimeoutChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        var timeout = TimeSpan.FromMilliseconds(150);
        int releases = 0;
        using (var missingCallback = Create(timeout, () => { }, () => Interlocked.Increment(ref releases)))
        {
            missingCallback.Stop();
            await ExpectTimeout(missingCallback.Completion);
            await missingCallback.ReleaseCompletion.WaitAsync(TimeSpan.FromSeconds(3));
            Require(missingCallback.State == RecordingState.Failed && releases == 1,
                "Display sessions without a native callback must fail and attempt release.");
            Finish(missingCallback);
            missingCallback.Dispose();
            Require(missingCallback.State == RecordingState.Failed && releases == 1,
                "Late callbacks and repeated Dispose must not overwrite failure or repeat release.");
            checks += 2;
        }

        using (var success = Create(timeout, () => { }, () => { }))
        {
            success.Stop();
            Finish(success);
            await success.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            await success.ReleaseCompletion;
            await Task.Delay(timeout + timeout);
            Require(success.State == RecordingState.Complete && success.Completion.IsCompletedSuccessfully,
                "A successful completion must cancel its timeout.");
            checks++;
        }

        int stops = 0;
        releases = 0;
        using (var disposedActive = Create(TimeSpan.FromSeconds(3), () => Interlocked.Increment(ref stops),
                   () => Interlocked.Increment(ref releases)))
        {
            disposedActive.Dispose();
            disposedActive.Dispose();
            await Until(() => Volatile.Read(ref stops) == 1);
            Finish(disposedActive);
            await disposedActive.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            await disposedActive.ReleaseCompletion;
            Require(stops == 1 && releases == 1, "Dispose must request one stop and release once.");
            checks++;
        }

        using (var releaseEntered = new ManualResetEventSlim())
        using (var allowRelease = new ManualResetEventSlim())
        using (var blockedRelease = Create(timeout, () => { }, () => { releaseEntered.Set(); allowRelease.Wait(); }))
        {
            try
            {
                blockedRelease.Stop();
                Finish(blockedRelease);
                await Until(() => releaseEntered.IsSet);
                var response = Stopwatch.StartNew();
                blockedRelease.Dispose();
                Require(response.Elapsed < TimeSpan.FromMilliseconds(100), "Dispose must not block the UI thread.");
                Require(!blockedRelease.Completion.IsCompleted && !blockedRelease.ReleaseCompletion.IsCompleted,
                    "Success must wait for actual resource release.");
                await ExpectTimeout(blockedRelease.Completion);
                Require(blockedRelease.State == RecordingState.Failed && !blockedRelease.ReleaseCompletion.IsCompleted,
                    "Blocked release must not pretend to release handles or produce a completed recording.");
                checks += 3;
            }
            finally { allowRelease.Set(); }
            await blockedRelease.ReleaseCompletion.WaitAsync(TimeSpan.FromSeconds(3));
            Require(blockedRelease.State == RecordingState.Failed, "Late release must preserve the timeout failure.");
            checks++;
        }

        using (var stopEntered = new ManualResetEventSlim())
        using (var allowStop = new ManualResetEventSlim())
        using (var blockedStop = Create(timeout, () => { stopEntered.Set(); allowStop.Wait(); }, () => { }))
        {
            try
            {
                var response = Stopwatch.StartNew();
                blockedStop.Stop();
                Require(response.Elapsed < TimeSpan.FromMilliseconds(100), "Native Stop must not block its caller.");
                await Until(() => stopEntered.IsSet);
                await ExpectTimeout(blockedStop.Completion);
                Require(!blockedStop.ReleaseCompletion.IsCompleted,
                    "Release must wait for the native stop operation instead of disposing concurrently.");
                checks += 2;
            }
            finally { allowStop.Set(); }
            await blockedStop.ReleaseCompletion.WaitAsync(TimeSpan.FromSeconds(3));
        }

        using (var releaseFailure = Create(TimeSpan.FromSeconds(3), () => { }, () => throw new InvalidOperationException("release fixture")))
        {
            Finish(releaseFailure);
            try { await releaseFailure.Completion.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Expected release failure."); }
            catch (InvalidOperationException error) when (error.Message == "release fixture") { }
            try { await releaseFailure.ReleaseCompletion; throw new Exception("Expected release failure."); }
            catch (InvalidOperationException error) when (error.Message == "release fixture") { }
            Require(releaseFailure.State == RecordingState.Failed, "A release failure must never report successful recording.");
            checks++;
        }

        var failedOverlay = new RealtimeCameraOverlay(30, new(0, .1, .1, .2, .2, 4.0 / 3));
        var image = System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96,
            System.Windows.Media.PixelFormats.Bgr32, null, new byte[16], 8);
        image.Freeze(); failedOverlay.SubmitFrame(image);
        await failedOverlay.Ready.WaitAsync(TimeSpan.FromSeconds(3));
        var overlayFailure = new System.IO.IOException("overlay fixture");
        failedOverlay.Bind(640, 480, 120, (_, _) => Task.FromException<bool>(overlayFailure));
        try { await failedOverlay.Completion.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Expected overlay failure."); }
        catch (System.IO.IOException error) when (ReferenceEquals(error, overlayFailure)) { }
        releases = 0;
        using (var releasedAfterOverlayFailure = Create(TimeSpan.FromSeconds(3), () => { }, () => Interlocked.Increment(ref releases)))
        {
            typeof(RecordingSession).GetField("cameraOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(releasedAfterOverlayFailure, failedOverlay);
            Finish(releasedAfterOverlayFailure, overlayFailure);
            try { await releasedAfterOverlayFailure.Completion; throw new Exception("Expected recording failure."); }
            catch (System.IO.IOException error) when (ReferenceEquals(error, overlayFailure)) { }
            await releasedAfterOverlayFailure.ReleaseCompletion.WaitAsync(TimeSpan.FromSeconds(3));
            Require(releases == 1 && releasedAfterOverlayFailure.State == RecordingState.Failed,
                "A failed overlay must preserve recording failure while confirming successful native release.");
            checks++;
        }
        return checks;
    }

    private static RecordingSession Create(TimeSpan timeout, Action stop, Action release)
    {
        var session = new RecordingSession(timeout, stop, release);
        // Own fake display session exercises production Stop/Finish/release flow without a capture device.
        typeof(RecordingSession).GetField("started", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, true);
        typeof(RecordingSession).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, RecordingState.Recording);
        return session;
    }

    private static void Finish(RecordingSession session, Exception? error = null) => typeof(RecordingSession)
        .GetMethod("Finish", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, new object?[] { null, error });

    private static async Task ExpectTimeout(Task completion)
    {
        try { await completion.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) when (completion.IsFaulted) { return; }
        throw new Exception("Expected the session's finalization timeout, not an outer test timeout.");
    }

    private static async Task Until(Func<bool> ready)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Fixture operation was not scheduled.");
            await Task.Delay(5);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

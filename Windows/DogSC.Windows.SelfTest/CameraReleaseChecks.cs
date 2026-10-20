using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DogSC.Windows;

namespace DogSC.Windows.SelfTest;

internal static class CameraReleaseChecks
{
    public static async Task<int> RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        int checks = 0, releases = 0;
        using (var notStarted = new CameraRecordingSession(() => Interlocked.Increment(ref releases)))
        {
            notStarted.Dispose();
            notStarted.Dispose();
            await notStarted.ReleaseCompletion.WaitAsync(TimeSpan.FromSeconds(3));
            Require(releases == 1, "Repeated Dispose on an unused camera must release exactly once.");
            checks++;
        }

        releases = 0;
        string latePath = Path.Combine(root, "camera-late-callback.fixture");
        using (var releaseEntered = new ManualResetEventSlim())
        using (var allowRelease = new ManualResetEventSlim())
        using (var late = Create(latePath, () =>
               { Interlocked.Increment(ref releases); releaseEntered.Set(); allowRelease.Wait(); }))
        {
            try
            {
                late.Dispose();
                late.Dispose();
                await Task.Delay(50);
                Require(!late.ReleaseCompletion.IsCompleted,
                    "Deferred Dispose without a native callback must not pretend resources were released.");
                checks++;
                Finish(late);
                await Until(() => releaseEntered.IsSet);
                var response = Stopwatch.StartNew();
                late.Dispose();
                Require(response.Elapsed < TimeSpan.FromMilliseconds(100), "Camera Dispose must not block its caller.");
                Require(!late.ReleaseCompletion.IsCompleted && !late.Completion.IsCompleted,
                    "Native release that is still blocked must not complete either task.");
                RequireExclusiveOpenRejected(latePath);
                checks += 3;
            }
            finally { allowRelease.Set(); }
            await late.ReleaseCompletion.WaitAsync(TimeSpan.FromSeconds(3));
            await late.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Finish(late);
            late.Dispose();
            Require(releases == 1 && late.State == RecordingState.Complete,
                "Late repeated callback and Dispose must not repeat native release.");
            using (new FileStream(latePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            checks += 2;
        }

        string failedPath = Path.Combine(root, "camera-release-failed.fixture");
        using (var failed = Create(failedPath, () => throw new InvalidOperationException("camera release fixture")))
        {
            Finish(failed);
            await ExpectFailure(failed.ReleaseCompletion, "camera release fixture");
            await ExpectFailure(failed.Completion, "camera release fixture");
            Require(failed.State == RecordingState.Failed, "Native Dispose failure must remain observable as failure.");
            using (new FileStream(failedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            checks += 2;
        }

        string flushPath = Path.Combine(root, "camera-flush-failed.fixture");
        releases = 0;
        using (var flushFailed = Create(flushPath, () => Interlocked.Increment(ref releases)))
        {
            ((FileStream)typeof(CameraRecordingSession).GetField("output", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(flushFailed)!).Dispose();
            Finish(flushFailed);
            try { await flushFailed.Completion.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Expected flush failure."); }
            catch (ObjectDisposedException) { }
            await flushFailed.ReleaseCompletion.WaitAsync(TimeSpan.FromSeconds(3));
            Require(releases == 1 && flushFailed.State == RecordingState.Failed,
                "Flush failure must still release resources without changing capture failure into success.");
            checks++;
        }
        return checks;
    }

    private static CameraRecordingSession Create(string path, Action release)
    {
        var camera = new CameraRecordingSession(release);
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        file.WriteByte(1);
        // Fake camera owns a real file handle; only the native Dispose operation is substituted.
        typeof(CameraRecordingSession).GetField("output", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(camera, file);
        typeof(CameraRecordingSession).GetField("started", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(camera, true);
        return camera;
    }

    private static void Finish(CameraRecordingSession camera) => typeof(CameraRecordingSession)
        .GetMethod("Finish", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(camera, new object?[] { null, null });

    private static void RequireExclusiveOpenRejected(string path)
    {
        try { using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return; }
        throw new Exception("Camera file handle unexpectedly released before native Dispose completed.");
    }

    private static async Task ExpectFailure(Task task, string message)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (InvalidOperationException error) when (error.Message == message) { return; }
        throw new Exception("Expected native release failure.");
    }

    private static async Task Until(Func<bool> ready)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Camera release fixture was not scheduled.");
            await Task.Delay(5);
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}

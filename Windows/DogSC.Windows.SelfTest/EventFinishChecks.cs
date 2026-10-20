using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using DogSC.Core;
using DogSC.Windows;

namespace DogSC.Windows.SelfTest;

public static class EventFinishChecks
{
    public static async Task<int> RunAsync(string root)
    {
        int passed = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("FAIL: " + name);
            passed++;
        }
        ProjectDocument Project(string name) => ProjectStore.Create(root, name,
            new CaptureMetadata("screen", "event finish fixture", 30, false, false, null)) with { DurationSeconds = 3 };
        var target = new CaptureTarget("event finish fixture", "fixture", 0, 0, 0, 640, 480);
        var field = typeof(RecordingEvents).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var enqueue = typeof(RecordingEvents).GetMethod("Enqueue", BindingFlags.Instance | BindingFlags.NonPublic)!;
        const string broken = "{not valid json";

        var overflowProject = Project("event-queue-full");
        using (var session = new RecordingSession())
        {
            var events = new RecordingEvents(session, target, null, overflowProject.PackagePath);
            await events.Ready.WaitAsync(TimeSpan.FromSeconds(3));
            var notified = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            int notifications = 0;
            events.Warning += message => { System.Threading.Interlocked.Increment(ref notifications); notified.TrySetResult(message); };
            var original = (Channel<object>)field.GetValue(events)!;
            var full = Channel.CreateBounded<object>(1);
            field.SetValue(events, full);
            try
            {
                Check((bool)enqueue.Invoke(events, ["first fixture entry"])!, "first event fits bounded queue");
                Check(!(bool)enqueue.Invoke(events, ["overflow fixture entry"])!, "full queue records failure");
                for (int i = 0; i < 10; i++) enqueue.Invoke(events, ["discard after degradation"]);
            }
            finally { field.SetValue(events, original); }
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(events.Incomplete && events.Warnings.Length == 1 && notifications == 1, "degradation immediately warns once");
            await events.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(overflowProject.PackagePath, "events", "recording-events.json")));
            Check(report.RootElement.GetProperty("incomplete").GetBoolean(), "overflow manifest identifies incomplete events");
            Check(report.RootElement.GetProperty("droppedEvents").GetInt64() == 1, "degraded capture stops producing a warning storm");
            Check(report.RootElement.GetProperty("embeddedCursor").GetBoolean(), "native cursor contract retained");
        }
        File.WriteAllText(Path.Combine(overflowProject.PackagePath, "events", "pointer.jsonl"), broken);
        File.WriteAllText(Path.Combine(overflowProject.PackagePath, "events", "markers.jsonl"), broken);
        var state = SceneCompositor.LoadRecordingData(overflowProject, EditorState.Initial(3));
        Check(state.PointerEvents.Length == 0 && state.EmbeddedCursor, "incomplete raw events never reach compositor");
        Check(SceneCompositor.WithAutomaticZooms(state).ZoomClips.Length == 0, "incomplete events never generate zooms");

        var failedProject = Project("event-writer-fault");
        using (var session = new RecordingSession())
        {
            var events = new RecordingEvents(session, target, null, failedProject.PackagePath);
            await events.Ready.WaitAsync(TimeSpan.FromSeconds(3));
            var notified = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            events.Warning += message => notified.TrySetResult(message);
            ((Channel<object>)field.GetValue(events)!).Writer.TryComplete(new IOException("fixture event writer I/O failure"));
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(events.Incomplete && events.Warnings[0].Contains("fixture event writer I/O failure"), "writer fault warns during capture");
            await events.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(failedProject.PackagePath, "events", "recording-events.json")));
            Check(report.RootElement.GetProperty("incomplete").GetBoolean(), "writer fault still produces safe manifest");
            Check(File.Exists(Path.Combine(failedProject.PackagePath, "events", "pointer.jsonl")), "writer fault retains partial raw events");
        }

        var normalProject = Project("event-normal-finish");
        using (var session = new RecordingSession())
        {
            var events = new RecordingEvents(session, target, null, normalProject.PackagePath);
            await events.Ready.WaitAsync(TimeSpan.FromSeconds(3));
            await events.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Check(!events.Incomplete && events.Warnings.Length == 0, "normal event finish remains successful");
        }
        File.WriteAllText(Path.Combine(normalProject.PackagePath, "events", "pointer.jsonl"),
            "{\"time\":1,\"kind\":\"leftClick\",\"location\":{\"x\":0.5,\"y\":0.5},\"outside\":false}");
        Check(SceneCompositor.LoadRecordingData(normalProject, EditorState.Initial(3)).PointerEvents.Length == 1,
            "complete event manifest permits valid pointer replay");

        var missingProject = Project("event-missing-report");
        Directory.CreateDirectory(Path.Combine(missingProject.PackagePath, "events"));
        File.WriteAllText(Path.Combine(missingProject.PackagePath, "events", "pointer.jsonl"), broken);
        Check(SceneCompositor.LoadRecordingData(missingProject, EditorState.Initial(3)).PointerEvents.Length == 0,
            "missing report cannot certify partial event files");
        return passed;
    }
}

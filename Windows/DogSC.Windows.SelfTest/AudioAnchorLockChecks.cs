using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;

namespace DogSC.Windows.SelfTest;

internal static class AudioAnchorLockChecks
{
    public static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var application = LiveMainChecks.CreateApplication();
        var dispatcher = Dispatcher.CurrentDispatcher;
        int exit = 1;
        _ = dispatcher.InvokeAsync(async () =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var main = new MainWindow();
            using var screen = new RecordingSession();
            using var audio = new WasapiAudioSession();
            using var writerStarted = new ManualResetEventSlim();
            var cases = new List<string>();
            var anchors = new List<object>();
            Exception? writerError = null;
            Thread? writer = null;
            void Check(bool valid, string name)
            {
                if (!valid) throw new Exception("FAIL: " + name);
                cases.Add(name);
            }
            try
            {
                await ((Task)typeof(MainWindow).GetField("_deviceDiscovery", flags)!.GetValue(main)!).WaitAsync(TimeSpan.FromSeconds(20));
                typeof(MainWindow).GetField("_session", flags)!.SetValue(main, screen);
                typeof(WasapiAudioSession).GetProperty(nameof(WasapiAudioSession.Format))!
                    .SetValue(audio, new WasapiAudioFormat(48000, 2, 16, 4, 1));
                var trackType = typeof(MainWindow).GetNestedType("AudioTrack", BindingFlags.NonPublic)!;
                var track = Activator.CreateInstance(trackType, ["system", "media/system.wav", audio, anchors])!;
                var add = typeof(MainWindow).GetMethod("RecordAudioAnchor", flags)!;
                var gate = typeof(RecordingSession).GetField("gate", flags)!.GetValue(screen)!;
                writer = new Thread(() =>
                {
                    writerStarted.Set();
                    try { add.Invoke(main, [track, RecordingState.Recording, screen]); }
                    catch (Exception error) { writerError = error; }
                }) { IsBackground = true, Name = "DogSC audio anchor lock fixture" };
                bool waitingOnGate = false, anchorsFree = false;
                lock (gate)
                {
                    writer.Start();
                    if (!writerStarted.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("Anchor fixture writer did not start.");
                    var elapsed = Stopwatch.StartNew();
                    while ((writer.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0 && elapsed.Elapsed < TimeSpan.FromSeconds(2))
                        Thread.Sleep(2);
                    waitingOnGate = (writer.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0;
                    anchorsFree = Monitor.TryEnter(anchors, TimeSpan.FromMilliseconds(100));
                    if (anchorsFree) Monitor.Exit(anchors);
                }
                if (!writer.Join(TimeSpan.FromSeconds(2))) throw new TimeoutException("Anchor fixture writer did not finish after its gate was released.");
                if (writerError is not null) throw writerError;
                Check(waitingOnGate, "the background anchor callback is held at the recording gate");
                Check(anchorsFree, "a callback waiting for the recording gate does not own the anchors lock");
                Check(anchors.Count == 1, "the callback adds exactly one anchor after the gate is released");
                var anchor = anchors[0];
                Check((long)anchor.GetType().GetProperty("hostTimestamp")!.GetValue(anchor)! > 0
                    && (double)anchor.GetType().GetProperty("sourceTime")!.GetValue(anchor)! >= 0
                    && (double)anchor.GetType().GetProperty("audioTime")!.GetValue(anchor)! >= 0,
                    "the released callback retains its two times and host timestamp");
                add.Invoke(main, [track, RecordingState.Complete, screen]);
                Check(anchors.Count == 1, "completed state does not append an audio anchor");
                File.WriteAllText(Path.Combine(root, "audio-anchor-lock-result.json"), JsonSerializer.Serialize(new
                    { success = true, controlledContention = true, actualDevices = false, checks = cases.Count, cases },
                    new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Audio anchor lock checks passed: {cases.Count}.");
                exit = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally
            {
                if (writer is { IsAlive: true }) writer.Join(TimeSpan.FromSeconds(2));
                typeof(MainWindow).GetField("_session", flags)!.SetValue(main, null);
                main.RequestExit();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run();
        GC.KeepAlive(application);
        return exit;
    }
}

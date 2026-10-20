using System.IO;
using System.Text.Json;

namespace DogSC.Windows.SelfTest;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => args.Contains("--exception-ui") ? ExceptionChecks.RunUi(args[1])
        : args.Contains("--live-audio-gain") ? ReportChecks(LiveAudioGainChecks.RunAsync().GetAwaiter().GetResult())
        : args.Contains("--live-camera-toggle") ? ReportChecks(LiveCameraToggleChecks.RunAsync(args[1]).GetAwaiter().GetResult())
        : args.Contains("--live-recording-controls") ? LiveRecordingControlsChecks.Run(args[1], args.Contains("--start-silent"))
        : args.Contains("--live-controls-failures") ? LiveControlsFailureChecks.Run(args[1])
        : args.Contains("--save-close") ? SaveCloseChecks.Run(args[1])
        : args.Contains("--save-close-live") ? LiveMainChecks.Run(args[1], closeDuringSave: true)
        : args.Contains("--audio-anchor-lock") ? AudioAnchorLockChecks.Run(args[1])
        : args.Contains("--release-data") ? ReleaseDataChecks.Run(args[1])
        : args.Contains("--editor-regression") ? EditorRegressionChecks.Run(args[1], args[2])
        : args.Contains("--legacy-null") ? LegacyImportChecks.RunAsync(args[1]).GetAwaiter().GetResult()
        : args.Contains("--container-import") ? ReportChecks(ContainerImportChecks.RunAsync(args[1]).GetAwaiter().GetResult())
        : args.Contains("--scratch-cleanup") ? ReportChecks(ScratchCleanupChecks.Run(args[1]))
        : args.Contains("--scene-post-review") ? ReportChecks(ScenePostReviewChecks.RunAsync(args[1], args[2]).GetAwaiter().GetResult())
        : args.Contains("--scene-focus") ? ReportChecks(SceneRegressionChecks.RunFocusAsync().GetAwaiter().GetResult())
        : args.Contains("--scene-camera-height") ? ReportChecks(SceneRegressionChecks.RunCameraHeightAsync().GetAwaiter().GetResult())
        : args.Contains("--scene-camera-opening") ? ReportChecks(SceneRegressionChecks.RunCameraOpeningAsync().GetAwaiter().GetResult())
        : args.Contains("--motion-core") ? ReportChecks(MotionContinuityChecks.RunCore(args[1]))
        : args.Contains("--motion-camera") ? ReportChecks(MotionContinuityChecks.RunCamera(args[1]))
        : args.Contains("--motion-layout") ? ReportChecks(MotionContinuityChecks.RunLayout(args[1]))
        : args.Contains("--camera-render-bench") ? CameraRenderBench.RunAsync(args[1], args[2]).GetAwaiter().GetResult()
        : args.Contains("--camera-render-verify") ? CameraRenderBench.RunAsync(args[1], args[2], render: false).GetAwaiter().GetResult()
        : args.Contains("--camera-raster-cache") ? CameraRasterCacheChecks.RunAsync(args[1]).GetAwaiter().GetResult() > 0 ? 0 : 1
        : args.Contains("--realtime-camera-overlay") ? RealtimeCameraOverlayChecks.RunAsync(args[1]).GetAwaiter().GetResult() > 0 ? 0 : 1
        : args.Contains("--realtime-overlay-capture") ? RealtimeOverlayCaptureChecks.Run(args[1])
        : args.Contains("--toolbar-appearance") ? ToolbarAppearanceChecks.Run(args[1])
        : args.Contains("--contracts-ui") ? ContractUiChecks.Run(args[1])
        : args.Contains("--contracts-language-load") ? ContractUiChecks.Run(args[1], args[2])
        : args.Contains("--bluetooth-audio") ? AudioRateChecks.RunBluetoothAsync(args[1]).GetAwaiter().GetResult() > 0 ? 0 : 1
        : args.Contains("--bluetooth-verify") ? AudioRateChecks.RunBluetoothVerificationAsync(args[1]).GetAwaiter().GetResult() > 0 ? 0 : 1
        : args.Contains("--audio-rate-check") ? AudioRateChecks.RunAsync(args[1]).GetAwaiter().GetResult() > 0 ? 0 : 1
        : args.Contains("--camera-fast-save") ? LiveMainChecks.Run(args[1], frameRate: 120, recordingHeight: 0, phaseMilliseconds: 7500, composeFinal: false)
        : args.Contains("--main-live") ? LiveMainChecks.Run(args[1])
        : args.Contains("--main-live-verify") ? LiveMainChecks.Run(args[1], capture: false)
        : args.Contains("--publication-ui") ? PublicationUiChecks.Run(args[1])
        : args.Contains("--editor-ui") ? EditorFailureChecks.Run(args[1])
        : args.Contains("--audio-live") ? AudioFailureChecks.RunLiveAsync(args[1]).GetAwaiter().GetResult()
        : args.Contains("--audio-extract") ? MediaChecks.RunExtractionAsync(args[1], args[2]).GetAwaiter().GetResult()
        : args.Contains("--media") ? MediaChecks.RunAsync(args[1], args[2]).GetAwaiter().GetResult() > 0 ? 0 : 1
        : RunAsync(args).GetAwaiter().GetResult();

    private static int ReportChecks(int checks)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { success = checks > 0, checks }));
        return checks > 0 ? 0 : 1;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "dogsc-windows-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var results = new Dictionary<string, int>();
        try
        {
            results["finalization"] = await FinalizeTimeoutChecks.RunAsync();
            results["cameraRelease"] = await CameraReleaseChecks.RunAsync(root);
            results["exceptions"] = ExceptionChecks.Run(root);
            results["audioFailure"] = await AudioFailureChecks.RunAsync();
            results["exportGate"] = await ExportGateChecks.RunAsync();
            results["optionalAudio"] = await OptionalAudioChecks.RunAsync(root);
            results["sceneBounds"] = await SceneBoundaryChecks.RunAsync();
            if (!args.Contains("--finalize")) results["events"] = await EventFinishChecks.RunAsync(root);
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, checks = results.Values.Sum(), results }));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, recursive: true); }
    }
}

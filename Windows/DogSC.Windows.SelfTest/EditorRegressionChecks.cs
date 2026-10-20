using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Threading;
using DogSC.Core;

namespace DogSC.Windows.SelfTest;

internal static class EditorRegressionChecks
{
    internal const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    public static int Run(string root, string phase)
    {
        Directory.CreateDirectory(root);
        var application = LiveMainChecks.CreateApplication();
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var watchdog = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(45), watchdog.Token); }
            catch (OperationCanceledException) { return; }
            Console.Error.WriteLine("Own editor regression process timed out."); Environment.Exit(2);
        });
        int exit = 1;
        _ = dispatcher.InvokeAsync(async () =>
        {
            EditorWindow? editor = null;
            var cases = new List<string>();
            void Check(bool value, string name)
            {
                if (!value) throw new Exception("FAIL: " + name);
                cases.Add(name); Console.WriteLine("PASS editor regression: " + name);
            }
            try
            {
                var project = ProjectStore.Create(root, phase, new CaptureMetadata("screen", "fixture", 30, false, false, null));
                await MediaExport.RunWorkerAsync((_, _) =>
                {
                    Mf.Check(Mf.MFStartup(0x20070, 0));
                    try { AudioRateChecks.WriteSilentVideo(project.MediaPath, 3); }
                    finally { Mf.MFShutdown(); }
                }, "Editor regression fixture video");
                project = ProjectStore.Complete(project, TimeSpan.FromSeconds(3));
                if (phase is "preview" or "restore")
                {
                    var cameraPath = Path.Combine(project.PackagePath, "media", "camera.mp4");
                    File.Copy(project.MediaPath, cameraPath);
                    File.WriteAllText(Path.Combine(project.PackagePath, "media", "camera-sync.json"), JsonSerializer.Serialize(new
                    {
                        format = "dogsc.windows.camera", version = 1, relativePath = "media/camera.mp4",
                        startOffset = 0, sourceEndTime = 3, syncAnchors = Array.Empty<object>()
                    }));
                    EditorWindow.AtomicJson(Path.Combine(project.PackagePath, "editor.json"),
                        EditorState.Initial(3) with { Camera = new CameraStyle { Enabled = true } });
                }
                if (phase == "preview")
                {
                    var cameraPath = Path.Combine(project.PackagePath, "media", "camera.mp4");
                    editor = new EditorWindow(project); editor.Show(); await Task.Delay(100);
                    Check(Get<MediaFrameReader>(editor, "_reader") is not null && Get<CameraFrameReader>(editor, "_cameraReader") is not null,
                        "the actual screen and independent camera videos decode in the loaded editor");
                    Get<CameraFrameReader>(editor, "_cameraReader")!.Dispose(); Set(editor, "_cameraReader", null);
                    File.Move(cameraPath, cameraPath + ".held");
                    Get<Slider>(editor, "_position")!.Value = 1;
                    var message = Get<TextBlock>(editor, "_status")!.Text;
                    Check(editor.IsVisible && message.Contains("摄像头") && message.Contains(cameraPath),
                        "dragging the playhead with missing camera media reports its Chinese location and keeps the editor open");
                    Set(editor, "_state", editor.State with { Camera = editor.State.Camera with { Enabled = false } });
                    Get<Slider>(editor, "_position")!.Value = 1.5;
                    Check(editor.IsVisible && Get<TextBlock>(editor, "_status")!.Text == message,
                        "a successful preview does not erase the prior failure message");
                    File.Move(cameraPath + ".held", cameraPath);
                    Set(editor, "_state", editor.State with { Camera = editor.State.Camera with { Enabled = true } });
                    Get<Slider>(editor, "_position")!.Value = 2;
                    Check(Get<CameraFrameReader>(editor, "_cameraReader") is not null,
                        "restoring the same camera media permits preview without restarting the editor");
                    Check(Get<TextBlock>(editor, "_transportClock")!.Text.Contains("2.00"),
                        "transport time still updates independently of error feedback");
                }
                else if (phase == "selection")
                {
                    editor = new EditorWindow(project); editor.Show(); await Task.Delay(100);
                    Invoke(editor, "Commit", editor.State with
                    {
                        Layers = [new("遮盖", .1, 1, .1, .1, .2, .2), new("遮盖", .5, 2, .3, .3, .2, .2)],
                        ScreenMotionClips = [new(Guid.NewGuid(), new(.1, .8), new(new(.5, .5))), new(Guid.NewGuid(), new(1, 1), new(new(.7, .6)))]
                    });
                    var layers = Get<ListBox>(editor, "_layers")!;
                    layers.SelectedIndex = 1; Get<TextBox>(editor, "_layerX")!.Text = ".2";
                    Click(editor, "更新选中效果");
                    Check(layers.SelectedIndex == 1 && editor.State.Layers[1].X == .2, "updating an effect retains its selected slot");
                    Check(Get<TextBox>(editor, "_layerStart")!.Text == "0.5", "effect fields retain selected timing after refresh");
                    Get<TextBox>(editor, "_layerX")!.Text = ".4"; Click(editor, "更新选中效果");
                    Check(layers.SelectedIndex == 1 && editor.State.Layers[1].X == .4, "a second effect update applies to the same selected effect");
                    Click(editor, "复制效果 Ctrl+C"); Check(Get<SceneLayer>(editor, "_clipboard")!.X == .4, "copy after repeated update uses the selected effect");
                    Click(editor, "删除效果"); Check(layers.SelectedIndex == 0 && editor.State.Layers.Length == 1, "delete selects the remaining neighbor without a stale index");
                    Click(editor, "只粘贴属性 Ctrl+Shift+V"); Check(layers.SelectedIndex == 0 && editor.State.Layers[0].X == .4, "pasting attributes applies and retains selection");
                    Invoke(editor, "Undo"); Invoke(editor, "Redo"); Check(layers.SelectedIndex == 0, "undo and redo preserve the valid selected effect slot");
                    var motions = Get<ListBox>(editor, "_motions")!;
                    motions.SelectedIndex = 1; var id = editor.State.ScreenMotionClips[1].Id;
                    Get<TextBox>(editor, "_motionX")!.Text = ".6"; Click(editor, "更新选中运镜");
                    Check(motions.SelectedIndex == 1 && editor.State.ScreenMotionClips[1].Target.Position.X == .6, "motion update preserves its selection");
                    Get<TextBox>(editor, "_motionX")!.Text = ".8"; Click(editor, "更新选中运镜");
                    Check(editor.State.ScreenMotionClips[1].Target.Position.X == .8, "the next motion update still applies");
                    Invoke(editor, "Commit", editor.State with { ScreenMotionClips = [new(Guid.NewGuid(), new(0, .1), new(new(.5, .5))), .. editor.State.ScreenMotionClips] });
                    Check(motions.SelectedIndex == 2 && editor.State.ScreenMotionClips[motions.SelectedIndex].Id == id, "refresh restores motion selection by Id when its index changes");
                    Click(editor, "删除运镜 / 组合"); Check(motions.SelectedIndex < 0 && editor.State.ScreenMotionClips.All(c => c.Id != id), "deleting a motion clears the removed Id");
                    layers.SelectedIndex = -1;
                    foreach (string button in new[] { "更新选中效果", "删除效果", "复制效果 Ctrl+C", "只粘贴属性 Ctrl+Shift+V", "更新选中运镜", "删除运镜 / 组合" })
                    {
                        Click(editor, button); Check(Get<TextBlock>(editor, "_status")!.Text.Contains("请先选中"), button + " gives feedback without selection");
                    }
                    Get<Slider>(editor, "_position")!.Value = 1.1;
                    Check(Get<TextBlock>(editor, "_status")!.Text.Contains("请先选中"), "preview does not replace the no-selection feedback");
                }
                else if (phase == "restore")
                {
                    editor = new EditorWindow(project); editor.Show(); await Task.Delay(100);
                    Invoke(editor, "Commit", editor.State with
                    {
                        Segments = [new(Guid.NewGuid(), .5, 1.5, 2)],
                        Style = editor.State.Style with { Background = "#FF123456", FocusX = .3 },
                        Layers = [new("遮盖", .1, .6, .1, .2, .3, .2, Opacity: .7, SourceSpace: false)],
                        ZoomClips = [new(Guid.NewGuid(), .1, .5, 1.6, new(.7, .6), EnterDuration: .1, ExitDuration: .1)],
                        ScreenMotionClips = [new(Guid.NewGuid(), new(.1, .3, .1, .1), new(new(.6, .6)))],
                        CameraMotionClips = [new(Guid.NewGuid(), new(.2, .3, .1, .1), new(new(.8, .7)))],
                        Camera = new CameraStyle { Enabled = true, X = .4, Width = .25, Mirror = true },
                        CameraPlacements = [new(0, .1, .2, .3, .2, 4d / 3)],
                        Opening = new(true, "聚拢", Duration: 1, Stagger: .1), Audio = new(.7, .6, .5, true, false, .1, -.1),
                        Pointer = new(Scale: 2, Hidden: true, ClickFeedback: "关闭"),
                        RecordingMarkers = [new(Guid.NewGuid(), 1, 1)]
                    });
                    var previous = editor.State; var undo = Get<Stack<EditorState>>(editor, "_undo")!; int count = undo.Count;
                    Click(editor, "恢复裁剪/速度");
                    Check(editor.State.Segments.Length == 1 && editor.State.Segments[0].SourceStart == 0
                        && editor.State.Segments[0].SourceDuration == 3 && editor.State.Segments[0].PlaybackRate == 1,
                        "restore changes only the source timeline to full duration and normal speed");
                    var restoredLayer = editor.State.Layers.Single(); var restoredZoom = editor.State.ZoomClips.Single();
                    Check(editor.State.Style == previous.Style
                        && Math.Abs(restoredLayer.Start - .7) < TimelineMap.Epsilon && Math.Abs(restoredLayer.End - 1.7) < TimelineMap.Epsilon
                        && (restoredLayer with { Start = previous.Layers[0].Start, End = previous.Layers[0].End }) == previous.Layers[0]
                        && Math.Abs(restoredZoom.StartTime - .7) < TimelineMap.Epsilon && Math.Abs(restoredZoom.EndTime - 1.5) < TimelineMap.Epsilon
                        && (restoredZoom with { StartTime = previous.ZoomClips[0].StartTime, EndTime = previous.ZoomClips[0].EndTime }) == previous.ZoomClips[0],
                        "restore keeps style and authored effect properties while mapping their accelerated output times back to source");
                    Check(editor.State.Camera == previous.Camera && editor.State.CameraPlacements.SequenceEqual(previous.CameraPlacements)
                        && editor.State.Opening == previous.Opening && editor.State.Audio == previous.Audio && editor.State.Pointer == previous.Pointer,
                        "restore retains camera, placement, opening, audio, and pointer settings");
                    Check(editor.State.ScreenMotionClips.Length == 1 && editor.State.ScreenMotionClips[0].Id == previous.ScreenMotionClips[0].Id
                        && editor.State.CameraMotionClips.Length == 1 && editor.State.CameraMotionClips[0].Id == previous.CameraMotionClips[0].Id,
                        "restore retains motion identities while remapping them to the restored source timeline");
                    Check(editor.State.RecordingMarkers!.SequenceEqual(previous.RecordingMarkers!), "restore preserves recording markers");
                    Check(Get<TextBox>(editor, "_start")!.Text == "0" && Get<TextBox>(editor, "_end")!.Text == "3"
                        && Get<TextBox>(editor, "_speed")!.Text == "1", "the restored source controls immediately match the new state");
                    Check(Get<CheckBox>(editor, "_recordedPlacementCheck")!.IsChecked == previous.Camera.UseRecordedPlacement,
                        "the rebuilt camera property controls match the retained state");
                    Check(undo.Count == count + 1, "restore creates one undo entry");
                    var restored = editor.State; Invoke(editor, "Undo"); Check(editor.State == previous, "undo restores all prior edits together");
                    Invoke(editor, "Redo"); Check(editor.State == restored, "redo restores the limited timeline change");
                    count = undo.Count; Click(editor, "恢复裁剪/速度"); Check(undo.Count == count, "restoring an already restored timeline does not append undo");
                }
                else if (phase == "restore-slow")
                {
                    editor = new EditorWindow(project); editor.Show(); await Task.Delay(100);
                    Check(Get<MediaFrameReader>(editor, "_reader") is not null && Get<Image>(editor, "_preview")!.Source is not null,
                        "the restore boundary fixture decodes the real three-second original");
                    var zoom = new ZoomAnimationClip(Guid.Parse("3d40e026-7d26-4e21-a2d7-000000000004"), 4, 5, 1.6,
                        new(.6, .4), EnterDuration: .2, ExitDuration: .2);
                    var layer = new SceneLayer("遮盖", 4, 5, .1, .2, .2, .2, Opacity: .4, SourceSpace: false);
                    Invoke(editor, "Commit", editor.State with
                    {
                        Segments = [new(editor.State.Segments[0].Id, 0, 3, .5)], ZoomClips = [zoom], Layers = [layer]
                    });
                    var previous = editor.State; var undo = Get<Stack<EditorState>>(editor, "_undo")!; int count = undo.Count;
                    Check(previous.Duration == 6 && previous.ZoomClips.Single().StartTime == 4 && previous.Layers.Single().End == 5,
                        "half speed permits valid authored zoom and effect intervals beyond the source duration");
                    Click(editor, "恢复裁剪/速度");
                    Check(editor.State.Duration == 3 && editor.State.Segments.Length == 1
                        && editor.State.Segments[0].SourceStart == 0 && editor.State.Segments[0].SourceDuration == 3
                        && editor.State.Segments[0].PlaybackRate == 1,
                        "restoring half-speed footage returns to the whole original at normal speed");
                    var restoredZoom = editor.State.ZoomClips.Single(); var restoredLayer = editor.State.Layers.Single();
                    Check(restoredZoom.Id == zoom.Id && Math.Abs(restoredZoom.StartTime - 2) < TimelineMap.Epsilon
                        && Math.Abs(restoredZoom.EndTime - 2.5) < TimelineMap.Epsilon
                        && restoredZoom.Scale == zoom.Scale && restoredZoom.Focus == zoom.Focus,
                        "the four-to-five-second zoom follows its source interval to two-to-two-point-five seconds");
                    Check(Math.Abs(restoredLayer.Start - 2) < TimelineMap.Epsilon && Math.Abs(restoredLayer.End - 2.5) < TimelineMap.Epsilon
                        && (restoredLayer with { Start = layer.Start, End = layer.End }) == layer,
                        "the matching effect follows source time and retains geometry, opacity, and canvas coordinates");
                    Check(undo.Count == count + 1, "the slowed-timeline restoration records one undo step");
                    string restoredStatus = Get<TextBlock>(editor, "_status")!.Text;
                    Get<Slider>(editor, "_position")!.Value = 2.25;
                    Check(editor.IsVisible && Get<Image>(editor, "_preview")!.Source is not null
                        && Get<TextBlock>(editor, "_status")!.Text == restoredStatus,
                        "the remapped zoom and effect preview without replacing success with a validation failure");
                    Check(Get<TextBox>(editor, "_start")!.Text == "0" && Get<TextBox>(editor, "_end")!.Text == "3"
                        && Get<TextBox>(editor, "_speed")!.Text == "1", "restored source fields show the actual duration and speed");
                    var restored = editor.State;
                    Invoke(editor, "Undo");
                    Check(editor.State == previous && editor.State.Duration == 6 && editor.State.ZoomClips.Single() == zoom
                        && editor.State.Layers.Single() == layer, "undo restores the entire slowed timeline and both original effect intervals");
                    Invoke(editor, "Redo"); Check(editor.State == restored, "redo restores the complete source-time remapping");
                    editor.Save(); editor.Close();
                    project = ProjectStore.Open(project.PackagePath);
                    editor = new EditorWindow(project); editor.Show(); await Task.Delay(100);
                    Check(editor.State.Duration == 3 && editor.State.ZoomClips.Single().Id == zoom.Id
                        && Math.Abs(editor.State.ZoomClips.Single().StartTime - 2) < TimelineMap.Epsilon
                        && Math.Abs(editor.State.ZoomClips.Single().EndTime - 2.5) < TimelineMap.Epsilon
                        && Math.Abs(editor.State.Layers.Single().Start - 2) < TimelineMap.Epsilon
                        && Math.Abs(editor.State.Layers.Single().End - 2.5) < TimelineMap.Epsilon
                        && editor.State.Layers.Single().Opacity == .4 && !editor.State.Layers.Single().SourceSpace,
                        "the restored timeline and independently authored settings survive actual save and reopen");

                    string background = editor.State.Style.Background;
                    Invoke(editor, "Commit", editor.State with { Style = editor.State.Style with { Background = "#FF224466" } });
                    var unchanged = editor.State; undo = Get<Stack<EditorState>>(editor, "_undo")!; count = undo.Count;
                    Click(editor, "恢复裁剪/速度");
                    Check(editor.State == unchanged && undo.Count == count,
                        "restoring an already complete original does not insert an undo after an unrelated background edit");
                    string unchangedStatus = Get<TextBlock>(editor, "_status")!.Text;
                    Check(unchangedStatus.Contains("已是完整原片与1倍速度") && !unchangedStatus.Contains("可撤销"),
                        "a no-op restore reports the existing full original and does not promise a new undo action");
                    Invoke(editor, "Undo");
                    Check(editor.State.Style.Background == background && editor.State.Duration == 3
                        && Math.Abs(editor.State.ZoomClips.Single().StartTime - 2) < TimelineMap.Epsilon,
                        "undo after no-op still belongs solely to the preceding background edit");
                    Invoke(editor, "Redo");

                    var crossing = zoom with { StartTime = 1, EndTime = 2 };
                    Invoke(editor, "Commit", editor.State with
                    {
                        Segments = [new(Guid.Parse("3d40e026-7d26-4e21-a2d7-000000000005"), 1.5, 1.5),
                            new(Guid.Parse("3d40e026-7d26-4e21-a2d7-000000000006"), 0, 1.5)],
                        ZoomClips = [crossing], Layers = []
                    });
                    previous = editor.State; count = undo.Count;
                    Click(editor, "恢复裁剪/速度");
                    Check(editor.State == previous && undo.Count == count && editor.State.ZoomClips.Single() == crossing,
                        "restoration rejects a zoom crossing reversed source pieces instead of silently dropping its settings");
                    Check(Get<TextBlock>(editor, "_status")!.Text.Contains("缩放"),
                        "the rejected restoration explains the unmappable authored zoom in Chinese");
                    editor.Save(); editor.Close();
                    project = ProjectStore.Open(project.PackagePath);
                    editor = new EditorWindow(project); editor.Show(); await Task.Delay(100);
                    Check(editor.State.Segments.SequenceEqual(previous.Segments) && editor.State.ZoomClips.Single() == crossing,
                        "saving after the rejected restoration preserves the existing reordered source and zoom");
                }
                else if (phase == "tail-zoom")
                {
                    editor = new EditorWindow(project); editor.Show(); await Task.Delay(100);
                    var zoom = new ZoomAnimationClip(Guid.NewGuid(), 1, 2.5, 1.6, new(.6, .4), EnterDuration: .2, ExitDuration: .2);
                    Invoke(editor, "Commit", editor.State with
                    {
                        Segments = [new(Guid.NewGuid(), 0, 1.5), new(Guid.NewGuid(), 1.5, 1.5)], ZoomClips = [zoom]
                    });
                    Get<ListBox>(editor, "_segments")!.SelectedIndex = 1;
                    Click(editor, "删除片段");
                    Check(editor.State.Duration == 1.5 && editor.State.ZoomClips.Length == 1 && editor.State.ZoomClips[0].Id == zoom.Id
                        && editor.State.ZoomClips[0].EndTime <= 1.5, "tail deletion keeps the visible crossing zoom within the shortened timeline");
                    Click(editor, "应用裁剪/速度");
                    Check(editor.State.ZoomClips.Length == 1 && editor.State.ZoomClips[0].Id == zoom.Id,
                        "applying the unchanged segment after tail deletion does not discard its zoom");
                    Invoke(editor, "Undo"); Invoke(editor, "Redo");
                    Check(editor.State.ZoomClips.Length == 1 && editor.State.ZoomClips[0].EndTime <= editor.State.Duration,
                        "undo and redo keep the clipped authored zoom valid");
                }
                else if (phase == "move-zoom")
                {
                    editor = new EditorWindow(project); editor.Show(); await Task.Delay(100);
                    Invoke(editor, "Commit", editor.State with
                    {
                        Segments = [new(Guid.NewGuid(), 0, 1.5), new(Guid.NewGuid(), 1.5, 1.5)],
                        ZoomClips = [new(Guid.NewGuid(), 1, 2.5, 1.6, new(.6, .4), EnterDuration: .2, ExitDuration: .2)]
                    });
                    Get<ListBox>(editor, "_segments")!.SelectedIndex = 0;
                    var previous = editor.State; var undo = Get<Stack<EditorState>>(editor, "_undo")!; int count = undo.Count;
                    Click(editor, "向后");
                    Check(editor.State == previous && undo.Count == count && Get<TextBlock>(editor, "_status")!.Text.Contains("缩放"),
                        "a destructive move is rejected with a Chinese explanation and does not change state or undo");
                    Invoke(editor, "Commit", editor.State with { ZoomClips = [new(Guid.NewGuid(), .2, .7, 1.6, new(.6, .4), EnterDuration: .1, ExitDuration: .1)] });
                    var safe = editor.State; var id = safe.Segments[0].Id;
                    Click(editor, "向后");
                    Check(editor.State.Segments[1].Id == id && Get<ListBox>(editor, "_segments")!.SelectedIndex == 1,
                        "a safe move changes order and keeps the moved source segment selected");
                    Click(editor, "向前");
                    var returned = editor.State.ZoomClips.Single(); var expected = safe.ZoomClips.Single();
                    Check(editor.State.Segments.SequenceEqual(safe.Segments)
                        && Math.Abs(returned.StartTime - expected.StartTime) < TimelineMap.Epsilon && Math.Abs(returned.EndTime - expected.EndTime) < TimelineMap.Epsilon
                        && (returned with { StartTime = expected.StartTime, EndTime = expected.EndTime }) == expected
                        && Get<ListBox>(editor, "_segments")!.SelectedIndex == 0, "the opposite button returns the selected source and complete zoom properties");
                }
                else if (phase == "terminal-markers")
                {
                    var terminal = new RecordingMarker(Guid.Parse("3d40e026-7d26-4e21-a2d7-000000000003"), 3, 1);
                    editor = new EditorWindow(project); editor.Show(); await Task.Delay(100);
                    Check(Get<MediaFrameReader>(editor, "_reader") is not null && Get<Image>(editor, "_preview")!.Source is not null,
                        "the generated three-second screen original decodes in the actual editor");
                    Invoke(editor, "Commit", editor.State with { RecordingMarkers = [terminal] });
                    var markerButton = Get<Button>(editor, "_markerButton")!;
                    var popup = Get<System.Windows.Controls.Primitives.Popup>(editor, "_markerPopup")!;
                    markerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(markerButton.IsVisible && popup.IsOpen, "the terminal marker has a visible list entry point");
                    var terminalSeek = Descendants(popup.Child!).OfType<Button>().SingleOrDefault(button =>
                        button.Tag is TimelineRecordingMarker item && item.Marker.Id == terminal.Id);
                    Check(terminalSeek is { IsEnabled: true }, "the exact recording-end marker offers an enabled seek button");
                    Check(Descendants(popup.Child!).OfType<Button>().Any(button => button.Tag is Guid id && id == terminal.Id),
                        "the terminal marker has a delete button tagged with its original Guid");
                    Get<Slider>(editor, "_position")!.Value = .25;
                    string beforeSeekStatus = Get<TextBlock>(editor, "_status")!.Text;
                    terminalSeek!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    double seekTime = Get<Slider>(editor, "_position")!.Value;
                    Check(!popup.IsOpen && seekTime > 2.99 && seekTime < 3 && editor.IsVisible
                        && Get<Image>(editor, "_preview")!.Source is not null,
                        "clicking the terminal marker seeks to the last retained source frame without closing the editor");
                    Check(editor.State.RecordingMarkers!.Single() == terminal, "seeking does not rewrite the exact source-end anchor");
                    Check(Get<TextBlock>(editor, "_status")!.Text == beforeSeekStatus,
                        "terminal seeking does not leave a preview failure hidden behind the previous image");

                    Get<ListBox>(editor, "_segments")!.SelectedIndex = 0;
                    Get<TextBox>(editor, "_start")!.Text = "0"; Get<TextBox>(editor, "_end")!.Text = "2";
                    Get<TextBox>(editor, "_speed")!.Text = "1"; Click(editor, "应用裁剪/速度");
                    Check(editor.State.Duration == 2 && editor.State.RecordingMarkers!.Single() == terminal,
                        "the actual trim control removes the source tail while retaining its marker identity");
                    markerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var deleteTrimmed = Descendants(popup.Child!).OfType<Button>().SingleOrDefault(button =>
                        button.Tag is Guid id && id == terminal.Id);
                    Check(popup.IsOpen && deleteTrimmed is { IsEnabled: true },
                        "the trimmed-out marker remains visible and deletable through its Guid-tagged button");
                    Check(!Descendants(popup.Child!).OfType<Button>().Any(button => button.IsEnabled
                        && (button.Tag is TimelineRecordingMarker mapped && mapped.Marker.Id == terminal.Id
                            || button.Tag is RecordingMarker marker && marker.Id == terminal.Id)),
                        "the trimmed-out marker does not offer an enabled seek to missing source footage");
                    deleteTrimmed!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(editor.State.Duration == 2 && editor.State.RecordingMarkers!.Length == 0,
                        "clicking the trimmed marker delete button removes only that marker");
                    Check(!popup.IsOpen && markerButton.Visibility == Visibility.Collapsed,
                        "deleting the last marker closes the list and removes its empty entry point");
                    Click(editor, "撤销 Ctrl+Z");
                    Check(editor.State.Duration == 2 && editor.State.RecordingMarkers!.Single() == terminal,
                        "undo restores the trimmed-out marker without undoing the trim");
                    markerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(Descendants(popup.Child!).OfType<Button>().Any(button => button.IsEnabled && button.Tag is Guid id && id == terminal.Id),
                        "the restored unmapped marker is deletable after undo");
                    popup.IsOpen = false; Click(editor, "撤销 Ctrl+Z");
                    Check(editor.State.Duration == 3 && editor.State.RecordingMarkers!.Single() == terminal,
                        "a second undo restores the source tail and the original terminal marker");
                    markerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    terminalSeek = Descendants(popup.Child!).OfType<Button>().SingleOrDefault(button =>
                        button.Tag is TimelineRecordingMarker item && item.Marker.Id == terminal.Id);
                    Check(terminalSeek is { IsEnabled: true }, "restoring the source tail makes the same marker seekable again");
                    terminalSeek!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(Get<Slider>(editor, "_position")!.Value > 2.99 && Get<Slider>(editor, "_position")!.Value < 3,
                        "the restored terminal marker still seeks inside the final source frame");
                    editor.Save(); editor.Close(); editor = new EditorWindow(project); editor.Show(); await Task.Delay(100);
                    Check(editor.State.RecordingMarkers!.Single() == terminal && editor.State.Duration == 3,
                        "the terminal marker identity and exact source-end time survive save and reopen");
                    markerButton = Get<Button>(editor, "_markerButton")!; popup = Get<System.Windows.Controls.Primitives.Popup>(editor, "_markerPopup")!;
                    markerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(Descendants(popup.Child!).OfType<Button>().Any(button => button.IsEnabled
                        && button.Tag is TimelineRecordingMarker item && item.Marker.Id == terminal.Id),
                        "the reopened editor exposes the same enabled terminal marker seek action");
                    popup.IsOpen = false;
                }
                else if (phase == "fields")
                {
                    editor = new EditorWindow(project); editor.Show(); await Task.Delay(100);
                    Invoke(editor, "Commit", editor.State with { Layers = [new("遮盖", .1, 2, .1, .2, .2, .2, Opacity: .4, SourceSpace: false)] });
                    Get<ListBox>(editor, "_layers")!.SelectedIndex = 0; Get<TextBox>(editor, "_layerX")!.Text = ".25";
                    Click(editor, "更新选中效果");
                    Check(editor.State.Layers[0].X == .25 && !editor.State.Layers[0].SourceSpace && editor.State.Layers[0].Opacity == .4,
                        "updating visible effect fields preserves its canvas coordinate mode and opacity");
                    Set(editor, "_state", editor.State with { EmbeddedCursor = false, Motion = editor.State.Motion with { Cursor = CursorMotion.None } });
                    Invoke(editor, "BuildProperties"); Choice(editor, "轨迹平滑").SelectedIndex = 1;
                    Check(editor.State.Pointer.Smoothing == CursorMotion.Rapid && editor.State.Motion.Cursor == CursorMotion.Rapid,
                        "the pointer smoothing control synchronizes its compatibility copy");
                    Invoke(editor, "Commit", editor.State with { Style = editor.State.Style with { Intro = "侧滑" }, Opening = editor.State.Opening with { Enabled = false } });
                    Invoke(editor, "BuildProperties");
                    Check(editor.State.Style.Intro == "侧滑" && !editor.State.Opening.Enabled, "old opening settings remain loadable before an explicit opening choice");
                    Choice(editor, "开场").SelectedIndex = 1;
                    Check(editor.State.Opening.Enabled && editor.State.Opening.Preset == "聚拢" && editor.State.Style.Intro == "无",
                        "an explicit new opening choice prevents simultaneous old and new opening effects");
                    Choice(editor, "开场").SelectedIndex = 0;
                    Check(!editor.State.Opening.Enabled && editor.State.Opening.Preset == "无", "the none opening choice stores the same disabled preset it displays");
                    editor.Save(); editor.Close();
                    editor = new EditorWindow(project); editor.Show(); await Task.Delay(100);
                    Check(editor.State.Pointer.Smoothing == CursorMotion.Rapid && editor.State.Motion.Cursor == CursorMotion.Rapid
                        && !editor.State.Layers[0].SourceSpace && editor.State.Layers[0].Opacity == .4,
                        "pointer and preserved effect fields survive save and reopen");
                }
                else throw new ArgumentException("Unknown editor regression phase: " + phase);
                editor.Save(); editor.Close();
                Check(!editor.IsVisible && File.Exists(project.MediaPath), "the editor closes normally and preserves the screen original");
                File.WriteAllText(Path.Combine(root, "editor-" + phase + "-result.json"), JsonSerializer.Serialize(new
                    { success = true, actualDecodedMedia = true, checks = cases.Count, cases }, new JsonSerializerOptions { WriteIndented = true }));
                exit = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally
            {
                if (editor?.IsVisible == true) editor.Close();
                watchdog.Cancel(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run(); GC.KeepAlive(application); return exit;
    }
    internal static T? Get<T>(EditorWindow editor, string field) => (T?)typeof(EditorWindow).GetField(field, Flags)!.GetValue(editor);
    internal static void Set(EditorWindow editor, string field, object? value) => typeof(EditorWindow).GetField(field, Flags)!.SetValue(editor, value);
    internal static void Invoke(EditorWindow editor, string method, params object[] arguments) => typeof(EditorWindow).GetMethod(method, Flags)!.Invoke(editor, arguments);
    internal static void Click(EditorWindow editor, string text)
    {
        var pages = Get<Dictionary<string, StackPanel>>(editor, "_propertyPages")!;
        var roots = new DependencyObject[] { editor }.Concat(pages.Values);
        var button = roots.SelectMany(Descendants).OfType<Button>().First(b => Equals(b.Content, text));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    internal static ComboBox Choice(EditorWindow editor, string label)
    {
        foreach (var page in Get<Dictionary<string, StackPanel>>(editor, "_propertyPages")!.Values)
            for (int i = 0; i + 1 < page.Children.Count; i++)
                if (page.Children[i] is TextBlock block && block.Text == label && page.Children[i + 1] is ComboBox choice) return choice;
        throw new Exception("Missing owned editor choice: " + label);
    }
    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Descendants(child)) yield return item;
    }
}

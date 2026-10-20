using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DogSC.Core;

namespace DogSC.Windows.SelfTest;

internal static class CameraRenderBench
{
    internal static async Task<int> RunAsync(string packagePath, string root, bool render = true)
    {
        root = Path.GetFullPath(root); MediaExport.RejectLinks(root); Directory.CreateDirectory(root);
        var project = ProjectStore.Open(packagePath);
        var savedFolder = Directory.GetParent(project.PackagePath) ?? throw new IOException("基准缺少已保存视频目录。");
        string baselineVideo = Path.Combine(savedFolder.FullName, savedFolder.Name + ".mp4");
        var state = JsonSerializer.Deserialize<EditorState>(File.ReadAllText(Path.Combine(project.PackagePath, "editor.json")))!;
        if (!state.Camera.Enabled || state.ZoomClips.Length != 0 || !state.EmbeddedCursor)
            throw new InvalidOperationException("基准必须为摄像头开启、无运镜、系统指针的已有受控录像。");
        string output = Path.Combine(root, "render.mp4");
        var timer = Stopwatch.StartNew();
        if (render) await MediaExport.ExportAsync(project, state, output, new ExportSettings(project.Capture.FrameRate, 0));
        timer.Stop();
        double renderSeconds = timer.Elapsed.TotalSeconds;
        Mf.Check(Mf.MFStartup(0x20070, 0));
        int frames = 0; int width, height; double duration, sourceDuration, baselineDuration;
        try
        {
            using (var source = new MfReader(project.MediaPath, false)) sourceDuration = source.Duration;
            using (var baseline = new MfReader(baselineVideo, false)) baselineDuration = baseline.Duration;
            using var reader = new MfReader(output, false);
            width = reader.Width; height = reader.Height; duration = reader.Duration;
            while (reader.Read() is { } sample)
            {
                if (sample.Bytes.Length != checked(width * height * 4)) throw new IOException("基准输出帧数据不完整。");
                frames++;
            }
        }
        finally { Mf.MFShutdown(); }
        int expectedFrames = (int)Math.Ceiling(baselineDuration * project.Capture.FrameRate - 1e-8);
        bool valid = frames == expectedFrames && Math.Abs(duration - project.DurationSeconds) <= .05;
        File.WriteAllText(Path.Combine(root, "camera-render-bench-result.json"), JsonSerializer.Serialize(new
        {
            success = valid, reusedActualCapture = true, packagePath, output, render, renderSeconds, frames, expectedFrames,
            width, height, duration, sourceDuration, baselineVideo, baselineDuration, wallClockDuration = project.DurationSeconds,
            frameRate = project.Capture.FrameRate, zoomEnabled = false,
            cameraEnabled = state.Camera.Enabled, sourcePreserved = true, bytes = new FileInfo(output).Length
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (!valid) throw new IOException("优化不能改变基准输出的帧数或时长。");
        Console.WriteLine($"PASS render benchmark: {renderSeconds:F3}s, {frames} frames, {width}x{height}");
        return 0;
    }
}

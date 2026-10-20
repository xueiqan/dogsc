using System.IO;
using System.Reflection;
using System.Threading.Channels;

namespace DogSC.Windows.SelfTest;

internal static class AudioFailureChecks
{
    public static async Task<int> RunLiveAsync(string root)
    {
        Directory.CreateDirectory(root);
        using var audio = new WasapiAudioSession();
        string path = Path.Combine(root, "system.wav");
        audio.Start(AudioCaptureKind.SystemLoopback, null, path);
        await audio.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(1200);
        audio.SetPaused(true); await Task.Delay(200); audio.SetPaused(false);
        await Task.Delay(1200); audio.Stop();
        var duration = await audio.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        if (duration.TotalSeconds < 1 || audio.State != RecordingState.Complete || new FileInfo(path).Length < 100) throw new Exception("真实独立音频正常路径失败。");
        Console.WriteLine($"真实系统声音采集/暂停/继续/收尾通过，{duration.TotalSeconds:F2}秒，{audio.RecordedFrames}帧。");
        return 0;
    }
    public static async Task<int> RunAsync()
    {
        var packet = typeof(WasapiAudioSession).GetNestedType("Packet", BindingFlags.NonPublic)!;
        var factory = typeof(Channel).GetMethods().Single(method => method.Name == "CreateBounded" && method.IsGenericMethod && method.GetParameters()[0].ParameterType == typeof(int));
        var queue = factory.MakeGenericMethod(packet).Invoke(null, [32]);
        var item = Activator.CreateInstance(packet, [null, 0, 0L, 0u, 0ul, 0ul, 0u]);
        var enqueue = typeof(WasapiAudioSession).GetMethod("QueuePacket", BindingFlags.NonPublic | BindingFlags.Static)!;
        for (int i = 0; i < 32; i++) enqueue.Invoke(null, [queue, item]);
        try { enqueue.Invoke(null, [queue, item]); throw new Exception("音频队列满未报错。"); }
        catch (TargetInvocationException error) when (error.InnerException is IOException failure && failure.Message.Contains("屏幕录制继续")) { }
        using var audio = new WasapiAudioSession();
        var completion = (TaskCompletionSource<TimeSpan>)typeof(WasapiAudioSession).GetField("completion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(audio)!;
        completion.SetException(new IOException("controlled audio writer failure"));
        string? warning = null;
        bool complete = await MainWindow.FinishAudioTrackAsync(audio, "microphone", value => warning = value);
        if (complete || warning?.Contains("麦克风") != true || !warning.Contains("主视频继续保存")) throw new Exception("附加音频失败仍阻断主视频。");
        return 2;
    }
}

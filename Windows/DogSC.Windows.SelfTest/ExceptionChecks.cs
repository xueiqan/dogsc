using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace DogSC.Windows.SelfTest;

internal static class ExceptionChecks
{
    private sealed class FixtureApp : App
    {
        protected override void OnStartup(System.Windows.StartupEventArgs e) { }
    }
    public static int RunUi(string root)
    {
        var app = new FixtureApp();
        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        app.RegisterExceptionHandlers(root);
        using var cancel = new CancellationTokenSource();
        bool dismissed = false;
        _ = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                EnumWindows((handle, _) =>
                {
                    GetWindowThreadProcessId(handle, out uint pid);
                    var name = new StringBuilder(64); GetClassName(handle, name, name.Capacity);
                    if (pid == Environment.ProcessId && name.ToString() == "#32770")
                    { dismissed = true; PostMessage(handle, 0x111, 1, 0); }
                    return true;
                }, 0);
                await Task.Delay(50);
            }
        });
        app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => throw new IOException("controlled dispatcher fixture")));
        int exit = app.Run();
        cancel.Cancel();
        bool success = exit == 1 && dismissed && File.ReadAllText(Path.Combine(root, "last-error.log")).Contains("controlled dispatcher fixture");
        Console.WriteLine($"Dispatcher exception: handled={success}, exit={exit}, dialog={dismissed}");
        return success ? 0 : 1;
    }

    private delegate bool EnumWindow(nint handle, nint argument);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint argument);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint handle, uint message, nint wParam, nint lParam);

    public static int Run(string root)
    {
        string expected = "controlled failure fixture";
        string path = ExceptionReporting.Write(new IOException(expected), "selftest", root);
        if (!File.ReadAllText(path).Contains(expected)) throw new Exception("异常日志未记录实际错误。");
        string invalid = Path.Combine(root, "not-a-directory");
        File.WriteAllText(invalid, "retained");
        string warning = ExceptionReporting.Write(new Exception(expected), "selftest", invalid);
        if (!warning.Contains("日志未能写入") || File.ReadAllText(invalid) != "retained") throw new Exception("日志失败影响了已有文件。");
        return 2;
    }
}

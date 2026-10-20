using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DogSC.Windows;

public partial class App : Application
{
    private DesktopIntegration? desktop;
    private Mutex? instance;
    private bool ownsInstance;
    private bool shuttingDown;
    private bool handlingFailure;
    private string? exceptionLogDirectory;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterExceptionHandlers();
        await StartAsync(e);
    }

    internal void RegisterExceptionHandlers(string? logDirectory = null)
    {
        exceptionLogDirectory = logDirectory;
        DispatcherUnhandledException += (_, failure) =>
        {
            failure.Handled = true;
            if (handlingFailure) { ExceptionReporting.Write(failure.Exception, "异常处理期间再次失败", exceptionLogDirectory); Shutdown(1); return; }
            handlingFailure = true;
            HandleFailureAsync(failure.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, failure) =>
        { if (failure.ExceptionObject is Exception error) ExceptionReporting.Write(error, "进程无法继续运行的异常", exceptionLogDirectory); };
        TaskScheduler.UnobservedTaskException += (_, failure) =>
        {
            string path = ExceptionReporting.Write(failure.Exception, "未观察的后台任务异常", exceptionLogDirectory);
            failure.SetObserved();
            if (!shuttingDown && !Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(new Action(() => MessageBox.Show($"后台任务失败，已写入日志：{path}。已写录制素材会保留。", "DogSC", MessageBoxButton.OK, MessageBoxImage.Warning)));
        };
    }

    private async Task StartAsync(StartupEventArgs e)
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, args) =>
            { if (sender is Window loaded && ReferenceEquals(args.OriginalSource, loaded)) Localization.Apply(loaded); }));
        if (e.Args.FirstOrDefault() is "--probe" or "--smoke-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { Shutdown(await Diagnostics.RunAsync(e.Args)); }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                Shutdown(1);
            }
            return;
        }

        try { Localization.LoadPreference(); }
        catch (Exception error) { MessageBox.Show(error.Message, "DogSC界面语言设置", MessageBoxButton.OK, MessageBoxImage.Warning); }
        var window = new MainWindow();
        MainWindow = window;
        if (e.Args.FirstOrDefault() == "--ui-snapshot")
        {
            window.Show();
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                if (e.Args.Length != 2)
                    throw new ArgumentException("--ui-snapshot需要一个PNG输出路径。");
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var dpi = VisualTreeHelper.GetDpi(window);
                var bitmap = new RenderTargetBitmap(
                    (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
                    (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var path = Path.GetFullPath(e.Args[1]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var stream = File.Create(path);
                encoder.Save(stream);
                Shutdown(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                Shutdown(1);
            }
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        bool backgroundStartup = e.Args.Contains("--background", StringComparer.Ordinal);
        instance = new Mutex(true, @"Local\DogSC.Windows", out ownsInstance);
        if (!ownsInstance)
        {
            if (!backgroundStartup)
                MessageBox.Show("DogSC已在运行。按Ctrl+Shift+S呼出录制条，或双击托盘图标打开设置。", "DogSC");
            Shutdown(0);
            return;
        }
        try
        {
            desktop = new DesktopIntegration(window, window.ShowRecordingToolbar,
                window.TogglePause, window.ShowSettings, window.OpenSaveFolder, window.RequestExit,
                window.AddRecordingMarker, window.ShowMemo, window.ToggleRecordingToolbar);
            window.RecordingMarkerRegistrationChanged += desktop.SetMarkerRegistration;
            window.Closed += (_, _) => { shuttingDown = true; Shutdown(0); };
            void EnterRecording()
            {
                if (!shuttingDown && !Dispatcher.HasShutdownStarted) window.ShowRecordingToolbar();
            }
            if (!backgroundStartup && !FirstRunWindow.ShowIfNeeded(backgroundStartup, EnterRecording)) EnterRecording();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "DogSC启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async void HandleFailureAsync(Exception error)
    {
        string path = ExceptionReporting.Write(error, "界面或启动操作异常", exceptionLogDirectory);
        try
        {
            MessageBox.Show($"操作失败：{error.Message}\n已写素材保留，程序将停止采集并退出。\n日志：{path}", "DogSC异常", MessageBoxButton.OK, MessageBoxImage.Error);
            if (MainWindow is MainWindow window) await window.StopAfterFailureAsync();
        }
        catch (Exception cleanup) { ExceptionReporting.Write(cleanup, "异常退出收尾失败"); }
        finally { shuttingDown = true; Shutdown(1); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        shuttingDown = true;
        try { desktop?.Dispose(); } catch (Exception error) { ExceptionReporting.Write(error, "关闭托盘失败"); }
        try { if (ownsInstance) instance?.ReleaseMutex(); } catch (Exception error) { ExceptionReporting.Write(error, "释放实例锁失败"); }
        instance?.Dispose();
        base.OnExit(e);
    }
}

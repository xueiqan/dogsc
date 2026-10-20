namespace DogSC.Windows.SelfTest;

internal static class ExportGateChecks
{
    public static async Task<int> RunAsync()
    {
        var timeout = TimeSpan.FromMilliseconds(120);
        await MediaExport.RunWorkerAsync((_, _) => { }, "fixture", inactivityTimeout: timeout);
        if (MediaExport.IsBusy) throw new Exception("成功任务未释放导出闸门。");
        try { await MediaExport.RunWorkerAsync((_, _) => throw new InvalidOperationException("fixture"), "fixture"); }
        catch (InvalidOperationException error) when (error.Message == "fixture") { }
        if (MediaExport.IsBusy) throw new Exception("失败任务未释放导出闸门。");
        using var blocked = new ManualResetEventSlim();
        try
        {
            try { await MediaExport.RunWorkerAsync((_, _) => blocked.Wait(TimeSpan.FromSeconds(5)), "blocked fixture", inactivityTimeout: timeout); throw new Exception("阻塞任务未超时。"); }
            catch (TimeoutException) { }
            if (!MediaExport.IsBusy || !MediaExport.IsStalled) throw new Exception("超时后假称原生线程已退出。");
            try { await MediaExport.RunWorkerAsync((_, _) => { }, "second fixture"); throw new Exception("阻塞线程仍在时允许第二导出。"); }
            catch (InvalidOperationException) { }
        }
        finally { blocked.Set(); }
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (MediaExport.IsBusy && DateTime.UtcNow < deadline) await Task.Delay(10);
        if (MediaExport.IsBusy || MediaExport.IsStalled) throw new Exception("实际退出后导出闸门未恢复。");
        await MediaExport.RunWorkerAsync((report, _) =>
        {
            for (int i = 0; i < 12; i++) { Thread.Sleep(25); report.Report(i); }
        }, "progressing fixture", inactivityTimeout: timeout);
        using var cancel = new CancellationTokenSource();
        var cancelling = MediaExport.RunWorkerAsync((_, token) => { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); }, "cancel fixture", token: cancel.Token);
        cancel.Cancel();
        try { await cancelling; throw new Exception("取消未传达给媒体线程。"); } catch (OperationCanceledException) { }
        if (MediaExport.IsBusy) throw new Exception("取消完成后闸门未释放。");
        return 7;
    }
}

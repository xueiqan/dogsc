using System.IO;

namespace DogSC.Windows;

internal static class ExceptionReporting
{
    internal static string Write(Exception error, string context, string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DogSC", "logs");
        string path = Path.Combine(directory, "last-error.log");
        try
        {
            MediaExport.RejectLinks(path);
            Directory.CreateDirectory(directory);
            string text = $"{DateTimeOffset.Now:O}\n{context}\n{error}";
            File.WriteAllText(path, text.Length > 65_536 ? text[..65_536] : text);
            return path;
        }
        catch { return "错误日志未能写入，请检查日志目录权限：" + directory; }
    }
}

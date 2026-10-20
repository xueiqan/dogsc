namespace DogSC.Core;

public sealed class FilePublicationException : IOException
{
    public string? RecoveryPath { get; }
    internal FilePublicationException(string message, string? recoveryPath, Exception error) : base(message, error)
        => RecoveryPath = recoveryPath;
}

public static class SafeFilePublication
{
    // The caller supplies a complete, flushed file and must retain it if publication fails.
    public static void Publish(string temporary, string destination, bool overwrite = true)
        => PublishCore(temporary, destination, overwrite, File.Move);

    private static void PublishCore(string temporary, string destination, bool overwrite, Action<string, string, bool> move)
    {
        temporary = Path.GetFullPath(temporary); destination = Path.GetFullPath(destination);
        try
        {
            if (temporary.Equals(destination, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetDirectoryName(temporary), Path.GetDirectoryName(destination), StringComparison.OrdinalIgnoreCase))
                throw new IOException("文件发布必须使用目标同目录中的独立暂存文件。");
            RejectLinks(temporary); RejectLinks(destination);
            if (!File.Exists(temporary)) throw new FileNotFoundException("完整暂存文件不存在。", temporary);
            // Same-directory rename avoids ReplaceFile's documented partial replacement states.
            move(temporary, destination, overwrite);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            string? recovery = File.Exists(temporary) ? temporary : File.Exists(destination) ? destination : null;
            string original = File.Exists(destination) ? $"现有目标仍位于：{destination}。" : "目标文件当前不可用。";
            string retained = recovery is null ? "未能确认可恢复文件。" : $"可恢复文件保留在：{recovery}。";
            throw new FilePublicationException($"文件发布失败：{error.Message} {original}{retained}", recovery, error);
        }
    }

    private static void RejectLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("文件发布路径不能经过链接或重解析点。");
    }
}

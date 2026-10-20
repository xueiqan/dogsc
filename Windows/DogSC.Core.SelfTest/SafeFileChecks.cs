using System.Reflection;
using DogSC.Core;

public static class SafeFileChecks
{
    public static int Run(string root, Action<string> writeMediaFixture)
    {
        root = Path.Combine(root, "safe-file-publication"); Directory.CreateDirectory(root);
        int passed = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("FAIL: " + name);
            passed++;
            Console.WriteLine("PASS safe publication: " + name);
        }
        string source = Path.Combine(root, "complete.tmp"), destination = Path.Combine(root, "published.json");
        void NewFiles() { File.WriteAllText(source, "new completed file"); File.WriteAllText(destination, "old valid file"); }
        NewFiles();
        SafeFilePublication.Publish(source, destination);
        Check(File.ReadAllText(destination) == "new completed file" && !File.Exists(source), "overwrite publishes complete file");
        File.Delete(destination); File.WriteAllText(source, "first publication");
        SafeFilePublication.Publish(source, destination, false);
        Check(File.ReadAllText(destination) == "first publication", "non-overwrite can create destination");

        NewFiles();
        FilePublicationException? failure = null;
        try { SafeFilePublication.Publish(source, destination, false); }
        catch (FilePublicationException error) { failure = error; }
        Check(failure is not null && failure.RecoveryPath == source && failure.Message.Contains(source), "non-overwrite reports recovery file");
        Check(File.ReadAllText(destination) == "old valid file" && File.ReadAllText(source) == "new completed file", "non-overwrite retains both files");

        using (var held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            failure = null;
            try { SafeFilePublication.Publish(source, destination); }
            catch (FilePublicationException error) { failure = error; }
            Check(failure is not null && File.Exists(source) && failure.Message.Contains("文件发布失败")
                && failure.Message.Contains("可恢复文件保留在") && failure.Message.Contains(source),
                "occupied destination retains complete temporary file and reports Chinese recovery path");
            Check(File.ReadAllText(destination) == "old valid file", "occupied destination preserves old contents");
        }

        NewFiles();
        var publish = typeof(SafeFilePublication).GetMethod("PublishCore", BindingFlags.Static | BindingFlags.NonPublic)!;
        Action<string, string, bool> partialFailure = (_, target, _) =>
        {
            // Simulate documented ReplaceFile error 1176: target disappeared, completed new file remains.
            File.Delete(target);
            throw new IOException("fixture ERROR_UNABLE_TO_MOVE_REPLACEMENT", unchecked((int)0x80070498));
        };
        failure = null;
        try { publish.Invoke(null, [source, destination, true, partialFailure]); }
        catch (TargetInvocationException error) when (error.InnerException is FilePublicationException retained) { failure = retained; }
        Check(failure?.InnerException?.HResult == unchecked((int)0x80070498), "1176 underlying failure retained");
        Check(failure?.RecoveryPath == source && failure.Message.Contains(source), "1176 exposes surviving recovery path");
        Check(!File.Exists(destination) && File.ReadAllText(source) == "new completed file", "1176 never deletes surviving completed file");

        File.WriteAllText(destination, "old valid file");
        var project = ProjectStore.Create(root, "publication-export", new CaptureMetadata("screen", "fixture", 30, false, false, null));
        writeMediaFixture(project.MediaPath); project = ProjectStore.Complete(project, TimeSpan.FromSeconds(1));
        string exported = Path.Combine(root, "exported.mp4"); File.WriteAllText(exported, "old exported video");
        using (var held = new FileStream(exported, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            failure = null;
            try { ProjectStore.ExportAsync(project, exported, true).GetAwaiter().GetResult(); }
            catch (FilePublicationException error) { failure = error; }
            Check(failure?.RecoveryPath is { } recoverable && File.Exists(recoverable), "Core export finally retains completed file on publication failure");
            Check(File.ReadAllText(exported) == "old exported video", "Core export publication failure preserves old video");
            Check(failure?.RecoveryPath is { } recovered && File.ReadAllBytes(recovered).SequenceEqual(File.ReadAllBytes(project.MediaPath)),
                "retained Core export is an exact copy of original media");
        }
        return passed;
    }
}

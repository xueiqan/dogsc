using System.IO;
using System.Reflection;
using System.Text.Json;

namespace DogSC.Windows.SelfTest;

internal static class ScratchCleanupChecks
{
    public static int Run(string root)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        var delete = typeof(MediaExport).GetMethod("DeleteScratch", BindingFlags.Static | BindingFlags.NonPublic,
            null, [typeof(string), typeof(string)], null)
            ?? throw new MissingMethodException("The scratch helper has not been implemented yet.");
        string logs = Path.Combine(root, "logs"), logPath = Path.Combine(logs, "last-error.log");
        var cases = new List<string>();
        void Check(bool valid, string name)
        {
            if (!valid) throw new Exception("FAIL: " + name);
            cases.Add(name);
        }
        void Delete(string path, string? logDirectory = null)
        {
            try { delete.Invoke(null, [path, logDirectory ?? logs]); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        void Create(string path)
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.WriteByte(42);
        }
        void CheckDiagnostic(string path, string scenario)
        {
            string text = File.ReadAllText(logPath);
            Check(text.Contains(Path.GetFullPath(path)), scenario + ": cleanup diagnostic includes the exact absolute scratch path");
            Check((text.Contains("清理") || text.Contains("删除")) && text.Contains("保留"),
                scenario + ": cleanup diagnostic explains in Chinese that the scratch file was retained");
        }

        string readOnly = Path.Combine(root, "readonly.fixture");
        Create(readOnly);
        var attributes = File.GetAttributes(readOnly);
        try
        {
            File.SetAttributes(readOnly, attributes | FileAttributes.ReadOnly);
            Delete(readOnly);
            Check(File.Exists(readOnly) && File.ReadAllBytes(readOnly).SequenceEqual(new byte[] { 42 }),
                "read-only scratch cleanup returns normally and retains the unchanged file");
            CheckDiagnostic(readOnly, "read-only");
        }
        finally { if (File.Exists(readOnly)) File.SetAttributes(readOnly, attributes); }
        Delete(readOnly);
        Check(!File.Exists(readOnly), "scratch cleanup succeeds after the read-only attribute is removed");

        string held = Path.Combine(root, "held.fixture");
        Create(held);
        using (var lease = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Delete(held);
            Check(File.Exists(held) && !lease.SafeFileHandle.IsClosed && lease.Length == 1,
                "scratch held without delete sharing remains owned and is not removed");
            CheckDiagnostic(held, "held handle");
        }
        Delete(held);
        Check(!File.Exists(held), "scratch cleanup succeeds after its external handle is closed");
        Delete(held);
        Check(!File.Exists(held), "repeated scratch cleanup remains a no-op after successful deletion");

        string badLogs = Path.Combine(root, "blocked-logs");
        Directory.CreateDirectory(badLogs);
        string heldLog = Path.Combine(badLogs, "last-error.log");
        Create(heldLog);
        string loggerFailure = Path.Combine(root, "logger-failure.fixture");
        Create(loggerFailure);
        attributes = File.GetAttributes(loggerFailure);
        try
        {
            File.SetAttributes(loggerFailure, attributes | FileAttributes.ReadOnly);
            using var lease = new FileStream(heldLog, FileMode.Open, FileAccess.Read, FileShare.Read);
            Delete(loggerFailure, badLogs);
            Check(File.Exists(loggerFailure) && lease.Length == 1,
                "a failure to write the cleanup log also returns normally and preserves the scratch file");
        }
        finally { if (File.Exists(loggerFailure)) File.SetAttributes(loggerFailure, attributes); }
        Delete(loggerFailure);
        Check(!File.Exists(loggerFailure), "scratch can still be removed after its cleanup diagnostic could not be written");

        File.WriteAllText(Path.Combine(root, "scratch-cleanup-result.json"), JsonSerializer.Serialize(new
            { success = true, checks = cases.Count, cases, realFileHandles = true, readOnlyFiles = true,
                loggerFailureChecked = true, globalApplicationLogUsed = false, actualMediaExport = false,
                nativeShutdownMeasured = false, recoveryFilesTouched = false },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Scratch cleanup checks passed: {cases.Count}.");
        return cases.Count;
    }
}

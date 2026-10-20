using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DogSC.Core;

namespace DogSC.Windows.SelfTest;

internal static class LegacyImportChecks
{
    public static async Task<int> RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "original.dogscproject"); Directory.CreateDirectory(Path.Combine(source, "media"));
        var video = Path.Combine(source, "media", "screen.mp4");
        await MediaExport.RunWorkerAsync((_, _) =>
        {
            Mf.Check(Mf.MFStartup(0x20070, 0));
            try { AudioRateChecks.WriteSilentVideo(video, 1); }
            finally { Mf.MFShutdown(); }
        }, "Legacy import regression fixture");
        var manifestPath = Path.Combine(source, "project.json");
        var manifest = JsonNode.Parse("""
        {"version":27,"title":"导入测试","timeline":{"sourceSequence":{"kind":"fullRecording"}},
         "exportSettings":{"frameRate":30,"resolution":"原始素材"},
         "media":{"screen":{"relativePath":"media/screen.mp4"}},
         "camera":{"isHidden":null,"isMirrored":null},"audio":{"isSystemMuted":null,"isMicrophoneMuted":null},
         "cursorStyle":{"showsClickEffect":null,"hideWhenIdle":null},
         "openingSequence":{"isEnabled":null,"includedElements":null,"elementOrder":null}}
        """)!;
        var cases = new List<string>();
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            cases.Add(name); Console.WriteLine("PASS legacy import: " + name);
        }
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        byte[] original = File.ReadAllBytes(manifestPath); var videoHash = SHA256.HashData(File.ReadAllBytes(video));
        var imported = await LegacyProjectImport.ImportAsync(source, Path.Combine(root, "imported-null"));
        Check(imported.State.Camera.Mirror && !imported.State.Camera.Hidden && !imported.State.Audio.SystemMuted
            && !imported.State.Audio.MicrophoneMuted && !imported.State.Pointer.HideWhenIdle && !imported.State.Opening.Enabled,
            "nullable legacy Boolean fields use their existing defaults through the real importer");
        Check(imported.State.Opening.IncludedElements is null && imported.State.Opening.ElementOrder is null,
            "nullable opening arrays remain the default arrays");
        Check(File.ReadAllBytes(manifestPath).SequenceEqual(original)
            && File.ReadAllBytes(Path.Combine(imported.PreservedOriginalPath, "project.json")).SequenceEqual(original),
            "the Mac manifest and its preserved Windows copy keep their original bytes");
        Check(SHA256.HashData(File.ReadAllBytes(imported.Project.MediaPath)).SequenceEqual(videoHash),
            "ordinary MP4 screen import preserves the recorded video bytes");
        foreach (var (section, field, badValue, diagnosticField) in new[]
        {
            ("camera", "isHidden", "\"yes\"", "isHidden"), ("audio", "isSystemMuted", "1", "isSystemMuted"),
            ("openingSequence", "includedElements", "{}", "includedElements"), ("openingSequence", "elementOrder", "[false]", "elementOrder"),
            ("camera", "size", "\"small\"", "size"), ("camera", "size", "1e9999", "size"),
            ("camera", "shape", "10", "shape"), ("openingSequence", "preset", "true", "preset"),
            ("timeline", "screenMotionClips", """[{"id":"83327fd6-cb11-4f66-9426-39070c4eb6ef","timing":{"startTime":0,"duration":0.5},"target":{"position":{"x":0.5,"y":0.5},"scale":"bad"}}]""", "scale"),
            ("timeline", "screenMotionClips", """[{"id":"83327fd6-cb11-4f66-9426-39070c4eb6ef","timing":{"startTime":0,"duration":0.5},"target":{"position":{"x":0.5,"y":0.5}},"focusEffect":{"target":false}}]""", "target")
        })
        {
            var broken = manifest.DeepClone(); broken[section]![field] = JsonNode.Parse(badValue);
            var bytes = System.Text.Encoding.UTF8.GetBytes(broken.ToJsonString()); File.WriteAllBytes(manifestPath, bytes);
            try
            {
                await LegacyProjectImport.ImportAsync(source, Path.Combine(root, "invalid-" + section + "-" + field));
                throw new Exception("FAIL: legacy field accepted a wrong type: " + field);
            }
            catch (InvalidDataException error)
            {
                Check(error.Message.Contains(diagnosticField) && (error.Message.Contains("布尔") || error.Message.Contains("数组") || error.Message.Contains("字符串") || error.Message.Contains("数字")),
                    section + "." + field + " gives a Chinese field-specific diagnostic");
            }
            Check(File.ReadAllBytes(manifestPath).SequenceEqual(bytes) && SHA256.HashData(File.ReadAllBytes(video)).SequenceEqual(videoHash),
                section + "." + field + " failure preserves the source manifest and video");
        }
        File.WriteAllText(Path.Combine(root, "legacy-null-result.json"), JsonSerializer.Serialize(new
            { success = true, actualImporter = true, actualDecodedVideo = true, checks = cases.Count, cases },
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}

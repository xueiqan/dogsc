using System.Buffers.Binary;
using System.Text.Json.Nodes;
using DogSC.Core;

if (args.Contains("--tail-zoom", StringComparer.Ordinal))
{
    var checks = RegressionChecks.TailZoomClipping();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { success = true, checks, scope = "tail-zoom" }));
    return;
}

if (args.Contains("--move-zoom", StringComparer.Ordinal))
{
    var checks = RegressionChecks.MovePreservesZoom();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { success = true, checks, scope = "move-zoom" }));
    return;
}

if (args.Contains("--terminal-markers", StringComparer.Ordinal))
{
    var checks = RegressionChecks.TerminalRecordingMarkers();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { success = true, checks, scope = "terminal-markers" }));
    return;
}

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../artifacts/selftest",
    $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"));
Directory.CreateDirectory(root);
var passed = 0;
var skipped = 0;
var capture = new CaptureMetadata("screen", "测试显示器", 30, true, true, null);

ProjectDocument CreateComplete(string title)
{
    var project = ProjectStore.Create(root, title, capture);
    WriteFixture(project.MediaPath);
    return ProjectStore.Complete(project, TimeSpan.FromSeconds(12.5));
}

void Check(bool condition, string name)
{
    if (!condition) throw new Exception($"FAIL: {name}");
}

void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"FAIL: {name} did not reject input");
}

void Test(string name, Action action)
{
    action();
    passed++;
    Console.WriteLine($"PASS {name}");
}

void EditManifest(ProjectDocument project, string field, JsonNode? value)
{
    var path = Path.Combine(project.PackagePath, "project.json");
    var json = JsonNode.Parse(File.ReadAllText(path))!;
    json[field] = value;
    File.WriteAllText(path, json.ToJsonString());
}

Test("legacy null screen rejects with Chinese diagnostics without rewriting input", () =>
{
    var package = Path.Combine(root, "legacy-null-screen.dogscproject");
    Directory.CreateDirectory(package);
    var path = Path.Combine(package, "project.json");
    var json = JsonNode.Parse("""
        {"version":27,"timeline":{"sourceSequence":{"kind":"fullRecording"}},
         "exportSettings":{"frameRate":30,"resolution":"原始素材"},
         "media":{"screen":null,"camera":null,"microphone":null}}
        """)!;
    foreach (var missing in new[] { false, true })
    {
        if (missing) json["media"]!.AsObject().Remove("screen");
        File.WriteAllText(path, json.ToJsonString());
        var original = File.ReadAllBytes(path);
        try { LegacyProjectCodec.Open(package); throw new Exception("FAIL: legacy missing screen accepted"); }
        catch (InvalidDataException error)
        {
            Check(error.Message.Contains("屏幕素材"), "legacy screen failure uses Chinese explanation");
        }
        Check(File.ReadAllBytes(path).SequenceEqual(original), "rejected legacy manifest preserved byte for byte");
    }
    json["media"]!["screen"] = new JsonObject { ["relativePath"] = "media/recording.mp4" };
    File.WriteAllText(path, json.ToJsonString());
    var loaded = LegacyProjectCodec.Open(package);
    Check(loaded.MediaReferences.Count == 1 && loaded.MissingReferences.Single().Role == "screen",
        "valid screen reference remains reportable when file is missing; optional null tracks remain allowed");
});

Test("missing Windows media reports Chinese diagnostics and preserves manifest", () =>
{
    var project = CreateComplete("missing-media-diagnostics");
    var manifest = Path.Combine(project.PackagePath, "project.json");
    var original = File.ReadAllBytes(manifest);
    File.Delete(project.MediaPath);
    foreach (var missingDirectory in new[] { false, true })
    {
        if (missingDirectory) Directory.Delete(Path.GetDirectoryName(project.MediaPath)!);
        try { ProjectStore.Open(project.PackagePath); throw new Exception("FAIL: missing recording accepted"); }
        catch (InvalidDataException error)
        {
            Check(error.Message.Contains("素材缺失") && error.Message.Contains("media/recording.mp4"),
                "missing media explains how to restore the package-owned file");
            Check(error.InnerException is FileNotFoundException or DirectoryNotFoundException,
                "missing-file cause remains available for diagnostics");
        }
        Check(File.ReadAllBytes(manifest).SequenceEqual(original), "opening missing media never rewrites project");
    }
});

Test("zoom crop normalizes zero negative tiny and out-of-bounds rectangles", () =>
{
    var sample = new ZoomSample(2, new(.205, .31), 1);
    var cases = new (CompositionRect Input, CompositionRect Safe)[]
    {
        (new(.2, .3, 0, 0), new(.2, .3, .02, .02)),
        (new(.2, .3, -.1, -.2), new(.2, .3, .02, .02)),
        (new(.2, .3, .001, .002), new(.2, .3, .02, .02)),
        (new(1.2, -1, 0, 20), new(.98, 0, .02, 1)),
        (new(-2, 2, 4, -1), new(0, .98, 1, .02))
    };
    foreach (var (input, safe) in cases)
    {
        var actual = ZoomViewportTransform.Make(sample, input);
        var expected = ZoomViewportTransform.Make(sample, safe);
        Check(double.IsFinite(actual.Scale) && double.IsFinite(actual.Translation.X)
            && double.IsFinite(actual.Translation.Y), "crop never divides by zero");
        Check(Math.Abs(actual.Translation.X - expected.Translation.X) < 1e-12
            && Math.Abs(actual.Translation.Y - expected.Translation.Y) < 1e-12,
            "crop matches Swift NormalizedCrop.clamped minimum 0.02");
    }
    var full = ZoomViewportTransform.Make(sample);
    Check(full == ZoomViewportTransform.Make(sample, new(0, 0, 1, 1)), "default full crop is unchanged");
    Throws<ArgumentOutOfRangeException>(() => ZoomViewportTransform.Make(sample, new(double.NaN, 0, 1, 1)), "NaN crop rejected");
    Throws<ArgumentOutOfRangeException>(() => ZoomViewportTransform.Make(sample, new(0, 0, double.PositiveInfinity, 1)), "infinite crop rejected");
});

Test("project roundtrip and incomplete-media preservation", () =>
{
    var project = ProjectStore.Create(root, "中文项目", capture);
    Check(Path.IsPathFullyQualified(project.PackagePath) && Path.IsPathFullyQualified(project.MediaPath), "absolute paths");
    Throws<InvalidDataException>(() => ProjectStore.Open(project.PackagePath), "incomplete project");
    File.WriteAllText(project.MediaPath, "broken");
    Throws<InvalidDataException>(() => ProjectStore.Complete(project, TimeSpan.FromSeconds(1)), "invalid MP4");
    Check(File.ReadAllText(project.MediaPath) == "broken", "failed recording preserved");
    WriteFixture(project.MediaPath);
    var complete = ProjectStore.Complete(project, TimeSpan.FromSeconds(12.5));
    Check(ProjectStore.Open(project.PackagePath) == complete, "roundtrip equality");
});

Test("MP4 incomplete boxes never complete or modify the project", () =>
{
    var project = ProjectStore.Create(root, "invalid-container", capture);
    WriteFixture(project.MediaPath);
    var valid = File.ReadAllBytes(project.MediaPath);
    var manifestPath = Path.Combine(project.PackagePath, "project.json");
    var manifest = File.ReadAllBytes(manifestPath);
    var missingMovie = valid[..24].Concat(valid[40..]).ToArray();
    var truncatedLargeHeader = new byte[52];
    valid[..48].CopyTo(truncatedLargeHeader, 0);
    BinaryPrimitives.WriteUInt32BigEndian(truncatedLargeHeader.AsSpan(40), 1);
    var emptyMedia = valid[..48];
    BinaryPrimitives.WriteUInt32BigEndian(emptyMedia.AsSpan(40), 8);
    var emptyMovie = (byte[])valid.Clone();
    BinaryPrimitives.WriteUInt32BigEndian(emptyMovie.AsSpan(24), 8);
    var oversized = (byte[])valid.Clone();
    BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(40), uint.MaxValue);
    var truncatedMovie = valid[..38];
    BinaryPrimitives.WriteUInt32BigEndian(truncatedMovie.AsSpan(24), 24);
    var undersized = (byte[])valid.Clone();
    BinaryPrimitives.WriteUInt32BigEndian(undersized.AsSpan(40), 4);
    var missingType = (byte[])valid.Clone();
    "free"u8.CopyTo(missingType.AsSpan(4));
    foreach (var bytes in new[] { missingMovie, valid[..40], valid[..^1],
        valid.Concat(new byte[1]).ToArray(), truncatedLargeHeader, emptyMedia, emptyMovie,
        oversized, truncatedMovie, undersized, missingType, Array.Empty<byte>() })
    {
        File.WriteAllBytes(project.MediaPath, bytes);
        Throws<InvalidDataException>(() => ProjectStore.Complete(project, TimeSpan.FromSeconds(1)), "incomplete MP4");
        Check(File.ReadAllBytes(manifestPath).SequenceEqual(manifest), "failed completion preserves manifest");
        Check(File.ReadAllBytes(project.MediaPath).SequenceEqual(bytes), "failed completion preserves media");
    }
});

Test("recovery copies complete boxes and preserves interrupted originals", () =>
{
    var project = ProjectStore.Create(root, "恢复测试", capture);
    WriteFixture(project.MediaPath);
    var complete = File.ReadAllBytes(project.MediaPath);
    using (var tail = new FileStream(project.MediaPath, FileMode.Append, FileAccess.Write))
    { tail.Write(new byte[] { 0, 0, 0, 20, 109, 111, 111, 102, 1, 2, 3 }); }
    var original = File.ReadAllBytes(project.MediaPath);
    var manifest = File.ReadAllBytes(Path.Combine(project.PackagePath, "project.json"));
    string[] auxiliary = ["media/system.wav", "media/camera.mp4", "events/pointer.jsonl", "assets/cursor.png", "editor.json", "capture-options.json"];
    foreach (var relative in auxiliary)
    {
        var path = Path.Combine(project.PackagePath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "auxiliary fixture " + relative);
    }
    Directory.CreateDirectory(Path.Combine(project.PackagePath, "assets", "empty"));
    Check(ProjectStore.Inspect(project.PackagePath).State == "recording", "inspect interrupted manifest");
    var recovered = ProjectStore.CreateRecoveryCopy(project.PackagePath, root);
    Check(recovered.PackagePath != project.PackagePath && File.ReadAllBytes(recovered.MediaPath).SequenceEqual(complete), "complete prefix copy");
    Check(File.ReadAllBytes(project.MediaPath).SequenceEqual(original) && File.ReadAllBytes(Path.Combine(project.PackagePath, "project.json")).SequenceEqual(manifest), "original bytes preserved");
    foreach (var relative in auxiliary)
        Check(File.ReadAllBytes(Path.Combine(project.PackagePath, relative)).SequenceEqual(File.ReadAllBytes(Path.Combine(recovered.PackagePath, relative))), "auxiliary copy " + relative);
    Check(Directory.Exists(Path.Combine(recovered.PackagePath, "assets", "empty")), "empty asset directory preserved");
    Throws<InvalidDataException>(() => ProjectStore.CreateRecoveryCopy(project.PackagePath, project.PackagePath), "nested recovery denied");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        var before = Directory.GetDirectories(root).Length;
        Throws<OperationCanceledException>(() => ProjectStore.CreateRecoveryCopy(project.PackagePath, root, cancelled.Token), "recovery cancellation");
        Check(Directory.GetDirectories(root).Length == before, "cancelled recovery creates no new package");
    }
    ProjectStore.Complete(recovered, TimeSpan.FromSeconds(1));
    Check(ProjectStore.Open(recovered.PackagePath).State == "complete", "recovery finalization");
    File.WriteAllBytes(project.MediaPath, complete[..40]);
    Throws<InvalidDataException>(() => ProjectStore.CreateRecoveryCopy(project.PackagePath, root), "no complete media cannot recover");
});

Test("interrupted own WAV recovery repairs headers without changing originals or complete audio", () =>
{
    foreach (var item in new (int Tag, int Bits, int Channels, int Frames, int Tail, bool Ext, bool Rf64)[] { (1, 8, 1, 101, 0, false, false),
        (1, 16, 2, 127, 3, false, false), (1, 24, 2, 129, 5, false, false),
        (3, 32, 2, 48000, 7, false, false), (1, 16, 2, 131, 1, true, false),
        (3, 32, 2, 133, 1, true, true) })
    {
        var project = ProjectStore.Create(root, $"wav-{item.Tag}-{item.Bits}-{item.Ext}", capture);
        WriteFixture(project.MediaPath);
        string path = Path.Combine(project.PackagePath, "media", "system.wav");
        byte[] original = WaveFixture(item.Tag, item.Bits, item.Channels, item.Frames, item.Tail, item.Ext, item.Rf64);
        File.WriteAllBytes(path, original);
        var recovered = ProjectStore.CreateRecoveryCopy(project.PackagePath, root);
        byte[] repaired = File.ReadAllBytes(Path.Combine(recovered.PackagePath, "media", "system.wav"));
        int offset = item.Ext ? 160 : 138, audioBytes = item.Frames * item.Channels * item.Bits / 8;
        Check(File.ReadAllBytes(path).SequenceEqual(original), "WAV source preserved");
        Check(repaired.Length == offset + audioBytes + (audioBytes & 1), "torn frame excluded and padding present");
        Check(repaired.AsSpan(offset, audioBytes).SequenceEqual(original.AsSpan(offset, audioBytes)), "all complete PCM samples preserved");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(repaired.AsSpan(offset - 12)) == item.Frames, "fact sample count repaired");
        if (item.Rf64)
        {
            Check(repaired.AsSpan(0, 4).SequenceEqual("RF64"u8)
                && BinaryPrimitives.ReadUInt64LittleEndian(repaired.AsSpan(20)) == (ulong)(repaired.Length - 8)
                && BinaryPrimitives.ReadUInt64LittleEndian(repaired.AsSpan(28)) == (ulong)audioBytes
                && BinaryPrimitives.ReadUInt64LittleEndian(repaired.AsSpan(36)) == (ulong)item.Frames
                && BinaryPrimitives.ReadUInt32LittleEndian(repaired.AsSpan(offset - 4)) == uint.MaxValue, "RF64 sizes repaired");
        }
        else Check(BinaryPrimitives.ReadUInt32LittleEndian(repaired.AsSpan(4)) == repaired.Length - 8
            && BinaryPrimitives.ReadUInt32LittleEndian(repaired.AsSpan(offset - 4)) == audioBytes, "RIFF sizes repaired");
        File.WriteAllBytes(path, repaired);
        var second = ProjectStore.CreateRecoveryCopy(project.PackagePath, root);
        Check(File.ReadAllBytes(Path.Combine(second.PackagePath, "media", "system.wav")).SequenceEqual(repaired), "complete WAV preserved byte for byte");
    }
    foreach (int invalid in new[] { 0, 1, 2 })
    {
        var project = ProjectStore.Create(root, "invalid-own-wav", capture); WriteFixture(project.MediaPath);
        byte[] original = invalid == 1 ? WaveFixture(1, 64, 2, 10, 0, false, false) : WaveFixture(1, 16, 2, 10, 0, invalid == 2, false);
        if (invalid == 0) BinaryPrimitives.WriteUInt16LittleEndian(original.AsSpan(112), 0);
        if (invalid == 2) BinaryPrimitives.WriteUInt16LittleEndian(original.AsSpan(118), 32);
        string path = Path.Combine(project.PackagePath, "media", "microphone.wav"); File.WriteAllBytes(path, original);
        Throws<IOException>(() => ProjectStore.CreateRecoveryCopy(project.PackagePath, root), "bad own WAV rejected");
        Check(File.ReadAllBytes(path).SequenceEqual(original), "rejected audio source preserved");
    }
});

Test("MP4 extended sizes and terminal zero-size boxes", () =>
{
    var project = ProjectStore.Create(root, "large-box", capture);
    WriteFixture(project.MediaPath);
    var valid = File.ReadAllBytes(project.MediaPath);
    var extended = new byte[64];
    valid[..48].CopyTo(extended, 0);
    BinaryPrimitives.WriteUInt32BigEndian(extended.AsSpan(40), 1);
    BinaryPrimitives.WriteUInt64BigEndian(extended.AsSpan(48), 24);
    File.WriteAllBytes(project.MediaPath, extended);
    var complete = ProjectStore.Complete(project, TimeSpan.FromSeconds(1));
    Check(ProjectStore.Open(project.PackagePath) == complete, "valid 64-bit box");
    foreach (var size in new[] { 15UL, ulong.MaxValue })
    {
        BinaryPrimitives.WriteUInt64BigEndian(extended.AsSpan(48), size);
        File.WriteAllBytes(project.MediaPath, extended);
        Throws<InvalidDataException>(() => ProjectStore.Open(project.PackagePath), "invalid extended box size");
    }
    BinaryPrimitives.WriteUInt32BigEndian(valid.AsSpan(40), 0);
    File.WriteAllBytes(project.MediaPath, valid);
    Check(ProjectStore.Open(project.PackagePath) == complete, "terminal zero-size mdat");
});

Test("MP4 parser work limit", () =>
{
    var project = ProjectStore.Create(root, "box-limit", capture);
    WriteFixture(project.MediaPath);
    using (var fixture = new FileStream(project.MediaPath, FileMode.Append, FileAccess.Write))
    {
        var box = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(box, 8);
        "free"u8.CopyTo(box.AsSpan(4));
        for (var i = 0; i < 65_536; i++) fixture.Write(box);
    }
    Throws<InvalidDataException>(() => ProjectStore.Complete(project, TimeSpan.FromSeconds(1)), "too many MP4 boxes");
});

Test("region bounds and integer overflow", () =>
{
    new CaptureRegion(0, 0, 2, 2).Validate(2, 2);
    Throws<ArgumentOutOfRangeException>(() => new CaptureRegion(-1, 0, 2, 2).Validate(100, 100), "negative position");
    Throws<ArgumentOutOfRangeException>(() => new CaptureRegion(99, 0, 2, 2).Validate(100, 100), "outside source");
    Throws<ArgumentOutOfRangeException>(() => new CaptureRegion(0, 0, 1, 2).Validate(100, 100), "small region");
    Throws<ArgumentOutOfRangeException>(() => new CaptureRegion(int.MaxValue, 0, 2, 2).Validate(int.MaxValue, 100), "overflow");
});

Test("frame-rate metadata bounds and high-refresh roundtrip", () =>
{
    foreach (var frameRate in new[] { 1, 30, 60, 120 })
    {
        var project = ProjectStore.Create(root, $"fps-{frameRate}", capture with { FrameRate = frameRate });
        WriteFixture(project.MediaPath);
        var complete = ProjectStore.Complete(project, TimeSpan.FromSeconds(1));
        Check(ProjectStore.Open(complete.PackagePath).Capture.FrameRate == frameRate, "frame-rate roundtrip");
    }
    foreach (var frameRate in new[] { 0, 121, 165 })
    {
        Throws<InvalidDataException>(() => ProjectStore.Create(root, "invalid-fps", capture with { FrameRate = frameRate }),
            "invalid frame-rate create");
        var project = CreateComplete($"invalid-fps-{frameRate}");
        var path = Path.Combine(project.PackagePath, "project.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["capture"]!["frameRate"] = frameRate;
        File.WriteAllText(path, json.ToJsonString());
        Throws<InvalidDataException>(() => ProjectStore.Open(project.PackagePath), "invalid frame-rate read");
    }
});

Test("first recording creates missing project parent", () =>
{
    var parent = Path.Combine(root, "new-default", "DogSC");
    Check(!Directory.Exists(parent), "fresh parent is absent");
    var project = ProjectStore.Create(parent, "首次录制", capture);
    Check(Directory.Exists(parent) && File.Exists(Path.Combine(project.PackagePath, "project.json")), "parent and manifest created");
});

Test("future schema and macOS format rejection", () =>
{
    var project = CreateComplete("schema");
    EditManifest(project, "version", JsonValue.Create(2));
    Throws<InvalidDataException>(() => ProjectStore.Open(project.PackagePath), "future schema");
    EditManifest(project, "format", null);
    Throws<InvalidDataException>(() => ProjectStore.Open(project.PackagePath), "macOS format");
});

Test("strict fields, duplicate fields and manifest size limit", () =>
{
    var project = CreateComplete("strict");
    var path = Path.Combine(project.PackagePath, "project.json");
    var original = File.ReadAllText(path);
    EditManifest(project, "unexpected", JsonValue.Create(true));
    Throws<InvalidDataException>(() => ProjectStore.Open(project.PackagePath), "unknown field");
    File.WriteAllText(path, original.Replace("{", "{\"version\":1,", StringComparison.Ordinal));
    Throws<InvalidDataException>(() => ProjectStore.Open(project.PackagePath), "duplicate field");
    File.WriteAllText(path, new string(' ', 1_048_577));
    Throws<InvalidDataException>(() => ProjectStore.Open(project.PackagePath), "oversized manifest");
});

Test("null metadata and incorrect JSON field types", () =>
{
    Throws<InvalidDataException>(() => ProjectStore.Create(root, null!, capture), "null title");
    Throws<InvalidDataException>(() => ProjectStore.Create(root, "null", null!), "null capture");
    Throws<InvalidDataException>(() => ProjectStore.Create(root, "null", capture with { Kind = null! }), "null kind");
    Throws<InvalidDataException>(() => ProjectStore.Create(root, "null", capture with { TargetName = null! }), "null target");
    var project = CreateComplete("metadata");
    var path = Path.Combine(project.PackagePath, "project.json");
    var original = File.ReadAllText(path);
    foreach (var edit in new Action<JsonNode>[]
    {
        json => json["title"] = null,
        json => json["capture"] = null,
        json => json["capture"]!["kind"] = null,
        json => json["capture"]!["targetName"] = null,
        json => json["capture"]!["frameRate"] = "30",
        json => json["capture"]!["systemAudio"] = "true",
        json => json["capture"]!["region"] = new JsonArray(),
        json => json["durationSeconds"] = null
    })
    {
        var json = JsonNode.Parse(original)!;
        edit(json);
        var invalid = json.ToJsonString();
        File.WriteAllText(path, invalid);
        Throws<InvalidDataException>(() => ProjectStore.Open(project.PackagePath), "invalid metadata");
        Check(File.ReadAllText(path) == invalid, "rejected metadata stays untouched");
    }
});

Test("package path escape rejection", () =>
{
    var project = CreateComplete("escape");
    EditManifest(project, "mediaPath", JsonValue.Create("../../outside.mp4"));
    Throws<InvalidDataException>(() => ProjectStore.Open(project.PackagePath), "traversal");
    EditManifest(project, "mediaPath", JsonValue.Create(Path.Combine(root, "outside.mp4")));
    Throws<InvalidDataException>(() => ProjectStore.Open(project.PackagePath), "absolute media path");
});

var linkProject = ProjectStore.Create(root, "link", capture);
var mediaFolder = Path.GetDirectoryName(linkProject.MediaPath)!;
var external = Path.Combine(root, "link-target");
Directory.CreateDirectory(external);
Directory.Delete(mediaFolder);
var linkCreated = false;
try
{
    Directory.CreateSymbolicLink(mediaFolder, external);
    linkCreated = true;
}
catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
{
    skipped++;
    Console.WriteLine($"SKIP symbolic link creation unavailable: {error.GetType().Name}");
}
if (linkCreated)
    Test("symbolic link rejection", () =>
        Throws<InvalidDataException>(() => ProjectStore.Complete(linkProject, TimeSpan.FromSeconds(1)), "symbolic link"));

var exportProject = CreateComplete("export");
var output = Path.Combine(root, "output.mp4");
File.WriteAllText(output, "original target");
var damagedProject = CreateComplete("damaged-export");
var damaged = File.ReadAllBytes(damagedProject.MediaPath);
File.WriteAllBytes(damagedProject.MediaPath, damaged[..24].Concat(damaged[40..]).ToArray());
try
{
    await ProjectStore.ExportAsync(damagedProject, output, overwrite: true);
    throw new Exception("FAIL: missing-moov export succeeded");
}
catch (InvalidDataException) { }
Check(File.ReadAllText(output) == "original target", "invalid source preserves export target");
Check(!Directory.EnumerateFiles(root, ".dogsc-export-*.tmp").Any(), "invalid source creates no temporary file");
passed++;
Console.WriteLine("PASS damaged completed media cannot overwrite export target");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try
    {
        await ProjectStore.ExportAsync(exportProject, output, overwrite: true, cancelled.Token);
        throw new Exception("FAIL: cancelled export succeeded");
    }
    catch (OperationCanceledException) { }
    Check(File.ReadAllText(output) == "original target", "cancel preserves old target");
    Check(!Directory.EnumerateFiles(root, ".dogsc-export-*.tmp").Any(), "cancel removes own temporary files");
    passed++;
    Console.WriteLine("PASS cancelled export preserves existing target");
}

var largeProject = CreateComplete("cancel-during-copy");
using (var fixture = new FileStream(largeProject.MediaPath, FileMode.Open, FileAccess.Write))
{
    fixture.SetLength(16 * 1024 * 1024);
    fixture.Position = 40;
    Span<byte> size = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(size, (uint)(fixture.Length - 40));
    fixture.Write(size);
}
using (var cancelling = new CancellationTokenSource())
{
    var exporting = ProjectStore.ExportAsync(largeProject, output, overwrite: true, cancelling.Token);
    cancelling.Cancel();
    bool cancelledDuringExport = false;
    try
    {
        await exporting;
    }
    catch (OperationCanceledException) { cancelledDuringExport = true; }
    Check(!Directory.EnumerateFiles(root, ".dogsc-export-*.tmp").Any(), "in-progress cancel removes temporary file");
    if (cancelledDuringExport)
    {
        Check(File.ReadAllText(output) == "original target", "in-progress cancel preserves old target");
        passed++;
        Console.WriteLine("PASS cancellation after starting export preserves target and cleans temporary file");
    }
    else
    {
        Check(File.ReadAllBytes(output).SequenceEqual(File.ReadAllBytes(largeProject.MediaPath)),
            "export completed before cancellation publishes the complete source");
        skipped++;
        Console.WriteLine("SKIP in-progress cancellation: export completed first; complete target bytes verified");
        File.WriteAllText(output, "original target");
    }
}

try
{
    await ProjectStore.ExportAsync(exportProject, output, overwrite: false);
    throw new Exception("FAIL: unconfirmed overwrite succeeded");
}
catch (IOException) { }
Check(File.ReadAllText(output) == "original target", "refused overwrite preserves old target");
await ProjectStore.ExportAsync(exportProject, output, overwrite: true);
Check(File.ReadAllBytes(output).SequenceEqual(File.ReadAllBytes(exportProject.MediaPath)), "overwrite contents");
var freshOutput = Path.Combine(root, "fresh.mp4");
await ProjectStore.ExportAsync(exportProject, freshOutput, overwrite: false);
Check(File.ReadAllBytes(freshOutput).SequenceEqual(File.ReadAllBytes(exportProject.MediaPath)), "new export contents");
try
{
    await ProjectStore.ExportAsync(exportProject, exportProject.MediaPath, overwrite: true);
    throw new Exception("FAIL: overwrite source succeeded");
}
catch (IOException) { }
Check(!Directory.EnumerateFiles(root, ".dogsc-export-*.tmp").Any(), "export cleanup");
passed++;
Console.WriteLine("PASS export overwrite, new file and source protection");

var cleaned = CreateComplete("delete completed package");
Directory.CreateDirectory(Path.Combine(cleaned.PackagePath, "events"));
File.WriteAllText(Path.Combine(cleaned.PackagePath, "events", "extra.json"), "{}");
ProjectStore.DeleteCompletedRecordingPackage(cleaned);
Check(!Directory.Exists(cleaned.PackagePath), "completed package removed including auxiliary files");
var unfinished = ProjectStore.Create(root, "do not delete unfinished", capture);
WriteFixture(unfinished.MediaPath);
Throws<InvalidDataException>(() => ProjectStore.DeleteCompletedRecordingPackage(unfinished), "unfinished cleanup rejected");
Check(File.Exists(unfinished.MediaPath), "unfinished source preserved");
var protectedProject = CreateComplete("source reference check");
Throws<IOException>(() => ProjectStore.DeleteCompletedRecordingPackage(protectedProject with { MediaPath = freshOutput }), "mismatched source reference rejected");
Check(File.Exists(protectedProject.MediaPath) && File.Exists(freshOutput), "both source and unrelated media preserved");
passed++;
Console.WriteLine("PASS completed-package cleanup and unfinished/mismatched-source protection");

var publicationChecks = SafeFileChecks.Run(root, WriteFixture);
passed += publicationChecks;
Console.WriteLine($"PASS safe file publication: {publicationChecks} assertions");
Console.WriteLine($"Self-check completed: {passed} passed, {skipped} skipped. Fixtures: {root}");
Console.WriteLine("Fixtures check MP4 bounds, WAV recovery and file transactions; they are not actual recording evidence.");

static byte[] WaveFixture(int tag, int bits, int channels, int frames, int tail, bool extensible, bool rf64)
{
    using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
    writer.Write(rf64 ? "RF64"u8 : "RIFF"u8); writer.Write(rf64 ? uint.MaxValue : 0u); writer.Write("WAVE"u8);
    if (rf64)
    {
        writer.Write("ds64"u8); writer.Write(28u); writer.Write(new byte[28]); writer.Write("JUNK"u8); writer.Write(36u); writer.Write(new byte[36]);
    }
    else { writer.Write("JUNK"u8); writer.Write(72u); writer.Write(new byte[72]); }
    int align = channels * bits / 8;
    writer.Write("fmt "u8); writer.Write(extensible ? 40u : 18u); writer.Write((ushort)(extensible ? 65534 : tag));
    writer.Write((ushort)channels); writer.Write(48000u); writer.Write((uint)(48000 * align));
    writer.Write((ushort)align); writer.Write((ushort)bits); writer.Write((ushort)(extensible ? 22 : 0));
    if (extensible)
    {
        writer.Write((ushort)bits); writer.Write(3u);
        writer.Write(new Guid(tag == 1 ? "00000001-0000-0010-8000-00aa00389b71" : "00000003-0000-0010-8000-00aa00389b71").ToByteArray());
    }
    writer.Write("fact"u8); writer.Write(4u); writer.Write(0u);
    writer.Write("data"u8); writer.Write(rf64 ? uint.MaxValue : 0u);
    for (int frame = 0; frame < frames; frame++)
        for (int channel = 0; channel < channels; channel++)
        {
            double value = .25 * Math.Sin(2 * Math.PI * 440 * frame / 48000);
            if (tag == 3 && bits == 32) writer.Write((float)value);
            else if (tag == 3 && bits == 64) writer.Write(value);
            else if (bits == 8) writer.Write((byte)(128 + (int)(127 * value)));
            else { long sample = (long)(value * ((1L << Math.Min(bits - 1, 62)) - 1)); for (int b = 0; b < bits / 8; b++) writer.Write((byte)(sample >> (b * 8))); }
        }
    writer.Write(new byte[tail]); return stream.ToArray();
}

static void WriteFixture(string path)
{
    // This deliberately synthetic fixture exercises file transactions, not media decoding.
    var bytes = new byte[56];
    BinaryPrimitives.WriteUInt32BigEndian(bytes, 24);
    "ftyp"u8.CopyTo(bytes.AsSpan(4));
    "isom"u8.CopyTo(bytes.AsSpan(8));
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24), 16);
    "moov"u8.CopyTo(bytes.AsSpan(28));
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(32), 8);
    "free"u8.CopyTo(bytes.AsSpan(36));
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(40), 16);
    "mdat"u8.CopyTo(bytes.AsSpan(44));
    File.WriteAllBytes(path, bytes);
}

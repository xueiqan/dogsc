using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DogSC.Core;

namespace DogSC.Windows;

internal static class EditorRecordingMarkers
{
    public static RecordingMarker[] Load(ProjectDocument project, RecordingMarker[]? persisted)
    {
        if (persisted is not null) { RecordingMarkerDomain.Validate(persisted); return persisted; }
        var path = Path.Combine(project.PackagePath, "events", "markers.jsonl"); MediaExport.RejectLinks(path);
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 4_194_304) throw new InvalidDataException("录制标记文件超过4MiB。");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        var markers = new List<RecordingMarker>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 4096 || markers.Count >= RecordingMarkerDomain.MaximumMarkers)
                throw new InvalidDataException("录制标记记录过长或数量超限。");
            using var json = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            var item = json.RootElement;
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("kind", out var kind)
                || kind.ValueKind != JsonValueKind.String || kind.GetString() != "marker"
                || !item.TryGetProperty("time", out var time) || !time.TryGetDouble(out var sourceTime)
                || !double.IsFinite(sourceTime) || sourceTime < 0)
                throw new InvalidDataException("录制标记记录无效，原始文件已保留。");
            int number = markers.Count + 1;
            // Old JSONL has no ID; use the immutable ordinal and source anchor so reopening never invents another ID.
            var key = number.ToString(CultureInfo.InvariantCulture) + ":" + sourceTime.ToString("R", CultureInfo.InvariantCulture);
            var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("dogsc.windows.marker:" + key)).AsSpan(0, 16));
            markers.Add(new(id, sourceTime, number));
        }
        return markers.ToArray();
    }
}

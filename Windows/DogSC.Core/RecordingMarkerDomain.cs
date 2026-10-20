using System.Text.Json;

namespace DogSC.Core;

public sealed record RecordingMarker(Guid Id, double SourceTime, int Number);
public sealed record TimelineRecordingMarker(RecordingMarker Marker, double OutputTime);

public static class RecordingMarkerDomain
{
    public const int MaximumMarkers = 100_000;

    public static void Validate(RecordingMarker[]? markers)
    {
        if (markers is null) return;
        if (markers.Length > MaximumMarkers || markers.Any(m => m is null
            || !double.IsFinite(m.SourceTime) || m.SourceTime < 0 || m.Number <= 0)
            || markers.Select(m => m.Id).Distinct().Count() != markers.Length)
            throw new InvalidDataException("录制标记数量、标识或时间无效。");
    }

    public static TimelineRecordingMarker[] Map(RecordingMarker[]? markers, TimelineMap map)
    {
        Validate(markers);
        // The source map forbids reused ranges; a retained source instant has one output owner.
        double? OutputTime(double sourceTime)
        {
            if (map.OutputTime(sourceTime) is { } mapped) return mapped;
            if (Math.Abs(sourceTime - map.FullSourceDuration) > TimelineMap.Epsilon) return null;
            var tail = map.Segments.FirstOrDefault(s => Math.Abs(s.SourceEnd - map.FullSourceDuration) <= TimelineMap.Epsilon);
            return tail is null ? null : tail.OutputEnd - Math.Min(TimelineMap.Epsilon, tail.OutputDuration / 2);
        }
        return (markers ?? []).Select(m => (Marker: m, Time: OutputTime(m.SourceTime)))
            .Where(m => m.Time is not null).Select(m => new TimelineRecordingMarker(m.Marker, m.Time!.Value))
            .OrderBy(m => m.OutputTime).ThenBy(m => m.Marker.Number).ToArray();
    }

    public static RecordingMarker[] ReadLegacy(JsonElement section)
    {
        if (section.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return [];
        if (section.ValueKind != JsonValueKind.Array || section.GetArrayLength() > MaximumMarkers)
            throw new InvalidDataException("原项目录制标记结构或数量无效。");
        var result = new List<RecordingMarker>(); var identifiers = new HashSet<Guid>();
        foreach (var item in section.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out var identifier)
                || !item.TryGetProperty("sourceTime", out var time) || !time.TryGetDouble(out var sourceTime)
                || !item.TryGetProperty("number", out var number) || !number.TryGetInt32(out var value))
                throw new InvalidDataException("原项目录制标记字段无效，原始数据已保留。");
            // Match the Swift decoder: keep the first valid ID and retain anchors outside edited ranges.
            if (double.IsFinite(sourceTime) && sourceTime >= 0 && value > 0 && identifiers.Add(identifier))
                result.Add(new(identifier, sourceTime, value));
        }
        return result.ToArray();
    }
}

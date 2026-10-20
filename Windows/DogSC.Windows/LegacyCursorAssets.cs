using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using DogSC.Core;

namespace DogSC.Windows;

/// <summary>Mac projects store cursor identities, not platform cursor bitmaps.</summary>
public static class LegacyCursorAssets
{
    public static string StyleId(JsonElement style)
    {
        string id;
        if (style.ValueKind == JsonValueKind.Object && style.TryGetProperty("assetID", out var value) && value.ValueKind != JsonValueKind.Null)
            id = value.GetString() ?? "";
        else
        {
            string? appearance = style.ValueKind == JsonValueKind.Object && style.TryGetProperty("appearance", out var old) ? old.GetString() : null;
            id = appearance switch { "指向手势" => "system.pointing-hand", "十字准星" => "system.crosshair", "隐藏" => "system.hidden", _ => "system.arrow" };
        }
        if (id.Length > 200 || id.Any(char.IsControl)) throw new InvalidDataException("原光标素材身份无效。");
        return id.Length == 0 ? "system.arrow" : id;
    }

    public static void Import(LegacyProjectDocument source, string destination, IEnumerable<string?> recordedIds,
        List<string> unsupported, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        destination = Path.GetFullPath(destination); MediaExport.RejectLinks(destination);
        if (!Directory.Exists(destination) || !destination.EndsWith(".dogscproject", StringComparison.OrdinalIgnoreCase)
            || destination.Equals(source.PackagePath, StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(source.PackagePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("光标迁移只能写入原包之外的Windows副本。");
        var ids = new HashSet<string>(StringComparer.Ordinal) { "system.arrow", StyleId(source.Section("cursorStyle")) };
        foreach (var id in recordedIds)
        {
            if (id is null) continue;
            if (id.Length > 200 || id.Any(char.IsControl)) throw new InvalidDataException("原录制光标身份无效。");
            if (id.Length > 0) ids.Add(id);
            if (ids.Count > 4096) throw new InvalidDataException("原光标身份超过4096种。");
        }
        var assets = new List<object>();
        var arrow = Capture("system.arrow", 32512);
        foreach (var id in ids.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (id is "system.automatic" or "system.hidden") continue;
            int resource = id switch
            {
                // shortcut: Windows lacks separate open/closed hand resources; use native hand until original Mac assets are available.
                "system.arrow" => 32512, "system.pointing-hand" or "system.open-hand" or "system.closed-hand" => 32649,
                "system.crosshair" => 32515, "system.ibeam" => 32513, "system.not-allowed" => 32648, _ => 0
            };
            if (resource != 0)
            {
                var asset = id == "system.arrow" ? arrow : Capture(id, resource);
                assets.Add(new { id, asset.RelativePath, asset.Width, asset.Height, asset.HotspotX, asset.HotspotY, asset.InvertMaskRelativePath,
                    scaleReferenceHeight = (double)arrow.Height });
                if (id is "system.open-hand" or "system.closed-hand")
                    unsupported.Add("cursor " + id + "：Windows没有对应的原生开手/握拳资源，使用系统手势；Mac原身份保留。");
            }
            else if (id == "system.touch-dot")
            {
                string relativePath = "assets/cursors/touch-dot.png";
                var path = LegacyProjectCodec.ResolvePackagePath(destination, relativePath);
                WriteNew(path, stream => { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(TouchDot())); encoder.Save(stream); });
                assets.Add(new { id, relativePath, width = 24, height = 24, hotspotX = 12, hotspotY = 12, scaleReferenceHeight = 24.0 });
            }
            else unsupported.Add("cursor " + id + "：按照原版缺失/退役素材规则回落系统箭头，原身份与包内未知文件保留。");
        }
        string manifest = LegacyProjectCodec.ResolvePackagePath(destination, "assets/cursors.json");
        WriteNew(manifest, stream => JsonSerializer.Serialize(stream, new { format = "dogsc.windows.cursor-assets", version = 1, assets },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
        unsupported.Add("cursor：系统PNG和热点取自本机Windows主题；Mac NSCursor图像及系统PDF不在源码或项目包中，未宣称跨平台像素等价。");

        CursorAssetRecord Capture(string id, int resource)
        {
            var handle = LoadCursor(IntPtr.Zero, (IntPtr)resource);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法载入Windows光标：" + id);
            using var snapshot = CursorCaptureAssets.SnapshotFromHandle(handle);
            var asset = CursorCaptureAssets.PersistSnapshot(snapshot, destination);
            if (asset.Width > 512 || asset.Height > 512) throw new InvalidDataException("系统光标超过项目清单的512像素限制。");
            return asset;
        }
    }

    private static BitmapSource TouchDot()
    {
        BitmapSource? result = null; Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var visual = new DrawingVisual();
                var fill = new DrawingVisual { Effect = new DropShadowEffect { Color = Colors.Black, Opacity = .38, BlurRadius = 2.4, Direction = 270, ShadowDepth = .7 } };
                using (var drawing = fill.RenderOpen()) drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(224, 184, 184, 184)), null, new System.Windows.Point(12, 12), 9.5, 9.5);
                visual.Children.Add(fill);
                var rim = new DrawingVisual();
                using (var drawing = rim.RenderOpen()) drawing.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(184, 255, 255, 255)), 1.05), new System.Windows.Point(12, 12), 9.5, 9.5);
                visual.Children.Add(rim);
                var bitmap = new RenderTargetBitmap(24, 24, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); result = bitmap;
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidDataException("触控光标图像生成失败。", failure);
        return result!;
    }

    private static void WriteNew(string path, Action<Stream> write)
    {
        MediaExport.RejectLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); MediaExport.RejectLinks(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); write(stream); stream.Flush(true);
    }
    [DllImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
}

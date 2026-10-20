using System.Buffers.Binary;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DogSC.Windows;

public sealed record CursorAssetRecord(string Id, string RelativePath, int Width, int Height,
    int HotspotX, int HotspotY, string? InvertMaskRelativePath = null);
public sealed record NativeCursorState(IntPtr CursorHandle, int ScreenX, int ScreenY, bool Visible);

public sealed class NativeCursorSnapshot : IDisposable
{
    internal CursorIconHandle Icon { get; }
    public IntPtr SourceCursorHandle { get; }
    public int ScreenX { get; }
    public int ScreenY { get; }
    internal NativeCursorSnapshot(IntPtr source, IntPtr ownedCopy, int x, int y)
    {
        SourceCursorHandle = source; Icon = new(ownedCopy); ScreenX = x; ScreenY = y;
    }
    public void Dispose() => Icon.Dispose();
}

internal sealed class CursorIconHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal CursorIconHandle(IntPtr handle) : base(true) => SetHandle(handle);
    protected override bool ReleaseHandle() => CursorCaptureAssets.DestroyIcon(handle);
}

public static class CursorCaptureAssets
{
    public static NativeCursorSnapshot? Snapshot()
    {
        var state = Peek();
        return state is { Visible: true } && state.CursorHandle != IntPtr.Zero
            ? SnapshotFromHandle(state.CursorHandle, state.ScreenX, state.ScreenY) : null;
    }

    public static NativeCursorState? Peek()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info)) return null;
        return new(info.Cursor, info.Position.X, info.Position.Y, (info.Flags & 1) != 0 && (info.Flags & 2) == 0);
    }

    internal static NativeCursorSnapshot SnapshotFromHandle(IntPtr handle, int x = 0, int y = 0)
    {
        var copy = CopyIcon(handle);
        if (copy == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法复制系统光标。");
        return new(handle, copy, x, y);
    }

    public static CursorAssetRecord PersistSnapshot(NativeCursorSnapshot snapshot, string package)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var root = CheckedPath(package).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("光标素材所属项目不存在。");
        bool retained = false;
        try
        {
            snapshot.Icon.DangerousAddRef(ref retained);
            var handle = snapshot.Icon.DangerousGetHandle();
            if (!GetIconInfo(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取系统光标图像。");
            try
            {
                var bitmapHandle = info.Color != IntPtr.Zero ? info.Color : info.Mask;
                if (GetObject(bitmapHandle, Marshal.SizeOf<NativeBitmap>(), out var bitmap) == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取光标尺寸。");
                int width = bitmap.Width, height = info.Color != IntPtr.Zero ? bitmap.Height : bitmap.Height / 2;
                if (width is < 1 or > 1024 || height is < 1 or > 1024 || info.HotspotX >= width || info.HotspotY >= height)
                    throw new InvalidDataException("光标尺寸或热点超出安全范围。");
                using var rgba = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                Bitmap? invertMask = null;
                try
                {
                    // Icon.ToBitmap loses alpha on some themed cursors; native black/white renders recover actual RGBA/XOR.
                    using var black = DrawCursor(handle, width, height, Color.Black);
                    using var white = DrawCursor(handle, width, height, Color.White);
                    using var monochrome = info.Color == IntPtr.Zero ? Bitmap.FromHbitmap(info.Mask) : null;
                    invertMask = new(width, height, PixelFormat.Format32bppArgb);
                    bool hasInversion = false;
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                        {
                            if (monochrome is not null)
                            {
                                bool and = monochrome.GetPixel(x, y).R >= 128;
                                bool xor = monochrome.GetPixel(x, y + height).R >= 128;
                                rgba.SetPixel(x, y, and ? Color.Transparent : xor ? Color.White : Color.Black);
                                if (and && xor) { invertMask.SetPixel(x, y, Color.White); hasInversion = true; }
                                continue;
                            }
                            var dark = black.GetPixel(x, y); var light = white.GetPixel(x, y);
                            int dr = light.R - dark.R, dg = light.G - dark.G, db = light.B - dark.B;
                            if (Math.Min(dr, Math.Min(dg, db)) < 0 || Math.Max(dr, Math.Max(dg, db)) - Math.Min(dr, Math.Min(dg, db)) > 2)
                            {
                                rgba.SetPixel(x, y, Color.Transparent);
                                invertMask.SetPixel(x, y, Color.FromArgb(255, dark.R, dark.G, dark.B));
                                hasInversion = true;
                            }
                            else
                            {
                                int alpha = 255 - (dr + dg + db - Math.Min(dr, Math.Min(dg, db)) - Math.Max(dr, Math.Max(dg, db)));
                                int Straight(int value) => alpha == 0 ? 0 : Math.Clamp((value * 255 + alpha / 2) / alpha, 0, 255);
                                rgba.SetPixel(x, y, Color.FromArgb(alpha, Straight(dark.R), Straight(dark.G), Straight(dark.B)));
                            }
                        }
                    if (!hasInversion) { invertMask.Dispose(); invertMask = null; }
                    var pixelData = Pixels(rgba); var invertData = invertMask is null ? [] : Pixels(invertMask);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    Span<byte> dimensions = stackalloc byte[16];
                    BinaryPrimitives.WriteInt32LittleEndian(dimensions, width);
                    BinaryPrimitives.WriteInt32LittleEndian(dimensions[4..], height);
                    BinaryPrimitives.WriteInt32LittleEndian(dimensions[8..], (int)info.HotspotX);
                    BinaryPrimitives.WriteInt32LittleEndian(dimensions[12..], (int)info.HotspotY);
                    hash.AppendData(dimensions); hash.AppendData(pixelData); hash.AppendData(invertData);
                    string id = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                    string relative = "assets/cursors/" + id + ".png";
                    string? invertRelative = invertMask is null ? null : "assets/cursors/" + id + ".invert.png";
                    var record = new CursorAssetRecord(id, relative, width, height, (int)info.HotspotX, (int)info.HotspotY, invertRelative);
                    var folder = CheckedPath(Path.Combine(root, "assets", "cursors"));
                    if (!folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("光标素材路径离开项目包。");
                    Directory.CreateDirectory(folder); CheckedPath(folder);
                    SaveNew(Path.Combine(folder, id + ".png"), stream => rgba.Save(stream, ImageFormat.Png));
                    if (invertMask is not null) SaveNew(Path.Combine(folder, id + ".invert.png"), stream => invertMask.Save(stream, ImageFormat.Png));
                    SaveNew(Path.Combine(folder, id + ".json"), stream => JsonSerializer.Serialize(stream, record,
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                    return record;
                }
                finally { invertMask?.Dispose(); }
            }
            finally
            {
                if (info.Color != IntPtr.Zero) DeleteObject(info.Color);
                if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask);
            }
        }
        finally { if (retained) snapshot.Icon.DangerousRelease(); }
    }

    private static void SaveNew(string path, Action<Stream> write)
    {
        path = CheckedPath(path);
        using var encoded = new MemoryStream(); write(encoded);
        var bytes = encoded.ToArray();
        void CheckExisting()
        {
            CheckedPath(path);
            if (new FileInfo(path).Length != bytes.Length || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("项目中已有光标素材与当前内容不一致，原文件已保留。");
        }
        if (File.Exists(path)) { CheckExisting(); return; }
        var temporary = CheckedPath(path + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            CheckedPath(path);
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { CheckExisting(); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Bitmap DrawCursor(IntPtr handle, int width, int height, Color background)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap); graphics.Clear(background);
            var dc = graphics.GetHdc();
            try
            {
                if (!DrawIconEx(dc, 0, 0, handle, width, height, 0, IntPtr.Zero, 3))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法绘制系统光标。");
            }
            finally { graphics.ReleaseHdc(dc); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static byte[] Pixels(Bitmap image)
    {
        var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[checked(image.Width * image.Height * 4)];
            for (int y = 0; y < image.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * image.Width * 4, image.Width * 4);
            return pixels;
        }
        finally { image.UnlockBits(data); }
    }

    private static string CheckedPath(string path)
    {
        var full = Path.GetFullPath(path);
        for (string? current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("光标素材路径不能包含符号链接或重解析点。");
        return full;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size, Flags; public IntPtr Cursor; public NativePoint Position; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { public int Icon; public uint HotspotX, HotspotY; public IntPtr Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap
    { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CopyIcon(IntPtr icon);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon,
        int width, int height, uint frame, IntPtr brush, uint flags);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW", SetLastError = true)] private static extern int GetObject(IntPtr value, int size, out NativeBitmap bitmap);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
}

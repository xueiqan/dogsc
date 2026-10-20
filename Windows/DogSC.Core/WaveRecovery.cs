using System.Buffers.Binary;

namespace DogSC.Core;

// Only the reserved header layout emitted by DogSC is repaired; other WAVs are preserved.
internal static class WaveRecovery
{
    internal static bool RepairCapturedWave(Stream data)
    {
        if (!data.CanRead || !data.CanWrite || !data.CanSeek) throw new ArgumentException("恢复声音需要可读写流。", nameof(data));
        if (data.Length < 100) return false;
        data.Position = 0;
        Span<byte> header = stackalloc byte[12]; data.ReadExactly(header);
        bool rf64 = header[..4].SequenceEqual("RF64"u8);
        if ((!rf64 && !header[..4].SequenceEqual("RIFF"u8)) || !header[8..].SequenceEqual("WAVE"u8)) return false;
        uint riffLength = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        Span<byte> chunk = stackalloc byte[8]; data.ReadExactly(chunk);
        ulong declaredData64 = 0, declaredFrames64 = 0, declaredRiff64 = 0;
        if (rf64)
        {
            if (!chunk[..4].SequenceEqual("ds64"u8) || BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != 28) return false;
            Span<byte> sizes = stackalloc byte[28]; data.ReadExactly(sizes);
            declaredRiff64 = BinaryPrimitives.ReadUInt64LittleEndian(sizes);
            declaredData64 = BinaryPrimitives.ReadUInt64LittleEndian(sizes[8..]);
            declaredFrames64 = BinaryPrimitives.ReadUInt64LittleEndian(sizes[16..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(sizes[24..]) != 0) return false;
            data.ReadExactly(chunk);
            if (!chunk[..4].SequenceEqual("JUNK"u8) || BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != 36) return false;
        }
        else if (!chunk[..4].SequenceEqual("JUNK"u8) || BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != 72) return false;
        data.Position = 92; data.ReadExactly(chunk);
        if (!chunk[..4].SequenceEqual("fmt "u8)) throw new InvalidDataException("录制声音缺少格式头。");
        uint formatLength = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
        if (formatLength is < 18 or > 256 || data.Position + formatLength + 20 > data.Length)
            throw new InvalidDataException("录制声音格式头损坏。");
        byte[] format = new byte[formatLength]; data.ReadExactly(format);
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(format);
        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2));
        uint rate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4));
        uint bytesPerSecond = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(8));
        ushort align = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(12));
        ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14));
        ushort extra = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(16));
        if (channels is < 1 or > 32 || rate is < 8000 or > 384000 || bits is not (8 or 16 or 24 or 32 or 64)
            || align != channels * bits / 8 || bytesPerSecond != rate * align || formatLength != 18 + extra)
            throw new InvalidDataException("录制声音格式参数无效。");
        if (tag == 65534)
        {
            if (extra < 22 || BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(18)) is 0
                || BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(18)) > bits)
                throw new InvalidDataException("扩展声音格式无效。");
            Guid subtype = new(format.AsSpan(24, 16));
            tag = subtype == new Guid("00000001-0000-0010-8000-00aa00389b71") ? (ushort)1
                : subtype == new Guid("00000003-0000-0010-8000-00aa00389b71") ? (ushort)3 : (ushort)0;
        }
        if (tag is not (1 or 3) || tag == 3 && bits is not (32 or 64) || tag == 1 && bits == 64)
            throw new InvalidDataException("录制声音编码格式无效。");
        data.Position += formatLength & 1; data.ReadExactly(chunk);
        if (!chunk[..4].SequenceEqual("fact"u8) || BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != 4)
            throw new InvalidDataException("录制声音采样头损坏。");
        long factOffset = data.Position; Span<byte> samples = stackalloc byte[4]; data.ReadExactly(samples);
        uint declaredFrames = BinaryPrimitives.ReadUInt32LittleEndian(samples); data.ReadExactly(chunk);
        if (!chunk[..4].SequenceEqual("data"u8)) throw new InvalidDataException("录制声音缺少采样数据。");
        long sizeOffset = data.Position - 4, audioOffset = data.Position;
        ulong declaredBytes = rf64 ? declaredData64 : BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
        if (declaredBytes <= (ulong)(data.Length - audioOffset) && declaredBytes % align == 0
            && declaredBytes + (declaredBytes & 1) == (ulong)(data.Length - audioOffset)
            && (rf64 ? declaredRiff64 : riffLength) == (ulong)(data.Length - 8)
            && declaredFrames == Math.Min(declaredBytes / align, uint.MaxValue)
            && (!rf64 || declaredFrames64 == declaredBytes / align && riffLength == uint.MaxValue
                && BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) == uint.MaxValue)) return false;
        long audioBytes = (data.Length - audioOffset) / align * align;
        if (audioBytes == 0) throw new InvalidDataException("录制声音没有完整采样帧。");
        long frames = audioBytes / align, fileLength = checked(audioOffset + audioBytes + (audioBytes & 1));
        data.SetLength(fileLength);
        using var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, leaveOpen: true);
        if ((audioBytes & 1) != 0) { data.Position = fileLength - 1; writer.Write((byte)0); }
        rf64 |= fileLength - 8 >= uint.MaxValue || frames > uint.MaxValue;
        data.Position = 0; writer.Write(rf64 ? "RF64"u8 : "RIFF"u8); writer.Write(rf64 ? uint.MaxValue : checked((uint)(fileLength - 8)));
        if (rf64)
        {
            data.Position = 12; writer.Write("ds64"u8); writer.Write(28u);
            writer.Write((ulong)(fileLength - 8)); writer.Write((ulong)audioBytes); writer.Write((ulong)frames); writer.Write(0u);
            writer.Write("JUNK"u8); writer.Write(36u); writer.Write(new byte[36]);
        }
        data.Position = factOffset; writer.Write((uint)Math.Min(frames, uint.MaxValue));
        data.Position = sizeOffset; writer.Write(rf64 ? uint.MaxValue : checked((uint)audioBytes));
        writer.Flush(); return true;
    }
}

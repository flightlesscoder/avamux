using System.Buffers.Binary;

namespace Avamux.Core.Wiki;

/// <summary>
/// The width and height of a picture read from the first bytes of its file, without decoding it: PNG, GIF, JPEG,
/// BMP and WebP. Lets the editor lay out a page of thousands of pictures at their right sizes before (or without)
/// loading them.
/// </summary>
public static class PictureHeader
{
    /// <summary>Bytes enough for every format here except JPEG, whose size can sit after long metadata.</summary>
    private const int Head = 64 * 1024;

    public static (int Width, int Height)? Read(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
            var buffer = new byte[(int)Math.Min(Head, stream.Length)];
            stream.ReadExactly(buffer);
            return Read(buffer, stream);
        }
        catch (Exception) { return null; }
    }

    /// <param name="rest">The rest of the file, for a JPEG whose size is further in than <paramref name="data"/> reaches.</param>
    public static (int Width, int Height)? Read(ReadOnlySpan<byte> data, Stream? rest = null)
    {
        (int, int)? Valid(long w, long h) => w is > 0 and < 1_000_000 && h is > 0 and < 1_000_000 ? ((int)w, (int)h) : null;

        // PNG: signature, then the IHDR chunk with width and height (big-endian).
        if (data.Length >= 24 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) && data.Slice(12, 4).SequenceEqual("IHDR"u8))
            return Valid(BinaryPrimitives.ReadUInt32BigEndian(data[16..]), BinaryPrimitives.ReadUInt32BigEndian(data[20..]));
        // GIF: "GIF87a"/"GIF89a", then the logical screen size (little-endian).
        if (data.Length >= 10 && data[..3].SequenceEqual("GIF"u8))
            return Valid(BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]));
        // BMP: "BM", then a header whose size says which layout follows.
        if (data.Length >= 26 && data[0] == 'B' && data[1] == 'M')
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(data[14..]);
            if (header == 12) return Valid(BinaryPrimitives.ReadUInt16LittleEndian(data[18..]), BinaryPrimitives.ReadUInt16LittleEndian(data[20..]));
            return Valid(BinaryPrimitives.ReadInt32LittleEndian(data[18..]), Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(data[22..])));
        }
        // WebP: RIFF container with a VP8, VP8L or VP8X chunk.
        if (data.Length >= 30 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            var chunk = data.Slice(12, 4);
            if (chunk.SequenceEqual("VP8 "u8) && data[23] == 0x9D && data[24] == 0x01 && data[25] == 0x2A)
                return Valid(BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3FFF);
            if (chunk.SequenceEqual("VP8L"u8) && data[20] == 0x2F)
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]);
                return Valid((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
            }
            if (chunk.SequenceEqual("VP8X"u8))
                return Valid((data[24] | data[25] << 8 | data[26] << 16) + 1, (data[27] | data[28] << 8 | data[29] << 16) + 1);
            return null;
        }
        // JPEG: walk the segments to the first start-of-frame marker.
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
            return Jpeg(data, rest);
        return null;
    }

    private static (int, int)? Jpeg(ReadOnlySpan<byte> data, Stream? rest)
    {
        var at = 2;
        while (true)
        {
            if (at + 9 > data.Length)
            {
                // The frame header is past what was read: read on, segment by segment, if the file is at hand.
                if (rest is null || !rest.CanSeek) return null;
                return JpegFrom(rest, at);
            }
            if (data[at] != 0xFF) return null;
            var marker = data[at + 1];
            if (marker == 0xFF) { at++; continue; }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { at += 2; continue; }
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return ((int, int)?)(BinaryPrimitives.ReadUInt16BigEndian(data[(at + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(data[(at + 5)..])) is var (w, h) && w > 0 && h > 0 ? (w, h) : null;
            if (marker == 0xD9 || length < 2) return null;
            at += 2 + length;
        }
    }

    private static (int, int)? JpegFrom(Stream stream, long at)
    {
        Span<byte> b = stackalloc byte[9];
        for (var guard = 0; guard < 10_000; guard++)
        {
            stream.Position = at;
            if (stream.Read(b) < 9 || b[0] != 0xFF) return null;
            var marker = b[1];
            if (marker == 0xFF) { at++; continue; }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { at += 2; continue; }
            var length = BinaryPrimitives.ReadUInt16BigEndian(b[2..]);
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                int w = BinaryPrimitives.ReadUInt16BigEndian(b[7..]), h = BinaryPrimitives.ReadUInt16BigEndian(b[5..]);
                return w > 0 && h > 0 ? (w, h) : null;
            }
            if (marker == 0xD9 || length < 2) return null;
            at += 2 + length;
        }
        return null;
    }
}

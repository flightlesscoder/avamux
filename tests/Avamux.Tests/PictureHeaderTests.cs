using System.Buffers.Binary;
using Avamux.Core.Wiki;

namespace Avamux.Tests;

/// <summary>Picture sizes read from file headers, so pages of many pictures lay out without decoding them all.</summary>
public class PictureHeaderTests
{
    private static byte[] Png(int w, int h)
    {
        var b = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 13);
        "IHDR"u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), (uint)h);
        return b;
    }

    private static byte[] Jpeg(int w, int h, int metadataBytes = 0)
    {
        var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        // APP segments (EXIF and the like) before the frame, each at most 65533 bytes of content.
        for (var left = metadataBytes; left > 0; left -= 65000)
        {
            var n = Math.Min(65000, left);
            ms.Write([0xFF, 0xE1, (byte)((n + 2) >> 8), (byte)(n + 2)]);
            ms.Write(new byte[n]);
        }
        ms.Write([0xFF, 0xDB, 0x00, 0x04, 0x00, 0x00]); // a quantization table segment
        ms.Write([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w]);
        ms.Write(new byte[12]);
        return ms.ToArray();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4000, 3000)]
    [InlineData(65535, 2)]
    public void Png_and_jpeg(int w, int h)
    {
        Assert.Equal((w, h), PictureHeader.Read(Png(w, h)));
        Assert.Equal((w, h), PictureHeader.Read(Jpeg(w, h)));
    }

    [Fact]
    public void Jpeg_with_metadata_longer_than_the_first_read()
    {
        using var dir = new Sandbox();
        var file = Path.Combine(dir.Root, "photo.jpg");
        Directory.CreateDirectory(dir.Root);
        File.WriteAllBytes(file, Jpeg(1920, 1080, metadataBytes: 200_000));
        Assert.Equal((1920, 1080), PictureHeader.Read(file));
    }

    [Fact]
    public void Gif_bmp_and_webp()
    {
        var gif = "GIF89a"u8.ToArray().Concat(new byte[] { 0x40, 0x01, 0xF0, 0x00, 0, 0, 0 }).ToArray();
        Assert.Equal((320, 240), PictureHeader.Read(gif));

        var bmp = new byte[30];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 640);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -480); // top-down rows
        Assert.Equal((640, 480), PictureHeader.Read(bmp));

        byte[] Riff(string chunk, byte[] payload) => "RIFF"u8.ToArray().Concat(new byte[4]).Concat("WEBP"u8.ToArray()).Concat(System.Text.Encoding.ASCII.GetBytes(chunk)).Concat(new byte[4]).Concat(payload).Concat(new byte[16]).ToArray();
        var vp8x = new byte[10];
        vp8x[4] = 799 & 0xFF; vp8x[5] = 799 >> 8; vp8x[7] = 599 & 0xFF; vp8x[8] = 599 >> 8;
        Assert.Equal((800, 600), PictureHeader.Read(Riff("VP8X", vp8x)));
        var vp8l = new byte[5];
        vp8l[0] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(vp8l.AsSpan(1), (uint)((100 - 1) | (50 - 1) << 14));
        Assert.Equal((100, 50), PictureHeader.Read(Riff("VP8L", vp8l)));
        var vp8 = new byte[10];
        vp8[3] = 0x9D; vp8[4] = 0x01; vp8[5] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(vp8.AsSpan(6), 1024);
        BinaryPrimitives.WriteUInt16LittleEndian(vp8.AsSpan(8), 768);
        Assert.Equal((1024, 768), PictureHeader.Read(Riff("VP8 ", vp8)));
    }

    [Fact]
    public void Anything_else_has_no_size()
    {
        Assert.Null(PictureHeader.Read("this is not a picture"u8));
        Assert.Null(PictureHeader.Read([]));
        Assert.Null(PictureHeader.Read(Png(0, 10)));
        Assert.Null(PictureHeader.Read(Jpeg(1, 1).Take(8).ToArray()));
        Assert.Null(PictureHeader.Read("/no/such/file.png"));
    }
}

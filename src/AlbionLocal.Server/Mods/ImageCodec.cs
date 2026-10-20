using System.Buffers.Binary;
using System.IO.Compression;

namespace AlbionLocal.Server.Mods;

/// <summary>
/// The Unity texture formats the icon extractor needs (icons SPEC §2): the client's item and spell atlases are RGBA32; ARGB32, RGB24
/// and DXT1/DXT5 (BC1/BC3) are here for other atlases. Output is RGBA, rows in Unity order (row 0 = the bottom of the image).
/// </summary>
public static class TextureDecode
{
    public const int RGB24 = 3, RGBA32 = 4, ARGB32 = 5, DXT1 = 10, DXT5 = 12;

    public static bool Supported(int format) => format is RGB24 or RGBA32 or ARGB32 or DXT1 or DXT5;

    public static string FormatName(int format) => format switch
    {
        RGB24 => "RGB24", RGBA32 => "RGBA32", ARGB32 => "ARGB32", DXT1 => "DXT1", DXT5 => "DXT5", 25 => "BC7", 24 => "BC6H", 34 => "ETC_RGB4",
        47 => "ETC2_RGBA8", 48 => "ASTC_4x4", 63 => "BC4", 64 => "BC5", _ => "format " + format,
    };

    /// <summary>Bytes of mip 0 of a <paramref name="width"/>×<paramref name="height"/> texture.</summary>
    public static long Mip0Size(int format, int width, int height) => format switch
    {
        RGB24 => 3L * width * height,
        RGBA32 or ARGB32 => 4L * width * height,
        DXT1 => 8L * ((width + 3) / 4) * ((height + 3) / 4),
        DXT5 => 16L * ((width + 3) / 4) * ((height + 3) / 4),
        _ => throw new InvalidDataException($"texture {FormatName(format)} is not supported"),
    };

    /// <summary>RGBA pixels of mip 0 (RGBA32 data is returned as it is).</summary>
    public static byte[] ToRgba(int format, int width, int height, byte[] data)
    {
        var need = Mip0Size(format, width, height);
        if (data.Length < need) throw new InvalidDataException($"texture data {data.Length} bytes, {need} expected");
        switch (format)
        {
            case RGBA32: return data;
            case ARGB32:
            {
                var o = new byte[4L * width * height];
                for (long i = 0; i < o.Length; i += 4) { o[i] = data[i + 1]; o[i + 1] = data[i + 2]; o[i + 2] = data[i + 3]; o[i + 3] = data[i]; }
                return o;
            }
            case RGB24:
            {
                var o = new byte[4L * width * height];
                for (long i = 0, j = 0; i < o.Length; i += 4, j += 3) { o[i] = data[j]; o[i + 1] = data[j + 1]; o[i + 2] = data[j + 2]; o[i + 3] = 255; }
                return o;
            }
            default:
            {
                var o = new byte[4L * width * height];
                var block = new byte[64];
                var size = format == DXT1 ? 8 : 16;
                var bw = (width + 3) / 4;
                for (int by = 0, at = 0; by < (height + 3) / 4; by++)
                    for (var bx = 0; bx < bw; bx++, at += size)
                    {
                        if (format == DXT1) DecodeDxt1(data.AsSpan(at, 8), block);
                        else DecodeDxt5(data.AsSpan(at, 16), block);
                        for (var py = 0; py < 4 && by * 4 + py < height; py++)
                            for (var px = 0; px < 4 && bx * 4 + px < width; px++)
                                Array.Copy(block, (py * 4 + px) * 4, o, ((long)(by * 4 + py) * width + bx * 4 + px) * 4, 4);
                    }
                return o;
            }
        }
    }

    /// <summary>One BC1 block (8 bytes) → 16 RGBA pixels, row-major within the block.</summary>
    public static void DecodeDxt1(ReadOnlySpan<byte> b, Span<byte> rgba) => ColorBlock(b, rgba, alpha: true);

    /// <summary>One BC3 block (16 bytes: 8 alpha, 8 colour) → 16 RGBA pixels.</summary>
    public static void DecodeDxt5(ReadOnlySpan<byte> b, Span<byte> rgba)
    {
        ColorBlock(b[8..], rgba, alpha: false);
        Span<byte> a = stackalloc byte[8];
        a[0] = b[0];
        a[1] = b[1];
        if (a[0] > a[1]) for (var i = 1; i < 7; i++) a[i + 1] = (byte)(((7 - i) * a[0] + i * a[1]) / 7);
        else
        {
            for (var i = 1; i < 5; i++) a[i + 1] = (byte)(((5 - i) * a[0] + i * a[1]) / 5);
            a[6] = 0;
            a[7] = 255;
        }
        ulong bits = 0;
        for (var i = 0; i < 6; i++) bits |= (ulong)b[2 + i] << (8 * i);
        for (var p = 0; p < 16; p++) rgba[p * 4 + 3] = a[(int)((bits >> (3 * p)) & 7)];
    }

    private static void ColorBlock(ReadOnlySpan<byte> b, Span<byte> rgba, bool alpha)
    {
        var c0 = BinaryPrimitives.ReadUInt16LittleEndian(b);
        var c1 = BinaryPrimitives.ReadUInt16LittleEndian(b[2..]);
        Span<byte> c = stackalloc byte[16];
        Rgb565(c0, c[..4]);
        Rgb565(c1, c[4..8]);
        if (c0 > c1 || !alpha)
        {
            for (var k = 0; k < 3; k++)
            {
                c[8 + k] = (byte)((2 * c[k] + c[4 + k]) / 3);
                c[12 + k] = (byte)((c[k] + 2 * c[4 + k]) / 3);
            }
            c[11] = c[15] = 255;
        }
        else
        {
            for (var k = 0; k < 3; k++) c[8 + k] = (byte)((c[k] + c[4 + k]) / 2);
            c[11] = 255;
            c[12] = c[13] = c[14] = c[15] = 0;
        }
        var idx = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        for (var p = 0; p < 16; p++) c.Slice((int)((idx >> (2 * p)) & 3) * 4, 4).CopyTo(rgba.Slice(p * 4, 4));
    }

    private static void Rgb565(ushort v, Span<byte> o)
    {
        int r = (v >> 11) & 31, g = (v >> 5) & 63, b = v & 31;
        o[0] = (byte)((r << 3) | (r >> 2));
        o[1] = (byte)((g << 2) | (g >> 4));
        o[2] = (byte)((b << 3) | (b >> 2));
        o[3] = 255;
    }

    /// <summary>
    /// A sprite out of an RGBA atlas in Unity row order: (<paramref name="x"/>, <paramref name="y"/>) is its top-left corner counted from
    /// the atlas's top-left (NGUI's UISpriteData), and the padding is added back as transparent pixels. Returns top-down RGBA.
    /// </summary>
    public static byte[] Crop(byte[] atlas, int atlasW, int atlasH, int x, int y, int w, int h, int padL = 0, int padT = 0, int padR = 0, int padB = 0)
    {
        int ow = w + padL + padR, oh = h + padT + padB;
        var o = new byte[4L * ow * oh];
        for (var j = 0; j < h; j++)
        {
            var sy = y + j;
            if (sy < 0 || sy >= atlasH) continue;
            var srcRow = (long)(atlasH - 1 - sy) * atlasW;
            for (var i = 0; i < w; i++)
            {
                var sx = x + i;
                if (sx < 0 || sx >= atlasW) continue;
                Array.Copy(atlas, (srcRow + sx) * 4, o, ((long)(padT + j) * ow + padL + i) * 4, 4);
            }
        }
        return o;
    }
}

/// <summary>A minimal PNG writer and reader (8-bit RGBA, no interlace): zlib from System.IO.Compression, own CRC-32.</summary>
public static class Png
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = MakeTable();

    private static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public static uint Crc32(ReadOnlySpan<byte> data, uint crc = 0)
    {
        crc = ~crc;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    /// <summary>Top-down RGBA → PNG bytes; each row gets the filter with the smallest sum of absolute differences.</summary>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0 || rgba.Length < 4L * width * height) throw new ArgumentException("bad image size");
        var stride = width * 4;
        var raw = new byte[(long)(stride + 1) * height];
        var cand = new byte[5][];
        for (var f = 0; f < 5; f++) cand[f] = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var row = rgba.Slice(y * stride, stride);
            var prev = y > 0 ? rgba.Slice((y - 1) * stride, stride) : default;
            var best = 0;
            var bestSum = long.MaxValue;
            for (var f = 0; f < 5; f++)
            {
                long sum = 0;
                for (var i = 0; i < stride; i++)
                {
                    int a = i >= 4 ? row[i - 4] : 0, b = y > 0 ? prev[i] : 0, c = i >= 4 && y > 0 ? prev[i - 4] : 0;
                    var v = (byte)(row[i] - f switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1, _ => Paeth(a, b, c) });
                    cand[f][i] = v;
                    sum += v < 128 ? v : 256 - v;
                }
                if (sum < bestSum) { bestSum = sum; best = f; }
            }
            var at = (long)y * (stride + 1);
            raw[at] = (byte)best;
            Array.Copy(cand[best], 0, raw, at + 1, stride);
        }
        using var ms = new MemoryStream();
        ms.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        Chunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
        for (var i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head);
        s.Write(data);
        var crc = Crc32(data, Crc32(head[4..]));
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
        s.Write(tail);
    }

    /// <summary>PNG (8-bit RGBA, as <see cref="Encode"/> writes) → (width, height, top-down RGBA); CRCs are checked.</summary>
    public static (int Width, int Height, byte[] Rgba) Decode(byte[] png)
    {
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("not a PNG");
        int width = 0, height = 0;
        using var idat = new MemoryStream();
        for (var p = 8; p + 12 <= png.Length;)
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p));
            var type = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
            if (len < 0 || p + 12 + len > png.Length) throw new InvalidDataException("PNG chunk out of range");
            if (Crc32(png.AsSpan(p + 8, len), Crc32(png.AsSpan(p + 4, 4))) != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p + 8 + len)))
                throw new InvalidDataException($"PNG chunk {type}: bad CRC");
            var data = png.AsSpan(p + 8, len);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                if (data[8] != 8 || data[9] != 6 || data[12] != 0) throw new InvalidDataException("only 8-bit RGBA non-interlaced PNG");
            }
            else if (type == "IDAT") idat.Write(data);
            else if (type == "IEND") break;
            p += 12 + len;
        }
        var stride = width * 4;
        var raw = new byte[(long)(stride + 1) * height];
        idat.Position = 0;
        using (var zs = new ZLibStream(idat, CompressionMode.Decompress)) zs.ReadExactly(raw);
        var o = new byte[(long)stride * height];
        for (var y = 0; y < height; y++)
        {
            var f = raw[(long)y * (stride + 1)];
            for (var i = 0; i < stride; i++)
            {
                var at = (long)y * stride + i;
                int a = i >= 4 ? o[at - 4] : 0, b = y > 0 ? o[at - stride] : 0, c = i >= 4 && y > 0 ? o[at - stride - 4] : 0;
                o[at] = (byte)(raw[(long)y * (stride + 1) + 1 + i] + f switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1, 4 => Paeth(a, b, c), _ => throw new InvalidDataException("bad PNG filter") });
            }
        }
        return (width, height, o);
    }
}

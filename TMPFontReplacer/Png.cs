using System.IO.Compression;

namespace TMPFontReplacer;

public static class Png
{
    /// <summary>Write an 8-bit grayscale PNG. Input rows are bottom-up (Unity layout); PNG is written top-down.</summary>
    public static void WriteGray(string path, byte[] bottomUp, int w, int h)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true))
        {
            for (int y = h - 1; y >= 0; y--) { z.WriteByte(0); z.Write(bottomUp, y * w, w); }
        }
        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        Be(ihdr, 0, w); Be(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 0;
        Chunk(fs, "IHDR", ihdr);
        Chunk(fs, "IDAT", ms.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    /// <summary>Write a top-down RGB PNG.</summary>
    public static void WriteRgb(string path, byte[] rgb, int w, int h)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true))
            for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgb, y * w * 3, w * 3); }
        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        Be(ihdr, 0, w); Be(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(fs, "IHDR", ihdr);
        Chunk(fs, "IDAT", ms.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    static void Be(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; Be(len, 0, data.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t); s.Write(data);
        uint crc = Crc(t, 0xFFFFFFFF); crc = Crc(data, crc) ^ 0xFFFFFFFF;
        var c = new byte[4]; Be(c, 0, (int)crc); s.Write(c);
    }

    static uint[]? table;
    static uint Crc(byte[] buf, uint c)
    {
        if (table == null)
        {
            table = new uint[256];
            for (uint n = 0; n < 256; n++) { uint x = n; for (int k = 0; k < 8; k++) x = (x & 1) != 0 ? 0xEDB88320 ^ (x >> 1) : x >> 1; table[n] = x; }
        }
        foreach (var b in buf) c = table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c;
    }
}

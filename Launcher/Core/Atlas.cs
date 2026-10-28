using System.IO.Compression;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace TranslationLauncher.Core;

/// <summary>Font atlas lookup (TMP font → Texture2D) and texture decoding for previews.</summary>
public static class Atlas
{
    public sealed record TexRef(int File, long PathId, string? ExternalPath);

    public sealed class TexInfo
    {
        public string Name = ""; public int Width, Height, Format; public string FormatName = "";
        public int File; public long PathId; public string? External;
    }

    // ------------------------------------------------------------------ font → atlas
    /// <summary>All atlas textures referenced by a TMP font asset (modern m_AtlasTextures or legacy atlas).</summary>
    public static List<TexRef> FontAtlases(UnityDoc d, int file, long pathId)
    {
        var res = new List<TexRef>();
        var f = d.Field(file, pathId, out _);
        if (f == null) return res;
        var arr = f["m_AtlasTextures"];
        if (!arr.IsDummy && !arr["Array"].IsDummy)
            foreach (var p in arr["Array"].Children) { var r = Resolve(d, file, p); if (r != null) res.Add(r); }
        if (res.Count == 0 && !f["atlas"].IsDummy) { var r = Resolve(d, file, f["atlas"]); if (r != null) res.Add(r); }
        if (res.Count == 0 && !f["m_AtlasTexture"].IsDummy) { var r = Resolve(d, file, f["m_AtlasTexture"]); if (r != null) res.Add(r); }
        return res;
    }

    public static TexRef? Resolve(UnityDoc d, int file, AssetTypeValueField pptr)
    {
        if (pptr.IsDummy) return null;
        int fid = pptr["m_FileID"].AsInt; long pid = pptr["m_PathID"].AsLong;
        if (pid == 0) return null;
        if (fid == 0) return new TexRef(file, pid, null);
        var ext = d.Files[file].file.Metadata.Externals;
        if (fid - 1 >= ext.Count) return null;
        var path = ext[fid - 1].PathName;
        var fname = Path.GetFileName(path.Replace('\\', '/'));
        for (int i = 0; i < d.Files.Count; i++)
            if (string.Equals(Path.GetFileName(d.Files[i].name), fname, StringComparison.OrdinalIgnoreCase)) return new TexRef(i, pid, null);
        return new TexRef(-1, pid, path);
    }

    public static TexInfo? Info(UnityDoc d, int file, long pathId)
    {
        var t = d.Field(file, pathId, out _);
        if (t == null || t["m_Width"].IsDummy) return null;
        int fmt = t["m_TextureFormat"].AsInt;
        return new TexInfo
        {
            Name = t["m_Name"].IsDummy ? "" : t["m_Name"].AsString, Width = t["m_Width"].AsInt, Height = t["m_Height"].AsInt,
            Format = fmt, FormatName = FormatName(fmt), File = file, PathId = pathId
        };
    }

    // ------------------------------------------------------------------ pixels
    /// <summary>Decode a Texture2D into top-down RGBA. Throws with a readable message for unsupported formats.</summary>
    public static (byte[] rgba, int w, int h, int fmt, bool alphaOnly) Decode(UnityDoc d, int file, long pathId, string? resDir)
    {
        var t = d.Field(file, pathId, out _) ?? throw new Exception("Texture layout unknown");
        int w = t["m_Width"].AsInt, h = t["m_Height"].AsInt, fmt = t["m_TextureFormat"].AsInt;
        byte[] data = t["image data"].IsDummy ? Array.Empty<byte>() : t["image data"].AsByteArray;
        var sd = t["m_StreamData"];
        if (data.Length == 0 && !sd.IsDummy && sd["size"].AsLong > 0)
            data = ReadStream(d, sd["path"].AsString, sd["offset"].AsLong, (int)sd["size"].AsLong, resDir)
                   ?? throw new Exception("Texture data file not found: " + sd["path"].AsString);
        if (w <= 0 || h <= 0 || data.Length == 0) throw new Exception("Empty texture");
        var rgba = new byte[w * h * 4];
        bool alphaOnly = false;
        int n = w * h;
        void Need(int bytes) { if (data.Length < bytes) throw new Exception($"Texture data too short ({data.Length} < {bytes})"); }
        switch (fmt)
        {
            case 1: // Alpha8
                Need(n); alphaOnly = true;
                for (int i = 0; i < n; i++) { rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 255; rgba[i * 4 + 3] = data[i]; }
                break;
            case 63: // R8
                Need(n); alphaOnly = true;
                for (int i = 0; i < n; i++) { rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 255; rgba[i * 4 + 3] = data[i]; }
                break;
            case 9: // R16
                Need(n * 2); alphaOnly = true;
                for (int i = 0; i < n; i++) { rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 255; rgba[i * 4 + 3] = data[i * 2 + 1]; }
                break;
            case 3: Need(n * 3); for (int i = 0; i < n; i++) { rgba[i * 4] = data[i * 3]; rgba[i * 4 + 1] = data[i * 3 + 1]; rgba[i * 4 + 2] = data[i * 3 + 2]; rgba[i * 4 + 3] = 255; } break;
            case 4: Need(n * 4); Buffer.BlockCopy(data, 0, rgba, 0, n * 4); break;
            case 5: Need(n * 4); for (int i = 0; i < n; i++) { rgba[i * 4] = data[i * 4 + 1]; rgba[i * 4 + 1] = data[i * 4 + 2]; rgba[i * 4 + 2] = data[i * 4 + 3]; rgba[i * 4 + 3] = data[i * 4]; } break;
            case 14: Need(n * 4); for (int i = 0; i < n; i++) { rgba[i * 4] = data[i * 4 + 2]; rgba[i * 4 + 1] = data[i * 4 + 1]; rgba[i * 4 + 2] = data[i * 4]; rgba[i * 4 + 3] = data[i * 4 + 3]; } break;
            case 2: case 13: // ARGB4444 / RGBA4444
                Need(n * 2);
                for (int i = 0; i < n; i++)
                {
                    int v = data[i * 2] | (data[i * 2 + 1] << 8);
                    int a = fmt == 2 ? (v >> 12) & 15 : v & 15, r = fmt == 2 ? (v >> 8) & 15 : (v >> 12) & 15, g = fmt == 2 ? (v >> 4) & 15 : (v >> 8) & 15, b = fmt == 2 ? v & 15 : (v >> 4) & 15;
                    rgba[i * 4] = (byte)(r * 17); rgba[i * 4 + 1] = (byte)(g * 17); rgba[i * 4 + 2] = (byte)(b * 17); rgba[i * 4 + 3] = (byte)(a * 17);
                }
                break;
            case 7: // RGB565
                Need(n * 2);
                for (int i = 0; i < n; i++) { int v = data[i * 2] | (data[i * 2 + 1] << 8); Rgb565(v, rgba, i * 4); rgba[i * 4 + 3] = 255; }
                break;
            case 10: DecodeBC(data, w, h, rgba, false); break;
            case 12: DecodeBC(data, w, h, rgba, true); break;
            default: throw new Exception($"Preview of texture format {FormatName(fmt)} ({fmt}) is not supported (compressed mobile/crunch format).");
        }
        // Unity stores rows bottom-up → flip to top-down
        var outp = new byte[rgba.Length]; int stride = w * 4;
        for (int y = 0; y < h; y++) Buffer.BlockCopy(rgba, (h - 1 - y) * stride, outp, y * stride, stride);
        return (outp, w, h, fmt, alphaOnly);
    }

    static void Rgb565(int v, byte[] o, int p)
    {
        int r = (v >> 11) & 31, g = (v >> 5) & 63, b = v & 31;
        o[p] = (byte)((r << 3) | (r >> 2)); o[p + 1] = (byte)((g << 2) | (g >> 4)); o[p + 2] = (byte)((b << 3) | (b >> 2));
    }

    static void DecodeBC(byte[] d, int w, int h, byte[] o, bool bc3)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4, bs = bc3 ? 16 : 8, pos = 0;
        var col = new byte[16]; var alpha = new byte[16];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++, pos += bs)
            {
                if (pos + bs > d.Length) return;
                int cp = pos;
                if (bc3)
                {
                    int a0 = d[pos], a1 = d[pos + 1];
                    var pal = new int[8]; pal[0] = a0; pal[1] = a1;
                    if (a0 > a1) for (int k = 1; k < 7; k++) pal[k + 1] = ((7 - k) * a0 + k * a1) / 7;
                    else { for (int k = 1; k < 5; k++) pal[k + 1] = ((5 - k) * a0 + k * a1) / 5; pal[6] = 0; pal[7] = 255; }
                    ulong bits = 0; for (int k = 0; k < 6; k++) bits |= (ulong)d[pos + 2 + k] << (8 * k);
                    for (int k = 0; k < 16; k++) alpha[k] = (byte)pal[(bits >> (3 * k)) & 7];
                    cp = pos + 8;
                }
                int c0 = d[cp] | (d[cp + 1] << 8), c1 = d[cp + 2] | (d[cp + 3] << 8);
                var c = new byte[16]; Rgb565(c0, c, 0); Rgb565(c1, c, 4); c[3] = c[7] = 255;
                if (c0 > c1 || bc3)
                {
                    for (int k = 0; k < 3; k++) { c[8 + k] = (byte)((2 * c[k] + c[4 + k]) / 3); c[12 + k] = (byte)((c[k] + 2 * c[4 + k]) / 3); }
                    c[11] = c[15] = 255;
                }
                else { for (int k = 0; k < 3; k++) c[8 + k] = (byte)((c[k] + c[4 + k]) / 2); c[11] = 255; c[12] = c[13] = c[14] = c[15] = 0; }
                uint idx = (uint)(d[cp + 4] | (d[cp + 5] << 8) | (d[cp + 6] << 16) | (d[cp + 7] << 24));
                for (int py = 0; py < 4; py++)
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px, y = by * 4 + py; if (x >= w || y >= h) continue;
                        int k = py * 4 + px, ci = (int)((idx >> (2 * k)) & 3) * 4, op = (y * w + x) * 4;
                        o[op] = c[ci]; o[op + 1] = c[ci + 1]; o[op + 2] = c[ci + 2]; o[op + 3] = bc3 ? alpha[k] : c[ci + 3];
                    }
            }
    }

    /// <summary>Bytes of a texture stored outside the asset (.resS / .resource, inside a bundle or next to the file).</summary>
    public static byte[]? ReadStream(UnityDoc d, string path, long offset, int size, string? resDir)
    {
        var name = Path.GetFileName(path.Replace('\\', '/'));
        if (d.Bundle != null)
        {
            var dirs = d.Bundle.file.BlockAndDirInfo.DirectoryInfos;
            foreach (var di in dirs)
                if (string.Equals(Path.GetFileName(di.Name), name, StringComparison.OrdinalIgnoreCase))
                {
                    var r = d.Bundle.file.DataReader;
                    lock (r) { r.Position = di.Offset + offset; return r.ReadBytes(size); }
                }
        }
        if (d.Legacy != null)
            foreach (var (n, data) in d.Legacy.Files)
                if (string.Equals(Path.GetFileName(n), name, StringComparison.OrdinalIgnoreCase))
                    return data.AsSpan((int)offset, size).ToArray();
        foreach (var dir in new[] { Path.GetDirectoryName(d.RealPath), Path.GetDirectoryName(d.LogicalPath), resDir })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var f = Path.Combine(dir, name);
            if (!File.Exists(f)) continue;
            using var fs = File.OpenRead(f);
            fs.Position = offset; var b = new byte[size]; fs.ReadExactly(b); return b;
        }
        return null;
    }

    // ------------------------------------------------------------------ PNG
    /// <summary>RGBA (top-down) → PNG. Alpha-only atlases are written as gray+alpha (half the size).</summary>
    public static byte[] ToPng(byte[] rgba, int w, int h, bool alphaOnly)
    {
        int ch = alphaOnly ? 2 : 4;
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true))
        {
            var row = new byte[w * ch + 1];
            for (int y = 0; y < h; y++)
            {
                row[0] = 0;
                if (alphaOnly) for (int x = 0; x < w; x++) { row[1 + x * 2] = 255; row[2 + x * 2] = rgba[(y * w + x) * 4 + 3]; }
                else Buffer.BlockCopy(rgba, y * w * 4, row, 1, w * 4);
                z.Write(row);
            }
        }
        using var o = new MemoryStream();
        o.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13]; Be(ihdr, 0, w); Be(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = (byte)(alphaOnly ? 4 : 6);
        Chunk(o, "IHDR", ihdr); Chunk(o, "IDAT", ms.ToArray()); Chunk(o, "IEND", Array.Empty<byte>());
        return o.ToArray();
    }

    static void Be(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    static uint[]? table;
    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; Be(len, 0, data.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type); s.Write(t); s.Write(data);
        if (table == null) { var tb = new uint[256]; for (uint n = 0; n < 256; n++) { uint x = n; for (int k = 0; k < 8; k++) x = (x & 1) != 0 ? 0xEDB88320 ^ (x >> 1) : x >> 1; tb[n] = x; } table = tb; }
        uint c = 0xFFFFFFFF; foreach (var b in t) c = table[(c ^ b) & 0xFF] ^ (c >> 8); foreach (var b in data) c = table[(c ^ b) & 0xFF] ^ (c >> 8);
        var cb = new byte[4]; Be(cb, 0, (int)(c ^ 0xFFFFFFFF)); s.Write(cb);
    }

    public static string FormatName(int f) => f switch
    {
        1 => "Alpha8", 2 => "ARGB4444", 3 => "RGB24", 4 => "RGBA32", 5 => "ARGB32", 7 => "RGB565", 9 => "R16", 10 => "DXT1", 12 => "DXT5",
        13 => "RGBA4444", 14 => "BGRA32", 25 => "BC7", 26 => "BC4", 27 => "BC5", 28 => "DXT1Crunched", 29 => "DXT5Crunched", 34 => "ETC_RGB4",
        47 => "ETC2_RGBA8", 48 => "ASTC_4x4", 62 => "RG16", 63 => "R8", _ => "#" + f
    };
}

using System.Text;
using System.Text.RegularExpressions;

namespace TranslationLauncher.Core;

/// <summary>
/// Addressables catalog (catalog.json / catalog.bin): a re-packed bundle gets a new CRC, and the game refuses
/// to load it while the catalog still holds the old one. Like "patchcrc" of AddressablesTools, every
/// AssetBundleRequestOptions.m_Crc is set to 0 (= no check). The file is patched in place, byte length and all
/// offsets stay the same.
/// </summary>
public static class Catalog
{
    const string AbroClass = "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleRequestOptions";

    public static bool IsCatalogName(string fileName)
    {
        var n = fileName.ToLowerInvariant();
        return n.StartsWith("catalog") && (n.EndsWith(".json") || n.EndsWith(".bin"));
    }

    /// <summary>Returns how many CRC values were reset (0 = nothing to do: already patched or no bundle entries).</summary>
    public static int PatchCrc(byte[] data, out byte[] result)
    {
        int i = 0;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) i = 3;
        while (i < data.Length && data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        result = (byte[])data.Clone();
        if (i < data.Length && data[i] == '{') return PatchJson(data, ref result);
        return PatchBinary(result);
    }

    // ------------------------------------------------------------------ JSON (m_ExtraDataString: base64, objects as UTF-16 JSON)
    static readonly Regex ExtraData = new("\"m_ExtraDataString\"\\s*:\\s*\"(?<b64>[^\"]*)\"");

    static int PatchJson(byte[] data, ref byte[] result)
    {
        var text = Encoding.UTF8.GetString(data);
        var m = ExtraData.Match(text);
        if (!m.Success) return 0;
        var raw = m.Groups["b64"].Value;
        bool escSlash = raw.Contains("\\/");
        var bytes = Convert.FromBase64String(escSlash ? raw.Replace("\\/", "/") : raw);
        int n = PatchUtf16Crc(bytes);
        if (n == 0) return 0;
        var b64 = Convert.ToBase64String(bytes);
        if (escSlash) b64 = b64.Replace("/", "\\/");
        var g = m.Groups["b64"];
        text = text[..g.Index] + b64 + text[(g.Index + g.Length)..];
        bool bom = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;
        result = bom ? Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray() : Encoding.UTF8.GetBytes(text);
        return n;
    }

    /// <summary>"m_Crc":123456 → "m_Crc":0      (spaces keep the length, so the entry offsets stay valid).</summary>
    static int PatchUtf16Crc(byte[] b)
    {
        var key = Encoding.Unicode.GetBytes("\"m_Crc\":");
        int n = 0;
        for (int p = b.AsSpan().IndexOf(key); p >= 0;)
        {
            int d = p + key.Length;
            while (d + 1 < b.Length && b[d] == ' ' && b[d + 1] == 0) d += 2;
            int start = d;
            while (d + 1 < b.Length && b[d] >= '0' && b[d] <= '9' && b[d + 1] == 0) d += 2;
            if (d > start && !(d - start == 2 && b[start] == '0'))
            {
                b[start] = (byte)'0';
                for (int k = start + 2; k < d; k += 2) b[k] = (byte)' ';
                n++;
            }
            int next = b.AsSpan(d).IndexOf(key);
            p = next < 0 ? -1 : d + next;
        }
        return n;
    }

    // ------------------------------------------------------------------ binary (Addressables 1.21+ / 2.x, versions 1–3)
    static int PatchBinary(byte[] d)
    {
        if (d.Length < 32) throw new Exception("Not an Addressables catalog");
        int version = BitConverter.ToInt32(d, 4);
        if (version < 1 || version > 3) throw new Exception($"Unsupported catalog.bin version {version}");
        uint keys = U32(d, 8);
        var seenLoc = new HashSet<uint>(); var seenObj = new HashSet<uint>();
        int n = 0;

        void Visit(uint loc)
        {
            if (loc == uint.MaxValue || !seenLoc.Add(loc)) return;
            // primaryKey, internalId, providerId, dependencies, dependencyHash, data, type
            uint deps = U32(d, loc + 12), data = U32(d, loc + 20);
            foreach (var dep in Offsets(d, deps)) Visit(dep);
            if (data == uint.MaxValue) return;
            uint type = U32(d, data), obj = U32(d, data + 4);
            if (obj == uint.MaxValue || !seenObj.Add(obj)) return;
            if (Str(d, U32(d, type + 4), '.', version) != AbroClass) return;
            // hash, bundleName, crc, bundleSize, commonInfo
            if (U32(d, obj + 8) == 0) return;
            BitConverter.TryWriteBytes(d.AsSpan((int)obj + 8, 4), 0u);
            n++;
        }

        var kv = Offsets(d, keys);
        for (int i = 1; i < kv.Length; i += 2)
            foreach (var loc in Offsets(d, kv[i])) Visit(loc);
        return n;
    }

    static uint U32(byte[] d, uint pos)
    {
        if (pos > d.Length - 4) throw new Exception("Damaged catalog (offset out of range)");
        return BitConverter.ToUInt32(d, (int)pos);
    }

    static uint[] Offsets(byte[] d, uint off)
    {
        if (off == uint.MaxValue) return Array.Empty<uint>();
        int size = (int)U32(d, off - 4);
        if (size < 0 || size % 4 != 0 || off + size > d.Length) throw new Exception("Damaged catalog (bad array)");
        var res = new uint[size / 4];
        for (int i = 0; i < res.Length; i++) res[i] = BitConverter.ToUInt32(d, (int)off + i * 4);
        return res;
    }

    static string? Str(byte[] d, uint enc, char sep, int version)
    {
        if (enc == uint.MaxValue) return null;
        bool unicode = (enc & 0x80000000) != 0, dynamic = (enc & 0x40000000) != 0 && sep != '\0';
        uint off = enc & 0x3fffffff;
        if (!dynamic)
        {
            int len = (int)U32(d, off - 4);
            if (len < 0 || off + len > d.Length) throw new Exception("Damaged catalog (bad string)");
            return (unicode ? Encoding.Unicode : Encoding.ASCII).GetString(d, (int)off, len);
        }
        var parts = new List<string>();
        for (int guard = 0; guard < 4096; guard++)
        {
            uint part = U32(d, off), next = U32(d, off + 4);
            parts.Add(Str(d, part, '\0', version) ?? "");
            if (next == uint.MaxValue) break;
            off = next;
        }
        if (version > 1) parts.Reverse();
        return string.Join(sep, parts);
    }
}

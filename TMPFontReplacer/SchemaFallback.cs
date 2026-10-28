using System.Reflection;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace TMPFontReplacer;

/// <summary>
/// Built-in serialization layouts of TMP_FontAsset for every TMP release (1.1 … Unity 6), generated at build time
/// from the original TMP + Unity TextCore sources. Used when the game's scripts can't be read
/// (old IL2CPP that Cpp2IL doesn't support, missing Managed folder, stripped files...).
/// A layout is accepted only if it consumes the asset's bytes exactly.
/// </summary>
public static class SchemaFallback
{
    sealed record Schema(string Name, List<AssetTypeTemplateField> Fields);
    static List<Schema>? cache;

    static List<Schema> Schemas()
    {
        if (cache != null) return cache;
        cache = new List<Schema>();
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("TMPFontReplacer.tmp_schemas.txt")!;
        using var r = new StreamReader(s);
        string? line; string name = ""; List<AssetTypeTemplateField>? roots = null;
        var stack = new List<AssetTypeTemplateField>();
        while ((line = r.ReadLine()) != null)
        {
            if (line.StartsWith("### "))
            {
                if (roots != null) cache.Add(new Schema(name, roots));
                name = line.Substring(4); roots = new(); stack.Clear();
                continue;
            }
            if (line.Length == 0 || roots == null) continue;
            var p = line.Split('\t');
            int depth = int.Parse(p[0]);
            var f = new AssetTypeTemplateField
            {
                Type = p[1], Name = p[2], ValueType = (AssetValueType)int.Parse(p[3]),
                IsArray = p[4] == "1", IsAligned = p[5] == "1", HasValue = p[6] == "1",
                Children = new List<AssetTypeTemplateField>()
            };
            if (depth == 0) roots.Add(f); else stack[depth - 1].Children.Add(f);
            if (stack.Count > depth) stack[depth] = f; else stack.Add(f);
        }
        if (roots != null) cache.Add(new Schema(name, roots));
        return cache;
    }

    public static int Count => Schemas().Count;

    /// <summary>Try every known TMP layout; returns the parsed base field or null.</summary>
    public static AssetTypeValueField? TryParse(AssetsManager am, AssetsFileInstance inst, AssetFileInfo info, out string schemaName)
    {
        schemaName = "";
        if (am.ClassDatabase == null) return null;
        var mbType = am.ClassDatabase.FindAssetClassByID((int)AssetClassID.MonoBehaviour);
        if (mbType == null) return null;
        var baseT = new AssetTypeTemplateField();
        baseT.FromClassDatabase(am.ClassDatabase, mbType, false);

        long start = info.GetAbsoluteByteOffset(inst.file);
        byte[] raw;
        var rd = inst.file.Reader;
        lock (rd) { rd.Position = start; raw = rd.ReadBytes((int)info.ByteSize); }

        AssetTypeValueField? best = null; int bestScore = int.MinValue;
        foreach (var sc in Schemas())
        {
            var t = baseT.Clone();
            t.Children = new List<AssetTypeTemplateField>(baseT.Children);
            foreach (var c in sc.Fields) t.Children.Add(c);
            try
            {
                using var ms = new MemoryStream(raw);
                var r = new AssetsFileReader(ms) { BigEndian = rd.BigEndian };
                var v = t.MakeValue(r, 0, null);
                if (ms.Position != raw.Length) continue;
                int score = Score(v);
                if (score > bestScore) { bestScore = score; best = v; schemaName = sc.Name; }
            }
            catch { }
        }
        if (best != null && bestScore > 0) return best;
        schemaName = "";
        return null;
    }

    /// <summary>Parse with a given template; null unless it consumes the asset's bytes exactly.</summary>
    public static AssetTypeValueField? ParseExact(AssetsFileInstance inst, AssetFileInfo info, AssetTypeTemplateField t)
    {
        var rd = inst.file.Reader;
        byte[] raw;
        lock (rd) { rd.Position = info.GetAbsoluteByteOffset(inst.file); raw = rd.ReadBytes((int)info.ByteSize); }
        using var ms = new MemoryStream(raw);
        var r = new AssetsFileReader(ms) { BigEndian = rd.BigEndian };
        var v = t.MakeValue(r, 0, null);
        return ms.Position == raw.Length ? v : null;
    }

    static bool Pow2(int v) => v == 0 || (v > 0 && (v & (v - 1)) == 0) || v % 64 == 0;
    static bool Printable(string? str) => str != null && str.Length < 256 && str.All(ch => !char.IsControl(ch));

    /// <summary>How plausible the parsed values are (several layouts can consume the same bytes by chance).</summary>
    static int Score(AssetTypeValueField v)
    {
        int s = 0;
        try
        {
            bool modern = !v["m_GlyphTable"].IsDummy;
            if (modern)
            {
                int w = v["m_AtlasWidth"].AsInt, h = v["m_AtlasHeight"].AsInt, pad = v["m_AtlasPadding"].AsInt;
                if (w < 0 || h < 0 || w > 16384 || h > 16384 || pad < 0 || pad > 1024) return -100;
                if (Pow2(w) && Pow2(h)) s += 2;
                if (pad <= 64) s += 1;
                var fi = v["m_FaceInfo"];
                double ps = fi["m_PointSize"].Value?.ValueType == AssetValueType.Float ? fi["m_PointSize"].AsFloat : fi["m_PointSize"].AsInt;
                if (ps > 0 && ps < 2000) s += 3; else s -= 5;
                float lh = fi["m_LineHeight"].AsFloat;
                if (lh > 0 && lh < ps * 6) s += 2; else s -= 2;
                if (Printable(fi["m_FamilyName"].AsString)) s += 1; else s -= 3;
                var glyphs = v["m_GlyphTable"]["Array"].Children;
                var ids = new HashSet<uint>(glyphs.Select(g => g["m_Index"].AsUInt));
                var chars = v["m_CharacterTable"]["Array"].Children;
                if (chars.All(c => c["m_Unicode"].AsUInt < 0x110000)) s += 2; else s -= 5;
                if (chars.Count > 0 && chars.All(c => ids.Contains(c["m_GlyphIndex"].AsUInt))) s += 3;
                if (glyphs.All(g => { var r = g["m_GlyphRect"]; int x = r["m_X"].AsInt, y = r["m_Y"].AsInt, gw = r["m_Width"].AsInt, gh = r["m_Height"].AsInt;
                        return x >= 0 && y >= 0 && gw >= 0 && gh >= 0 && (w == 0 || x + gw <= w) && (h == 0 || y + gh <= h); })) s += 3; else s -= 5;
                var ver = v["m_Version"];
                if (!ver.IsDummy && Printable(ver.AsString) && (ver.AsString == "" || ver.AsString.StartsWith("1."))) s += 2;
                var cs = v["m_CreationSettings"];
                if (!cs.IsDummy && Math.Abs(cs["pointSize"].AsInt - ps) < 1) s += 1;
                var mat = !v["m_Material"].IsDummy ? v["m_Material"] : v["material"];
                if (!mat.IsDummy && mat["m_FileID"].AsInt >= 0 && mat["m_FileID"].AsInt < 10000) s += 1;
            }
            else
            {
                var fi = v["m_fontInfo"];
                if (fi.IsDummy) return -100;
                float aw = fi["AtlasWidth"].AsFloat, ah = fi["AtlasHeight"].AsFloat, ps = fi["PointSize"].AsFloat;
                if (aw < 0 || aw > 16384 || ah < 0 || ah > 16384) return -100;
                if (Pow2((int)aw) && Pow2((int)ah)) s += 2;
                if (ps > 0 && ps < 2000) s += 3; else s -= 5;
                if (Printable(fi["Name"].AsString)) s += 1;
                var gl = v["m_glyphInfoList"]["Array"].Children;
                if (gl.All(g => g["x"].AsFloat >= 0 && g["y"].AsFloat >= 0 && g["x"].AsFloat + g["width"].AsFloat <= aw + 1)) s += 3; else s -= 5;
                if (gl.All(g => g["id"].AsInt >= 0 && g["id"].AsInt < 0x110000)) s += 2;
            }
        }
        catch { return -50; }
        return s;
    }
}

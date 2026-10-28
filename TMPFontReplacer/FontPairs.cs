using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace TMPFontReplacer;

/// <summary>
/// A TMP font = MonoBehaviour (font asset) + Texture2D (atlas) with matching names:
///   "Arial SDF" + "Arial SDF Atlas", "Arial SDF" + "Arial Atlas", "Arial" + "Arial SDF Atlas", or identical names.
/// </summary>
public static class FontPairs
{
    /// <summary>Lower-case letters/digits only ("Arial SDF - Atlas" → "arialsdfatlas").</summary>
    public static string Key(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    static string Cut(string k, string suffix) => k.Length > suffix.Length && k.EndsWith(suffix) ? k[..^suffix.Length] : k;

    static readonly string[] TexSuffixes = { "atlastexture", "atlas", "texture", "tex", "map", "sdf" };

    static string StripTex(string t, out bool stripped)
    {
        stripped = false;
        for (bool again = true; again;)
        {
            again = false;
            var d = t.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            if (d.Length != t.Length && d.Length > 0) { t = d; stripped = again = true; }
            foreach (var suf in TexSuffixes)
                if (t.Length > suf.Length && t.EndsWith(suf)) { t = t[..^suf.Length]; stripped = again = true; break; }
        }
        return t;
    }

    /// <summary>0 = no pair, 2 = "&lt;font&gt; Atlas"-style naming, 1 = identical names (weaker).</summary>
    public static int Match(string fontName, string texName)
    {
        var f = Key(fontName); var t = Key(texName);
        if (f.Length == 0 || t.Length == 0) return 0;
        if (t == f) return 1;
        var fb = Cut(f, "sdf");
        var tb = StripTex(t, out bool stripped);
        if (stripped && (tb == f || tb == fb)) return 2;
        return 0;
    }

    /// <summary>Length of the common start of two names (letters/digits only) — used to pair by a user word.</summary>
    public static int CommonPrefix(string a, string b)
    {
        var x = Key(a); var y = Key(b); int n = 0;
        while (n < x.Length && n < y.Length && x[n] == y[n]) n++;
        return n;
    }

    /// <summary>m_Name read straight from the asset bytes (works without type information).</summary>
    public static string RawName(AssetsFileInstance inst, AssetFileInfo info)
    {
        var r = inst.file.Reader;
        lock (r)
        {
            r.Position = info.GetAbsoluteByteOffset(inst.file);
            if (info.TypeId == (int)AssetClassID.MonoBehaviour)
            {
                int pptr = inst.file.Header.Version >= 14 ? 12 : 8;
                r.Position += pptr + 4 + pptr;
            }
            int len = r.ReadInt32();
            if (len <= 0 || len > 512 || len > info.ByteSize) return "";
            var s = Encoding.UTF8.GetString(r.ReadBytes(len));
            return s.Any(char.IsControl) ? "" : s;
        }
    }

    /// <summary>Best atlas texture for a font name among the given files (pair by name), or null.</summary>
    public static (AssetsFileInstance inst, AssetFileInfo info, string name)? FindAtlas(IEnumerable<AssetsFileInstance> files, string fontName)
    {
        (AssetsFileInstance, AssetFileInfo, string)? best = null; int bestScore = 0;
        foreach (var inst in files)
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Texture2D))
            {
                string n; try { n = RawName(inst, info); } catch { continue; }
                int sc = Match(fontName, n);
                if (sc > bestScore) { best = (inst, info, n); bestScore = sc; }
            }
        return best;
    }
}

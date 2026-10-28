using System.Globalization;
using System.Text;

namespace TMPFontReplacer;

/// <summary>Post-build check: which characters made it into the atlas, which were skipped, plus a rendered preview.</summary>
public static class Report
{
    public const string UkrUpper = "АБВГҐДЕЄЖЗИІЇЙКЛМНОПРСТУФХЦЧШЩЬЮЯ";
    public const string UkrLower = "абвгґдеєжзиіїйклмнопрстуфхцчшщьюя";
    public const string UkrExtra = "ʼ’«»—–…№₴";

    static bool IsBlank(int cp) =>
        cp == 32 || cp == 0xA0 || (cp >= 0x2000 && cp <= 0x200F) || cp == 0x2028 || cp == 0x2029 || cp == 0x202F || cp == 0x205F || cp == 0x3000
        || (cp >= 0x202A && cp <= 0x202E) || (cp >= 0x2060 && cp <= 0x206F) || cp == 0xAD || cp == 0xFEFF || char.GetUnicodeCategory(char.ConvertFromUtf32(cp), 0) == UnicodeCategory.Control;

    public sealed class Result
    {
        public List<int> Included = new(), MissingInTtf = new(), EmptyGlyph = new(), MissingFromRequestFile = new(), MissingFromOriginal = new();
        public List<char> UkrMissing = new();
    }

    public static Result Analyze(TrueTypeFont ttf, AtlasResult a, IEnumerable<int> wanted, List<int> chars, IEnumerable<int>? requestFile, IEnumerable<int> original)
    {
        var r = new Result();
        var byGlyph = a.Glyphs.ToDictionary(g => g.Index);
        var set = new HashSet<int>(chars);
        foreach (var c in wanted.Distinct().OrderBy(x => x))
        {
            if (!set.Contains(c)) { if (!IsBlank(c)) r.MissingInTtf.Add(c); continue; }
            var g = byGlyph[ttf.GlyphOf(c)];
            if (g.RectW == 0 && !IsBlank(c)) r.EmptyGlyph.Add(c); else r.Included.Add(c);
        }
        foreach (var ch in UkrUpper + UkrLower + UkrExtra)
            if (!set.Contains(ch) || r.EmptyGlyph.Contains(ch)) r.UkrMissing.Add(ch);
        var orig = new HashSet<int>(original);
        r.MissingFromOriginal = r.MissingInTtf.Where(orig.Contains).ToList();
        if (requestFile != null)
            r.MissingFromRequestFile = requestFile.Where(c => !IsBlank(c) && (!set.Contains(c) || r.EmptyGlyph.Contains(c))).Distinct().OrderBy(x => x).ToList();
        return r;
    }

    public static string Codes(IEnumerable<int> cps, int max = 200)
    {
        var l = cps.ToList();
        var sb = new StringBuilder();
        foreach (var c in l.Take(max)) sb.Append(Printable(c) + $"(U+{c:X4}) ");
        if (l.Count > max) sb.Append($"… ще {l.Count - max}");
        return sb.ToString().TrimEnd();
    }

    static string Printable(int c) => IsBlank(c) || (c >= 0x300 && c <= 0x36F) ? "◌" : char.ConvertFromUtf32(c);

    public static void Print(Result r, Action<string> log)
    {
        log("");
        log("──────── РЕЗУЛЬТАТ СТВОРЕННЯ ШРИФТУ ────────");
        log($"  У атласі: {r.Included.Count} символів");
        if (r.UkrMissing.Count == 0) log("  Українська абетка (33+33 літери, ʼ ’ « » — – … № ₴): УСЕ Є ✓");
        else log($"  УВАГА! Українських символів бракує: {string.Join(" ", r.UkrMissing)}");
        if (r.MissingInTtf.Count == 0) log("  Пропущених символів немає ✓");
        else
        {
            log($"  Пропущено (цих символів немає в новому TTF): {r.MissingInTtf.Count}");
            if (r.MissingFromOriginal.Count > 0)
                log($"    • були в оригінальному шрифті гри (гра візьме їх із fallback-шрифту або покаже □): {Codes(r.MissingFromOriginal, 60)}");
            var other = r.MissingInTtf.Except(r.MissingFromOriginal).ToList();
            if (other.Count > 0) log($"    • із доданого набору / --chars: {Codes(other, 60)}");
        }
        if (r.EmptyGlyph.Count > 0) log($"  Порожні гліфи (символ є в TTF, але без контуру): {Codes(r.EmptyGlyph, 60)}");
        if (r.MissingFromRequestFile.Count > 0) log($"  З тексту --chars-file НЕ покрито: {Codes(r.MissingFromRequestFile, 60)}");
        log("────────────────────────────────────────────");
    }

    public static void WriteText(string path, Result r, string fontName, string ttfName, AtlasResult a)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Шрифт: {fontName}  ←  {ttfName}");
        sb.AppendLine($"Атлас {a.Width}x{a.Height}, розмір семплінгу {a.PointSize}, padding {a.Padding}");
        sb.AppendLine();
        sb.AppendLine($"У атласі ({r.Included.Count}):");
        sb.AppendLine(string.Concat(r.Included.Select(Printable)));
        sb.AppendLine();
        sb.AppendLine(r.UkrMissing.Count == 0 ? "Українська абетка: повна" : "Українських символів бракує: " + string.Join(" ", r.UkrMissing));
        sb.AppendLine();
        sb.AppendLine($"Пропущено — немає в TTF ({r.MissingInTtf.Count}):");
        foreach (var c in r.MissingInTtf) sb.AppendLine($"  U+{c:X4}  {Printable(c)}");
        if (r.EmptyGlyph.Count > 0) { sb.AppendLine($"Порожні гліфи ({r.EmptyGlyph.Count}):"); foreach (var c in r.EmptyGlyph) sb.AppendLine($"  U+{c:X4}  {Printable(c)}"); }
        if (r.MissingFromRequestFile.Count > 0) { sb.AppendLine($"З тексту --chars-file не покрито ({r.MissingFromRequestFile.Count}):"); foreach (var c in r.MissingFromRequestFile) sb.AppendLine($"  U+{c:X4}  {Printable(c)}"); }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    // ------------------------------------------------------------------ rendered preview (drawn from the generated atlas, the way TMP draws it)
    sealed class Canvas
    {
        public readonly int W, H; public readonly byte[] Px; // RGB
        public Canvas(int w, int h) { W = w; H = h; Px = new byte[w * h * 3]; Array.Fill(Px, (byte)255); }
        public void Blend(int x, int y, float a, (byte r, byte g, byte b) c)
        {
            if (x < 0 || y < 0 || x >= W || y >= H || a <= 0) return;
            int i = (y * W + x) * 3;
            Px[i] = (byte)(Px[i] + (c.r - Px[i]) * a); Px[i + 1] = (byte)(Px[i + 1] + (c.g - Px[i + 1]) * a); Px[i + 2] = (byte)(Px[i + 2] + (c.b - Px[i + 2]) * a);
        }
        public void Rect(int x0, int y0, int w, int h, (byte, byte, byte) c, bool fill)
        {
            for (int y = y0; y < y0 + h; y++) for (int x = x0; x < x0 + w; x++)
                if (fill || y == y0 || y == y0 + h - 1 || x == x0 || x == x0 + w - 1) Blend(x, y, 1, c);
        }
    }

    sealed class Painter
    {
        readonly AtlasResult a; readonly TrueTypeFont f; readonly Dictionary<int, GlyphResult> g; readonly bool sdf; readonly float gs; readonly double asc;
        public Painter(AtlasResult a, TrueTypeFont f, bool sdf, float gs)
        {
            this.a = a; this.f = f; this.sdf = sdf; this.gs = gs;
            g = a.Glyphs.ToDictionary(x => x.Index);
            asc = f.Ascender * (double)a.PointSize / f.UnitsPerEm;
        }
        public bool Has(int cp) => f.HasChar(cp) && g.ContainsKey(f.GlyphOf(cp));
        double Sample(double x, double y)
        {
            int x0 = (int)Math.Floor(x - 0.5), y0 = (int)Math.Floor(y - 0.5);
            double fx = x - 0.5 - x0, fy = y - 0.5 - y0;
            double V(int xx, int yy) => xx < 0 || yy < 0 || xx >= a.Width || yy >= a.Height ? 0 : a.Alpha[yy * a.Width + xx] / 255.0;
            return V(x0, y0) * (1 - fx) * (1 - fy) + V(x0 + 1, y0) * fx * (1 - fy) + V(x0, y0 + 1) * (1 - fx) * fy + V(x0 + 1, y0 + 1) * fx * fy;
        }
        /// <summary>Draw one glyph with its baseline at y (canvas coords, y down). Returns the advance.</summary>
        public double Glyph(Canvas c, int cp, double x, double baseline, double size, (byte, byte, byte) col)
        {
            if (!Has(cp)) return 0;
            var gl = g[f.GlyphOf(cp)];
            double k = size / a.PointSize;
            if (gl.RectW > 0)
            {
                int pad = a.Padding;
                double qx0 = x + (gl.BearingX - pad) * k, qy0 = baseline - (gl.BearingY + pad) * k;
                double qw = (gl.RectW + 2 * pad) * k, qh = (gl.RectH + 2 * pad) * k;
                for (int py = (int)Math.Floor(qy0); py < qy0 + qh; py++)
                    for (int px = (int)Math.Floor(qx0); px < qx0 + qw; px++)
                    {
                        double u = (px + 0.5 - qx0) / k, v = (py + 0.5 - qy0) / k;           // texels inside padded box (v from top)
                        double ax = gl.RectX - pad + u, ay = gl.RectY + gl.RectH + pad - v;   // atlas coords (bottom-up)
                        double d = Sample(ax, ay);
                        double cov = sdf ? Math.Clamp((d - 0.5) * 2 * gs * k + 0.5, 0, 1) : d;
                        c.Blend(px, py, (float)cov, col);
                    }
            }
            return gl.Advance * k;
        }
        public double Text(Canvas c, string s, double x, double baseline, double size, (byte, byte, byte) col)
        {
            for (int i = 0; i < s.Length; i++)
            {
                int cp = char.ConvertToUtf32(s, i); if (char.IsHighSurrogate(s[i])) i++;
                x += Has(cp) ? Glyph(c, cp, x, baseline, size, col) : size * 0.5;
            }
            return x;
        }
        public double Ascent(double size) => asc * size / a.PointSize;
    }

    public static void WritePng(string path, Result r, TrueTypeFont f, AtlasResult a, bool sdf, float gs, string title)
    {
        var p = new Painter(a, f, sdf, gs);
        (byte, byte, byte) black = (20, 20, 20), gray = (130, 130, 130), red = (200, 30, 30), green = (20, 130, 50);
        const int cell = 64, cols = 24, glyphSize = 36;
        int incRows = (r.Included.Count + cols - 1) / cols;
        var bad = r.MissingInTtf.Concat(r.EmptyGlyph).ToList();
        int badRows = (bad.Count + cols - 1) / cols;
        int W = cols * cell + 40;
        int H = 40 + 200 + 40 + incRows * cell + (bad.Count > 0 ? 60 + badRows * cell : 40) + 20;
        var c = new Canvas(W, H);
        double y = 50;
        p.Text(c, title, 20, y, 26, black); y += 44;
        p.Text(c, "Їжак ґудзик, Щастя: «№ 42» ₴ — з’їсти, її, ЇЖАК", 20, y, 30, black); y += 44;
        p.Text(c, "Найняти Асистент, кваліфікованого(-у) у сфері: Майстерність", 20, y, 22, black); y += 34;
        p.Text(c, "The quick brown fox jumps over the lazy dog 0123456789", 20, y, 22, black); y += 34;
        string uk = r.UkrMissing.Count == 0 ? "Українська абетка: повна ✓" : "Бракує українських: " + string.Join(" ", r.UkrMissing);
        p.Text(c, uk.Replace("✓", "+"), 20, y, 20, r.UkrMissing.Count == 0 ? green : red); y += 30;
        p.Text(c, $"У атласі: {r.Included.Count}    Пропущено: {r.MissingInTtf.Count}    Порожні: {r.EmptyGlyph.Count}", 20, y, 20, bad.Count == 0 ? green : red);
        y += 30;
        int top = (int)y;
        for (int i = 0; i < r.Included.Count; i++)
        {
            int cx = 20 + (i % cols) * cell, cy = top + (i / cols) * cell;
            c.Rect(cx, cy, cell - 2, cell - 2, (225, 225, 225), false);
            int cp = r.Included[i];
            p.Glyph(c, cp, cx + 12, cy + 12 + p.Ascent(glyphSize) * 0.78, glyphSize, black);
            p.Text(c, cp.ToString("X4"), cx + 4, cy + cell - 6, 11, gray);
        }
        y = top + incRows * cell + 30;
        if (bad.Count > 0)
        {
            p.Text(c, "ПРОПУЩЕНІ СИМВОЛИ (немає в TTF або порожній гліф):", 20, y, 20, red); y += 14;
            int t2 = (int)y;
            for (int i = 0; i < bad.Count; i++)
            {
                int cx = 20 + (i % cols) * cell, cy = t2 + (i / cols) * cell;
                c.Rect(cx, cy, cell - 2, cell - 2, red, false);
                c.Rect(cx + 20, cy + 12, 20, 26, (240, 190, 190), true);
                p.Text(c, "U+" + bad[i].ToString("X4"), cx + 4, cy + cell - 8, 11, red);
            }
        }
        Png.WriteRgb(path, c.Px, c.W, c.H);
    }
}

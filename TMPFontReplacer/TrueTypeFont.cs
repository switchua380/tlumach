using System.Text;

namespace TMPFontReplacer;

/// <summary>Minimal TrueType (glyf) font reader: metrics, cmap, outlines, kerning.</summary>
public sealed class TrueTypeFont
{
    readonly byte[] d;
    readonly Dictionary<string, (int off, int len)> tables = new();
    public int UnitsPerEm, NumGlyphs, Ascender, Descender, LineGap;
    public int CapHeight, XHeight, StrikeoutPos, StrikeoutSize, UnderlinePos, UnderlineThickness;
    public string FamilyName = "", StyleName = "";
    int indexToLocFormat, numHMetrics;
    ushort[] advances = Array.Empty<ushort>();
    readonly Dictionary<int, int> cmap = new();

    public TrueTypeFont(byte[] data)
    {
        d = data;
        int off = 0;
        uint tag0 = U32(0);
        if (tag0 == 0x74746366) off = (int)U32(12); // 'ttcf' -> first font
        uint sfnt = U32(off);
        if (sfnt == 0x4F54544F) throw new Exception("Це OpenType-CFF шрифт (.otf). Потрібен TrueType (.ttf) з контурами glyf.");
        int n = U16(off + 4);
        for (int i = 0; i < n; i++)
        {
            int r = off + 12 + i * 16;
            string tag = Encoding.ASCII.GetString(d, r, 4);
            tables[tag] = ((int)U32(r + 8), (int)U32(r + 12));
        }
        if (!tables.ContainsKey("glyf")) throw new Exception("У шрифті немає таблиці glyf (потрібен TrueType .ttf).");
        var head = tables["head"].off;
        UnitsPerEm = U16(head + 18);
        indexToLocFormat = S16(head + 50);
        NumGlyphs = U16(tables["maxp"].off + 4);
        var hhea = tables["hhea"].off;
        Ascender = S16(hhea + 4); Descender = S16(hhea + 6); LineGap = S16(hhea + 8);
        numHMetrics = U16(hhea + 34);
        var hmtx = tables["hmtx"].off;
        advances = new ushort[NumGlyphs];
        for (int g = 0; g < NumGlyphs; g++)
            advances[g] = (ushort)U16(hmtx + 4 * Math.Min(g, numHMetrics - 1));
        if (tables.TryGetValue("OS/2", out var os2))
        {
            int o = os2.off; int ver = U16(o);
            StrikeoutSize = S16(o + 26); StrikeoutPos = S16(o + 28);
            if (ver >= 2 && os2.len >= 90) { XHeight = S16(o + 86); CapHeight = S16(o + 88); }
        }
        if (tables.TryGetValue("post", out var post)) { UnderlinePos = S16(post.off + 8); UnderlineThickness = S16(post.off + 10); }
        if (UnderlineThickness <= 0) UnderlineThickness = UnitsPerEm / 20;
        ReadCmap();
        ReadNames();
    }

    // ---------- binary helpers
    int U16(int o) => (d[o] << 8) | d[o + 1];
    int S16(int o) => (short)((d[o] << 8) | d[o + 1]);
    uint U32(int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

    void ReadCmap()
    {
        int c = tables["cmap"].off;
        int n = U16(c + 2);
        int best = -1, bestScore = -1;
        for (int i = 0; i < n; i++)
        {
            int pid = U16(c + 4 + i * 8), eid = U16(c + 6 + i * 8);
            int so = c + (int)U32(c + 8 + i * 8);
            int fmt = U16(so);
            int score = (pid == 3 && eid == 10 && fmt == 12) ? 4 : (pid == 0 && fmt == 12) ? 3 : (pid == 3 && eid == 1 && fmt == 4) ? 2 : (pid == 0 && fmt == 4) ? 1 : -1;
            if (score > bestScore) { bestScore = score; best = so; }
        }
        if (best < 0) throw new Exception("Не знайдено Unicode cmap у шрифті.");
        int f = U16(best);
        if (f == 4)
        {
            int segX2 = U16(best + 6);
            int ends = best + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
            for (int s = 0; s < segX2 / 2; s++)
            {
                int end = U16(ends + s * 2), start = U16(starts + s * 2), delta = S16(deltas + s * 2), ro = U16(ranges + s * 2);
                for (int ch = start; ch <= end && ch != 0xFFFF; ch++)
                {
                    int g;
                    if (ro == 0) g = (ch + delta) & 0xFFFF;
                    else
                    {
                        int gi = ranges + s * 2 + ro + (ch - start) * 2;
                        g = U16(gi); if (g != 0) g = (g + delta) & 0xFFFF;
                    }
                    if (g != 0) cmap[ch] = g;
                }
            }
        }
        else if (f == 12)
        {
            int ng = (int)U32(best + 12);
            for (int i = 0; i < ng; i++)
            {
                int r = best + 16 + i * 12;
                uint s = U32(r), e = U32(r + 4), g0 = U32(r + 8);
                for (uint ch = s; ch <= e; ch++) cmap[(int)ch] = (int)(g0 + ch - s);
            }
        }
    }

    void ReadNames()
    {
        if (!tables.TryGetValue("name", out var nt)) return;
        int o = nt.off, count = U16(o + 2), strOff = o + U16(o + 4);
        string[] found = new string[18];
        for (int i = 0; i < count; i++)
        {
            int r = o + 6 + i * 12;
            int pid = U16(r), eid = U16(r + 2), lang = U16(r + 4), nid = U16(r + 6), len = U16(r + 8), so = U16(r + 10);
            if (nid >= found.Length) continue;
            string s;
            if (pid == 3 && (eid == 1 || eid == 10)) { if (lang != 0x409 && found[nid] != null) continue; s = Encoding.BigEndianUnicode.GetString(d, strOff + so, len); }
            else if (pid == 1 && eid == 0) { if (found[nid] != null) continue; s = Encoding.Latin1.GetString(d, strOff + so, len); }
            else continue;
            found[nid] = s;
        }
        FamilyName = found[16] ?? found[1] ?? "Font";
        StyleName = found[17] ?? found[2] ?? "Regular";
    }

    public bool HasChar(int cp) => cmap.ContainsKey(cp);
    public int GlyphOf(int cp) => cmap.TryGetValue(cp, out var g) ? g : 0;
    public IEnumerable<int> AllCodepoints => cmap.Keys;
    public int Advance(int g) => g < advances.Length ? advances[g] : 0;

    // ---------- outlines
    public struct Pt { public double X, Y; public bool On; public Pt(double x, double y, bool on) { X = x; Y = y; On = on; } }

    /// <summary>Contours of a glyph in font units (quadratic TrueType points).</summary>
    public List<List<Pt>> GetContours(int g, int depth = 0)
    {
        var res = new List<List<Pt>>();
        if (g < 0 || g >= NumGlyphs || depth > 8) return res;
        int loca = tables["loca"].off, glyf = tables["glyf"].off;
        int start, end;
        if (indexToLocFormat == 0) { start = U16(loca + g * 2) * 2; end = U16(loca + g * 2 + 2) * 2; }
        else { start = (int)U32(loca + g * 4); end = (int)U32(loca + g * 4 + 4); }
        if (end <= start) return res;
        int p = glyf + start;
        int nc = S16(p);
        if (nc >= 0)
        {
            int[] endPts = new int[nc];
            for (int i = 0; i < nc; i++) endPts[i] = U16(p + 10 + i * 2);
            int nPts = nc > 0 ? endPts[nc - 1] + 1 : 0;
            int q = p + 10 + nc * 2;
            int il = U16(q); q += 2 + il;
            var flags = new byte[nPts];
            for (int i = 0; i < nPts;)
            {
                byte f = d[q++]; flags[i++] = f;
                if ((f & 8) != 0) { int rep = d[q++]; for (int k = 0; k < rep && i < nPts; k++) flags[i++] = f; }
            }
            var xs = new int[nPts]; var ys = new int[nPts];
            int v = 0;
            for (int i = 0; i < nPts; i++)
            {
                byte f = flags[i];
                if ((f & 2) != 0) { int dx = d[q++]; v += (f & 16) != 0 ? dx : -dx; }
                else if ((f & 16) == 0) { v += S16(q); q += 2; }
                xs[i] = v;
            }
            v = 0;
            for (int i = 0; i < nPts; i++)
            {
                byte f = flags[i];
                if ((f & 4) != 0) { int dy = d[q++]; v += (f & 32) != 0 ? dy : -dy; }
                else if ((f & 32) == 0) { v += S16(q); q += 2; }
                ys[i] = v;
            }
            int s0 = 0;
            for (int c = 0; c < nc; c++)
            {
                var cont = new List<Pt>();
                for (int i = s0; i <= endPts[c]; i++) cont.Add(new Pt(xs[i], ys[i], (flags[i] & 1) != 0));
                s0 = endPts[c] + 1;
                if (cont.Count > 0) res.Add(cont);
            }
        }
        else
        {
            int q = p + 10;
            while (true)
            {
                int flags = U16(q), gi = U16(q + 2); q += 4;
                double dx, dy;
                if ((flags & 1) != 0) { dx = S16(q); dy = S16(q + 2); q += 4; }
                else { dx = (sbyte)d[q]; dy = (sbyte)d[q + 1]; q += 2; }
                if ((flags & 2) == 0) { dx = 0; dy = 0; } // point matching: not supported, treat as no offset
                double a = 1, b = 0, c2 = 0, dd = 1;
                if ((flags & 8) != 0) { a = dd = F2Dot14(q); q += 2; }
                else if ((flags & 0x40) != 0) { a = F2Dot14(q); dd = F2Dot14(q + 2); q += 4; }
                else if ((flags & 0x80) != 0) { a = F2Dot14(q); b = F2Dot14(q + 2); c2 = F2Dot14(q + 4); dd = F2Dot14(q + 6); q += 8; }
                foreach (var cont in GetContours(gi, depth + 1))
                {
                    var nc2 = new List<Pt>(cont.Count);
                    foreach (var pt in cont) nc2.Add(new Pt(a * pt.X + c2 * pt.Y + dx, b * pt.X + dd * pt.Y + dy, pt.On));
                    res.Add(nc2);
                }
                if ((flags & 0x20) == 0) break;
            }
        }
        return res;
    }

    double F2Dot14(int o) => S16(o) / 16384.0;

    /// <summary>Flatten outline to closed polylines, scaled to pixels.</summary>
    public List<(double x0, double y0, double x1, double y1)> GetSegments(int g, double scale, out double xMin, out double yMin, out double xMax, out double yMax)
    {
        var segs = new List<(double, double, double, double)>();
        xMin = yMin = double.MaxValue; xMax = yMax = double.MinValue;
        foreach (var cont in GetContours(g))
        {
            int n = cont.Count; if (n < 2) continue;
            // expand implied on-curve points
            var pts = new List<Pt>();
            for (int i = 0; i < n; i++)
            {
                var a = cont[i]; var b = cont[(i + 1) % n];
                pts.Add(a);
                if (!a.On && !b.On) pts.Add(new Pt((a.X + b.X) / 2, (a.Y + b.Y) / 2, true));
            }
            int startIdx = pts.FindIndex(t => t.On);
            if (startIdx < 0) { pts.Insert(0, new Pt((pts[0].X + pts[^1].X) / 2, (pts[0].Y + pts[^1].Y) / 2, true)); startIdx = 0; }
            var seq = new List<Pt>();
            for (int i = 0; i < pts.Count; i++) seq.Add(pts[(startIdx + i) % pts.Count]);
            seq.Add(seq[0]);
            double cx = seq[0].X * scale, cy = seq[0].Y * scale;
            int k = 1;
            while (k < seq.Count)
            {
                var pnt = seq[k];
                if (pnt.On)
                {
                    double nx = pnt.X * scale, ny = pnt.Y * scale;
                    segs.Add((cx, cy, nx, ny)); cx = nx; cy = ny; k++;
                }
                else
                {
                    var e = seq[k + 1];
                    double qx = pnt.X * scale, qy = pnt.Y * scale, ex = e.X * scale, ey = e.Y * scale;
                    double len = Math.Abs(qx - cx) + Math.Abs(qy - cy) + Math.Abs(ex - qx) + Math.Abs(ey - qy);
                    int steps = Math.Clamp((int)Math.Ceiling(len / 1.5), 2, 32);
                    double px = cx, py = cy;
                    for (int s = 1; s <= steps; s++)
                    {
                        double t = (double)s / steps, mt = 1 - t;
                        double x = mt * mt * cx + 2 * mt * t * qx + t * t * ex;
                        double y = mt * mt * cy + 2 * mt * t * qy + t * t * ey;
                        segs.Add((px, py, x, y)); px = x; py = y;
                    }
                    cx = ex; cy = ey; k += 2;
                }
            }
        }
        foreach (var s in segs)
        {
            xMin = Math.Min(xMin, Math.Min(s.Item1, s.Item3)); xMax = Math.Max(xMax, Math.Max(s.Item1, s.Item3));
            yMin = Math.Min(yMin, Math.Min(s.Item2, s.Item4)); yMax = Math.Max(yMax, Math.Max(s.Item2, s.Item4));
        }
        if (segs.Count == 0) { xMin = yMin = xMax = yMax = 0; }
        return segs;
    }

    // ---------- kerning (GPOS PairPos + legacy kern) -> (left glyph, right glyph) => value in font units
    public Dictionary<(int, int), int> ReadKerning(HashSet<int> glyphs)
    {
        var res = new Dictionary<(int, int), int>();
        try { ReadGposKerning(glyphs, res); } catch { }
        if (res.Count == 0) { try { ReadKernTable(glyphs, res); } catch { } }
        return res;
    }

    void ReadKernTable(HashSet<int> glyphs, Dictionary<(int, int), int> res)
    {
        if (!tables.TryGetValue("kern", out var kt)) return;
        int o = kt.off; int ver = U16(o); if (ver != 0) return;
        int n = U16(o + 2); int p = o + 4;
        for (int t = 0; t < n; t++)
        {
            int len = U16(p + 2), cov = U16(p + 4);
            if ((cov >> 8) == 0 && (cov & 1) != 0)
            {
                int np = U16(p + 6);
                for (int i = 0; i < np; i++)
                {
                    int r = p + 14 + i * 6;
                    int l = U16(r), rr = U16(r + 2), v = S16(r + 4);
                    if (v != 0 && glyphs.Contains(l) && glyphs.Contains(rr)) res[(l, rr)] = v;
                }
            }
            p += len;
        }
    }

    void ReadGposKerning(HashSet<int> glyphs, Dictionary<(int, int), int> res)
    {
        if (!tables.TryGetValue("GPOS", out var gt)) return;
        int g = gt.off;
        int featList = g + U16(g + 6), lookupList = g + U16(g + 8);
        var lookups = new HashSet<int>();
        int fc = U16(featList);
        for (int i = 0; i < fc; i++)
        {
            string tag = Encoding.ASCII.GetString(d, featList + 2 + i * 6, 4);
            if (tag != "kern") continue;
            int fo = featList + U16(featList + 2 + i * 6 + 4);
            int lc = U16(fo + 2);
            for (int k = 0; k < lc; k++) lookups.Add(U16(fo + 4 + k * 2));
        }
        foreach (int li in lookups)
        {
            int lo = lookupList + U16(lookupList + 2 + li * 2);
            int type = U16(lo), sc = U16(lo + 4);
            for (int s = 0; s < sc; s++)
            {
                int st = lo + U16(lo + 6 + s * 2);
                int stype = type;
                if (type == 9) { stype = U16(st + 2); st = st + (int)U32(st + 4); }
                if (stype != 2) continue;
                ReadPairPos(st, glyphs, res);
            }
        }
    }

    static int ValueSize(int vf) { int n = 0; for (int b = 0; b < 8; b++) if ((vf & (1 << b)) != 0) n++; return n * 2; }
    int XAdvOf(int o, int vf)
    {
        // order: XPlacement(1), YPlacement(2), XAdvance(4), ...
        if ((vf & 4) == 0) return 0;
        int skip = ((vf & 1) != 0 ? 2 : 0) + ((vf & 2) != 0 ? 2 : 0);
        return S16(o + skip);
    }

    List<int> Coverage(int o)
    {
        var list = new List<int>();
        int fmt = U16(o);
        if (fmt == 1) { int n = U16(o + 2); for (int i = 0; i < n; i++) list.Add(U16(o + 4 + i * 2)); }
        else if (fmt == 2) { int n = U16(o + 2); for (int i = 0; i < n; i++) { int r = o + 4 + i * 6; int s = U16(r), e = U16(r + 2); for (int x = s; x <= e; x++) list.Add(x); } }
        return list;
    }

    int ClassOf(int o, int glyph)
    {
        if (o == 0) return 0;
        int fmt = U16(o);
        if (fmt == 1) { int sg = U16(o + 2), n = U16(o + 4); if (glyph >= sg && glyph < sg + n) return U16(o + 6 + (glyph - sg) * 2); return 0; }
        if (fmt == 2) { int n = U16(o + 2); for (int i = 0; i < n; i++) { int r = o + 4 + i * 6; if (glyph >= U16(r) && glyph <= U16(r + 2)) return U16(r + 4); } }
        return 0;
    }

    void ReadPairPos(int st, HashSet<int> glyphs, Dictionary<(int, int), int> res)
    {
        int fmt = U16(st);
        var cov = Coverage(st + U16(st + 2));
        int vf1 = U16(st + 4), vf2 = U16(st + 6);
        int s1 = ValueSize(vf1), s2 = ValueSize(vf2);
        if (fmt == 1)
        {
            int psc = U16(st + 8);
            for (int i = 0; i < psc && i < cov.Count; i++)
            {
                int first = cov[i]; if (!glyphs.Contains(first)) continue;
                int ps = st + U16(st + 10 + i * 2);
                int pvc = U16(ps);
                for (int k = 0; k < pvc; k++)
                {
                    int r = ps + 2 + k * (2 + s1 + s2);
                    int second = U16(r);
                    if (!glyphs.Contains(second)) continue;
                    int v = XAdvOf(r + 2, vf1);
                    if (v != 0 && !res.ContainsKey((first, second))) res[(first, second)] = v;
                }
            }
        }
        else if (fmt == 2)
        {
            int cd1 = st + U16(st + 8), cd2 = st + U16(st + 10);
            int c1c = U16(st + 12), c2c = U16(st + 14);
            var covSet = new HashSet<int>(cov);
            var lefts = glyphs.Where(covSet.Contains).ToList();
            var rights = glyphs.ToList();
            var rcls = rights.ToDictionary(x => x, x => ClassOf(cd2, x));
            foreach (var l in lefts)
            {
                int c1 = ClassOf(cd1, l); if (c1 >= c1c) continue;
                foreach (var r in rights)
                {
                    int c2 = rcls[r]; if (c2 >= c2c) continue;
                    int rec = st + 16 + (c1 * c2c + c2) * (s1 + s2);
                    int v = XAdvOf(rec, vf1);
                    if (v != 0 && !res.ContainsKey((l, r))) res[(l, r)] = v;
                }
            }
        }
    }
}

namespace TMPFontReplacer;

public sealed class GlyphResult
{
    public int Index;            // glyph id in the TTF
    public float Width, Height, BearingX, BearingY, Advance;   // metrics in sampling pixels
    public int RectX, RectY, RectW, RectH;                     // glyph rect in atlas (origin bottom-left)
    public List<(double x0, double y0, double x1, double y1)> Segs = new();
    public int OrigX, OrigY;    // integer bitmap origin (bearingX, bearingY - height)
}

public sealed class AtlasResult
{
    public int PointSize, Padding, Width, Height;
    public List<GlyphResult> Glyphs = new();
    public byte[] Alpha = Array.Empty<byte>(); // row 0 = bottom (Unity layout)
    public List<(int x, int y, int w, int h)> UsedRects = new();
    public List<(int x, int y, int w, int h)> FreeRects = new();
}

public static class AtlasBuilder
{
    /// <summary>Measure glyphs at a sampling size and try to pack them; null if they don't fit.</summary>
    public static AtlasResult? TryLayout(TrueTypeFont font, IReadOnlyList<int> glyphIds, int pointSize, int padding, int width, int height)
    {
        double scale = (double)pointSize / font.UnitsPerEm;
        var res = new AtlasResult { PointSize = pointSize, Padding = padding, Width = width, Height = height };
        foreach (var gid in glyphIds)
        {
            var g = new GlyphResult { Index = gid, Advance = (float)Math.Round(font.Advance(gid) * scale, 2) };
            var segs = font.GetSegments(gid, scale, out var x0, out var y0, out var x1, out var y1);
            if (segs.Count > 0 && x1 > x0 && y1 > y0)
            {
                int ix0 = (int)Math.Floor(x0), iy0 = (int)Math.Floor(y0), ix1 = (int)Math.Ceiling(x1), iy1 = (int)Math.Ceiling(y1);
                g.Width = ix1 - ix0; g.Height = iy1 - iy0; g.BearingX = ix0; g.BearingY = iy1;
                g.OrigX = ix0; g.OrigY = iy0; g.Segs = segs;
                g.RectW = ix1 - ix0; g.RectH = iy1 - iy0;
            }
            res.Glyphs.Add(g);
        }
        // MaxRects packing (best short side fit) of padded boxes, largest first
        var order = res.Glyphs.Where(g => g.RectW > 0).OrderByDescending(g => Math.Max(g.RectW, g.RectH)).ThenByDescending(g => g.RectW * g.RectH).ToList();
        var free = new List<(int x, int y, int w, int h)> { (0, 0, width, height) };
        foreach (var g in order)
        {
            int bw = g.RectW + 2 * padding, bh = g.RectH + 2 * padding;
            int bestIdx = -1, bestShort = int.MaxValue, bestLong = int.MaxValue;
            for (int i = 0; i < free.Count; i++)
            {
                var f = free[i];
                if (f.w >= bw && f.h >= bh)
                {
                    int sh = Math.Min(f.w - bw, f.h - bh), lg = Math.Max(f.w - bw, f.h - bh);
                    if (sh < bestShort || (sh == bestShort && lg < bestLong)) { bestShort = sh; bestLong = lg; bestIdx = i; }
                }
            }
            if (bestIdx < 0) return null;
            var place = (x: free[bestIdx].x, y: free[bestIdx].y, w: bw, h: bh);
            g.RectX = place.x + padding; g.RectY = place.y + padding;
            res.UsedRects.Add(place);
            // split free rects intersecting the placed one
            var next = new List<(int x, int y, int w, int h)>(free.Count + 4);
            foreach (var f in free)
            {
                if (place.x >= f.x + f.w || place.x + place.w <= f.x || place.y >= f.y + f.h || place.y + place.h <= f.y) { next.Add(f); continue; }
                if (place.x > f.x) next.Add((f.x, f.y, place.x - f.x, f.h));
                if (place.x + place.w < f.x + f.w) next.Add((place.x + place.w, f.y, f.x + f.w - place.x - place.w, f.h));
                if (place.y > f.y) next.Add((f.x, f.y, f.w, place.y - f.y));
                if (place.y + place.h < f.y + f.h) next.Add((f.x, place.y + place.h, f.w, f.y + f.h - place.y - place.h));
            }
            // prune contained rects
            free = new List<(int x, int y, int w, int h)>(next.Count);
            for (int i = 0; i < next.Count; i++)
            {
                var a = next[i]; bool contained = false;
                for (int j = 0; j < next.Count && !contained; j++)
                {
                    if (i == j) continue;
                    var b = next[j];
                    if (a.x >= b.x && a.y >= b.y && a.x + a.w <= b.x + b.w && a.y + a.h <= b.y + b.h && (a != b || j < i)) contained = true;
                }
                if (!contained) free.Add(a);
            }
        }
        res.FreeRects.AddRange(free);
        return res;
    }

    /// <summary>Largest sampling size in [min,max] whose layout fits.</summary>
    public static AtlasResult? FitLargest(TrueTypeFont font, IReadOnlyList<int> glyphIds, int minSize, int maxSize, int padding, int width, int height)
    {
        AtlasResult? best = null;
        int lo = minSize, hi = maxSize;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var r = TryLayout(font, glyphIds, mid, padding, width, height);
            if (r != null) { best = r; lo = mid + 1; } else hi = mid - 1;
        }
        return best;
    }

    /// <summary>Render SDF (or plain coverage) for every glyph into the atlas.</summary>
    public static void Render(AtlasResult a, bool sdf, float gradientScale)
    {
        var img = new byte[a.Width * a.Height];
        int pad = a.Padding;
        double spread = 2.0 * gradientScale;   // Unity: value = 0.5 + distance / (2 * _GradientScale)
        Parallel.ForEach(a.Glyphs.Where(g => g.RectW > 0), g =>
        {
            var segs = g.Segs.ToArray();
            int bw = g.RectW + 2 * pad, bh = g.RectH + 2 * pad;
            int ax0 = g.RectX - pad, ay0 = g.RectY - pad;
            for (int j = 0; j < bh; j++)
            {
                double py = g.OrigY - pad + j + 0.5;
                for (int i = 0; i < bw; i++)
                {
                    double px = g.OrigX - pad + i + 0.5;
                    byte v;
                    if (sdf)
                    {
                        double best = double.MaxValue; int wind = 0;
                        foreach (var s in segs)
                        {
                            double x0 = s.x0, y0 = s.y0, x1 = s.x1, y1 = s.y1;
                            // winding (nonzero) via horizontal ray to +x
                            if (y0 <= py) { if (y1 > py && Cross(x0, y0, x1, y1, px, py) > 0) wind++; }
                            else if (y1 <= py && Cross(x0, y0, x1, y1, px, py) < 0) wind--;
                            double dx = x1 - x0, dy = y1 - y0;
                            double l2 = dx * dx + dy * dy;
                            double t = l2 > 0 ? ((px - x0) * dx + (py - y0) * dy) / l2 : 0;
                            t = t < 0 ? 0 : t > 1 ? 1 : t;
                            double ex = x0 + t * dx - px, ey = y0 + t * dy - py;
                            double dd = ex * ex + ey * ey;
                            if (dd < best) best = dd;
                        }
                        double dist = Math.Sqrt(best);
                        if (wind == 0) dist = -dist;
                        double val = 0.5 + dist / spread;
                        v = (byte)Math.Clamp((int)Math.Round(val * 255.0), 0, 255);
                    }
                    else
                    {
                        // 4x4 supersampled coverage
                        int inside = 0;
                        for (int sy = 0; sy < 4; sy++)
                            for (int sx = 0; sx < 4; sx++)
                            {
                                double qx = px - 0.5 + (sx + 0.5) / 4, qy = py - 0.5 + (sy + 0.5) / 4;
                                int wind = 0;
                                foreach (var s in segs)
                                {
                                    if (s.y0 <= qy) { if (s.y1 > qy && Cross(s.x0, s.y0, s.x1, s.y1, qx, qy) > 0) wind++; }
                                    else if (s.y1 <= qy && Cross(s.x0, s.y0, s.x1, s.y1, qx, qy) < 0) wind--;
                                }
                                if (wind != 0) inside++;
                            }
                        v = (byte)(inside * 255 / 16);
                    }
                    img[(ay0 + j) * a.Width + ax0 + i] = v;
                }
            }
        });
        a.Alpha = img;
    }

    static double Cross(double x0, double y0, double x1, double y1, double px, double py)
        => (x1 - x0) * (py - y0) - (px - x0) * (y1 - y0);
}

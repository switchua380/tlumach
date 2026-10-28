using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TranslationLauncher.Core;

/// <summary>What happened while bringing a translation (possibly from an older game version) onto the current game data.</summary>
public sealed class MergeLog
{
    public readonly List<(string path, string oldV, string newV)> Changed = new();   // text replaced by the translation
    public readonly List<(string path, string value, string after)> Added = new();   // only in the translation → inserted
    public readonly List<(string path, string value)> NewInGame = new();             // only in the game → kept as is (untranslated)
    public readonly List<(string path, string value)> Dropped = new();               // only in the translation → not inserted
    public readonly List<(string path, string game, string trans)> Kept = new();     // non-text values differ → game value kept
    public readonly List<string> Order = new();                                      // containers whose element order differs
    public readonly List<string> Conflicts = new();                                  // incompatible structure → game value kept

    public JsonObject Stats() => new()
    {
        ["changed"] = Changed.Count, ["added"] = Added.Count, ["newInGame"] = NewInGame.Count, ["dropped"] = Dropped.Count,
        ["kept"] = Kept.Count, ["order"] = Order.Count, ["conflicts"] = Conflicts.Count
    };
}

/// <summary>
/// Order-preserving structural merge of two JSON trees (game = current version, trans = translation):
/// the game's structure and order win, text (string) values come from the translation,
/// array elements are matched by an id field (m_Id, Term, Key …) when there is one.
/// </summary>
public static class JsonMerge
{
    static readonly string[] KeyFields = { "m_Id", "Term", "m_Key", "Key", "key", "id", "Id", "ID", "m_Term", "m_Name", "name", "Name" };

    static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
    static readonly string[] TextFields = { "m_Localized", "m_Text", "Text", "text", "m_Value", "Value", "value", "Languages" };
    public static string Short(JsonNode? n, int max = 400)
    {
        if (n is JsonObject jo)   // an entry: show its text, not the whole object
            foreach (var f in TextFields)
                if (jo[f] is JsonNode tv && (IsString(tv) || tv is JsonObject { Count: 1 } || tv is JsonArray)) return Short(tv is JsonObject w && w["Array"] is JsonArray wa ? wa : tv, max);
        string s = n == null ? "null" : n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.ToString() : n.ToJsonString(Relaxed);
        s = s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return s.Length > max ? s[..max] + "…" : s;
    }

    static bool IsString(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String;
    static bool IsNumArray(JsonArray a) => a.All(x => x is JsonValue v && v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False);

    /// <summary>The id field shared by every element of both arrays (unique values), or null.</summary>
    static string? KeyField(JsonArray a, JsonArray b)
    {
        if (a.Count == 0 && b.Count == 0) return null;
        foreach (var k in KeyFields)
        {
            bool ok = true;
            foreach (var arr in new[] { a, b })
            {
                var seen = new HashSet<string>();
                foreach (var e in arr)
                {
                    if (e is not JsonObject o || o[k] is not JsonValue v) { ok = false; break; }
                    if (!seen.Add(v.ToJsonString())) { ok = false; break; }
                }
                if (!ok) break;
            }
            if (ok) return k;
        }
        return null;
    }

    static string KeyOf(JsonNode? e, string k) { var v = e![k]!; return IsString(v) ? v.ToString() : v.ToJsonString(); }

    public static bool DeepEqual(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    /// <summary>Same structure (keys, element ids, order, value kinds), ignoring the values themselves.</summary>
    public static bool SameShape(JsonNode? a, JsonNode? b)
    {
        switch (a)
        {
            case null: return b == null;
            case JsonObject oa:
                if (b is not JsonObject ob || oa.Count != ob.Count) return false;
                using (var ea = oa.GetEnumerator())
                using (var eb = ob.GetEnumerator())
                    while (ea.MoveNext() && eb.MoveNext())
                        if (ea.Current.Key != eb.Current.Key || !SameShape(ea.Current.Value, eb.Current.Value)) return false;
                return true;
            case JsonArray aa:
                if (b is not JsonArray ab || aa.Count != ab.Count) return false;
                if (IsNumArray(aa) && IsNumArray(ab)) return true;
                var kf = KeyField(aa, ab);
                for (int i = 0; i < aa.Count; i++)
                {
                    if (kf != null && KeyOf(aa[i], kf) != KeyOf(ab[i], kf)) return false;
                    if (!SameShape(aa[i], ab[i])) return false;
                }
                return true;
            case JsonValue va:
                return b is JsonValue vb && Kind(va) == Kind(vb);
        }
        return false;
    }

    static string Kind(JsonValue v) => v.GetValueKind() switch { JsonValueKind.String => "s", JsonValueKind.True or JsonValueKind.False => "b", _ => "n" };

    public static JsonNode? Merge(JsonNode? g, JsonNode? t, string path, MergeLog log, bool add)
    {
        if (t == null) return g?.DeepClone();
        if (g == null) return t.DeepClone();
        if (g is JsonObject og && t is JsonObject ot)
        {
            var gl = og.Select(kv => (kv.Key, kv.Value)).ToList();
            var tl = ot.Select(kv => (kv.Key, kv.Value)).ToList();
            var res = MergeKeyed(gl, tl, k => Join(path, k), log, add, path);
            var o = new JsonObject();
            foreach (var (k, v) in res) o[k] = v;
            return o;
        }
        if (g is JsonArray ag && t is JsonArray at)
        {
            if (IsNumArray(ag) && IsNumArray(at))
            {
                if (!JsonNode.DeepEquals(ag, at)) log.Kept.Add((path, $"[{ag.Count} чисел]", $"[{at.Count} чисел]"));
                return ag.DeepClone();
            }
            var kf = KeyField(ag, at);
            if (kf != null)
            {
                var gl = ag.Select(e => (KeyOf(e, kf), e)).ToList();
                var tl = at.Select(e => (KeyOf(e, kf), e)).ToList();
                var res = MergeKeyed(gl, tl, k => $"{path}[{kf}={k}]", log, add, path);
                return new JsonArray(res.Select(x => x.v).ToArray());
            }
            var arr = new JsonArray();
            int n = Math.Min(ag.Count, at.Count);
            for (int i = 0; i < n; i++) arr.Add(Merge(ag[i], at[i], $"{path}[{i}]", log, add));
            for (int i = n; i < ag.Count; i++) { log.NewInGame.Add(($"{path}[{i}]", Short(ag[i]))); arr.Add(ag[i]?.DeepClone()); }
            for (int i = n; i < at.Count; i++)
            {
                if (add) { log.Added.Add(($"{path}[{i}]", Short(at[i]), i > 0 ? $"{path}[{i - 1}]" : "")); arr.Add(at[i]?.DeepClone()); }
                else log.Dropped.Add(($"{path}[{i}]", Short(at[i])));
            }
            return arr;
        }
        if (g is JsonValue vg && t is JsonValue vt)
        {
            if (path == "m_Name")   // the asset's own name identifies it in the game — never renamed by an import
            {
                if (vg.ToJsonString() != vt.ToJsonString()) log.Kept.Add((path, Short(vg), Short(vt)));
                return vg.DeepClone();
            }
            if (IsString(vg) && IsString(vt))
            {
                if (vg.ToString() != vt.ToString()) { log.Changed.Add((path, Short(vg), Short(vt))); return vt.DeepClone(); }
                return vg.DeepClone();
            }
            if (vg.ToJsonString() != vt.ToJsonString()) log.Kept.Add((path, Short(vg), Short(vt)));
            return vg.DeepClone();
        }
        log.Conflicts.Add(path);
        return g.DeepClone();
    }

    static string Join(string path, string key) => key == "Array" ? path : path == "" ? key : path + "." + key;

    /// <summary>Game order wins; translation-only entries go right after the entry that precedes them in the translation.</summary>
    static List<(string k, JsonNode? v)> MergeKeyed(List<(string k, JsonNode? v)> g, List<(string k, JsonNode? v)> t,
        Func<string, string> pathOf, MergeLog log, bool add, string containerPath)
    {
        var tmap = new Dictionary<string, JsonNode?>();
        foreach (var (k, v) in t) tmap.TryAdd(k, v);
        var gset = new HashSet<string>(g.Select(x => x.k));
        var res = new List<(string k, JsonNode? v)>();
        foreach (var (k, v) in g)
        {
            if (tmap.TryGetValue(k, out var tv)) res.Add((k, Merge(v, tv, pathOf(k), log, add)));
            else { log.NewInGame.Add((pathOf(k), Short(v))); res.Add((k, v?.DeepClone())); }
        }
        // order check on the common entries
        var go = g.Where(x => tmap.ContainsKey(x.k)).Select(x => x.k).ToList();
        var to = t.Where(x => gset.Contains(x.k)).Select(x => x.k).Distinct().ToList();
        if (!go.SequenceEqual(to)) log.Order.Add(containerPath == "" ? "(корінь)" : containerPath);
        // translation-only entries
        int cursor = 0; string prev = "";
        foreach (var (k, v) in t)
        {
            if (gset.Contains(k)) { cursor = res.FindIndex(x => x.k == k) + 1; prev = pathOf(k); continue; }
            if (res.Any(x => x.k == k)) continue;
            if (add) { res.Insert(cursor++, (k, v?.DeepClone())); log.Added.Add((pathOf(k), Short(v), prev)); prev = pathOf(k); }
            else log.Dropped.Add((pathOf(k), Short(v)));
        }
        return res;
    }

    // ------------------------------------------------------------------ plain text files as keyed lines
    public sealed class LineDoc { public JsonObject Obj = new(); public string Nl = "\n"; public bool Trailing; public string Sep = ""; }

    /// <summary>Choose how lines are keyed (TAB / '=' / ',' first column, or line number) from the game text.</summary>
    public static string DetectSep(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("#") && !l.TrimStart().StartsWith("//")).ToList();
        if (lines.Count == 0) return "";
        foreach (var sep in new[] { "\t", "=", "," })
            if (lines.Count(l => l.Contains(sep)) >= lines.Count * 0.7)
            {
                var keys = lines.Select(l => KeyOfLine(l, sep)).ToList();
                if (keys.Distinct().Count() >= keys.Count * 0.9) return sep;
            }
        return "";
    }

    static string KeyOfLine(string l, string sep)
    {
        if (sep == ",")
        {
            var s = l.TrimStart();
            if (s.StartsWith("\"")) { int i = 1; var sb = new StringBuilder(); while (i < s.Length) { if (s[i] == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; } break; } sb.Append(s[i++]); } return sb.ToString(); }
        }
        int p = l.IndexOf(sep, StringComparison.Ordinal);
        return (p < 0 ? l : l[..p]).Trim();
    }

    public static LineDoc ToLines(string text, string sep)
    {
        var d = new LineDoc { Sep = sep, Nl = text.Contains("\r\n") ? "\r\n" : "\n", Trailing = text.EndsWith("\n") };
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (d.Trailing && lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        var used = new Dictionary<string, int>();
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            string key;
            if (sep == "" || l.Trim().Length == 0 || l.TrimStart().StartsWith("#") || l.TrimStart().StartsWith("//")) key = sep == "" ? "рядок " + (i + 1) : "·" + l.Trim();
            else key = KeyOfLine(l, sep);
            if (used.TryGetValue(key, out var n)) { used[key] = n + 1; key = key + " #" + (n + 1); } else used[key] = 1;
            d.Obj[key] = l;
        }
        return d;
    }

    public static string FromLines(JsonObject o, LineDoc fmt)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var kv in o) { if (!first) sb.Append(fmt.Nl); sb.Append(kv.Value?.ToString() ?? ""); first = false; }
        if (fmt.Trailing) sb.Append(fmt.Nl);
        return sb.ToString();
    }

    public static string WriteJson(JsonNode n, string likeText)
    {
        bool indented = likeText.TrimEnd().Contains('\n');
        var s = n.ToJsonString(new JsonSerializerOptions { WriteIndented = indented, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() });
        if (indented && likeText.Contains("\r\n")) s = s.Replace("\n", "\r\n");
        return s;
    }

    // ------------------------------------------------------------------ similarity
    public static double Similarity(string a, string b)
    {
        if (a == b) return 1;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 0.98;
        var x = TMPFontReplacer.FontPairs.Key(a); var y = TMPFontReplacer.FontPairs.Key(b);
        if (x.Length == 0 || y.Length == 0) return 0;
        if (x == y) return 0.95;
        int d = Levenshtein(x, y);
        return Math.Max(0, 1 - (double)d / Math.Max(x.Length, y.Length)) * 0.92;
    }

    static int Levenshtein(string a, string b)
    {
        if (a.Length > 200) a = a[..200]; if (b.Length > 200) b = b[..200];
        var prev = new int[b.Length + 1]; var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}

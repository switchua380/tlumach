using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace TranslationLauncher.Core;

public sealed class Row
{
    public string Id = "";
    public List<string> Cells = new();
}

/// <summary>A translatable view of an asset or a loose file.</summary>
public sealed class EntryTable
{
    public string Kind = "";             // text | stringtable | i2 | generic | raw | loose
    public string Info = "";
    public List<string> Columns = new();
    public List<int> Editable = new();
    public List<Row> Rows = new();
    public string? Text;                 // Kind == text / loose
    public bool Binary;
    public List<string>? Languages;      // multilingual table (I2 / per-row string arrays): a language can be added
    public string LangWhere = "";
    public bool CanAddLanguage;          // I2 / generic arrays; StringTable has one asset per language

    // "line_9" before "line_10"; letters compared without case
    static int Natural(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                string na = a[si..i].TrimStart('0'), nb = b[sj..j].TrimStart('0');
                int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                int c = string.Compare(a, i, b, j, 1, StringComparison.InvariantCultureIgnoreCase);
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    public JsonObject ToJson(int offset = 0, int limit = int.MaxValue, string? filter = null, Func<Row, bool>? where = null, int sortCol = -1, bool desc = false)
    {
        var rows = new JsonArray();
        IEnumerable<Row> src = where == null ? Rows : Rows.Where(where);
        if (!string.IsNullOrEmpty(filter)) src = src.Where(r => r.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.Cells.Any(c => c.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        if (sortCol >= 0)   // -1 = game order; empty cells always go last
        {
            string Cell(Row r) => sortCol < r.Cells.Count ? r.Cells[sortCol] : "";
            src = src.OrderBy(r => Cell(r).Length == 0).ThenBy(Cell, Comparer<string>.Create((x, y) => desc ? Natural(y, x) : Natural(x, y)));
        }
        var list = src.ToList();
        foreach (var r in list.Skip(offset).Take(limit))
            rows.Add(new JsonObject { ["id"] = r.Id, ["cells"] = new JsonArray(r.Cells.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()) });
        return new JsonObject
        {
            ["kind"] = Kind, ["info"] = Info, ["binary"] = Binary,
            ["columns"] = new JsonArray(Columns.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            ["editable"] = new JsonArray(Editable.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            ["total"] = list.Count, ["offset"] = offset, ["rows"] = rows,
            ["text"] = Text,
            ["languages"] = Languages == null ? null : new JsonArray(Languages.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            ["langWhere"] = LangWhere, ["canAddLang"] = CanAddLanguage
        };
    }
}

/// <summary>Builds translatable tables from Unity assets and applies edits back.</summary>
public static class Extract
{
    // ------------------------------------------------------------------ build
    public static EntryTable Build(UnityDoc doc, int file, long pathId, Func<long, Dictionary<long, string>?>? sharedKeys = null)
    {
        var info = doc.Info(file, pathId);
        var f = doc.Field(file, pathId, out var how);
        if (f == null) return RawTable(doc.RawBytes(file, pathId), "Layout unknown (no typetree / scripts) — raw string mode");

        // TextAsset
        if (info.TypeId == (int)AssetClassID.TextAsset && !f["m_Script"].IsDummy)
        {
            byte[] bytes;
            try { bytes = f["m_Script"].AsByteArray ?? Encoding.UTF8.GetBytes(f["m_Script"].AsString ?? ""); }
            catch { bytes = Encoding.UTF8.GetBytes(f["m_Script"].AsString ?? ""); }
            var t = new EntryTable { Kind = "text", Info = "TextAsset" };
            if (LooksBinary(bytes)) { t.Binary = true; t.Text = null; t.Info += " (binary)"; }
            else t.Text = DecodeText(bytes);
            return t;
        }
        // Unity Localization StringTable
        if (!f["m_TableData"].IsDummy && !f["m_SharedData"].IsDummy)
        {
            var t = new EntryTable { Kind = "stringtable", Info = "Unity Localization StringTable" + (f["m_LocaleId"].IsDummy ? "" : " (" + f["m_LocaleId"]["m_Code"].AsString + ")") };
            t.Columns = new() { "Key", "Text" }; t.Editable = new() { 1 };
            Dictionary<long, string>? keys = null;
            try { keys = sharedKeys?.Invoke(f["m_SharedData"]["m_PathID"].AsLong); } catch { }
            var arr = f["m_TableData"]["Array"].Children;
            for (int i = 0; i < arr.Count; i++)
            {
                long id = arr[i]["m_Id"].AsLong;
                string key = keys != null && keys.TryGetValue(id, out var k) ? k : id.ToString(CultureInfo.InvariantCulture);
                t.Rows.Add(new Row { Id = i.ToString(), Cells = new() { key, arr[i]["m_Localized"].AsString } });
            }
            if (keys == null) t.Info += " — keys not found (SharedTableData in another bundle), ids shown";
            t.Languages = new() { f["m_LocaleId"].IsDummy ? "Text" : f["m_LocaleId"]["m_Code"].AsString }; t.LangWhere = "m_TableData";
            return t;
        }
        // I2 Localization
        var src = !f["mSource"].IsDummy ? f["mSource"] : f;
        if (!src["mTerms"].IsDummy && !src["mLanguages"].IsDummy)
        {
            var t = new EntryTable { Kind = "i2", Info = "I2 Localization LanguageSource" };
            var langs = src["mLanguages"]["Array"].Children.Select(l => l["Name"].AsString + (l["Code"].IsDummy ? "" : " [" + l["Code"].AsString + "]")).ToList();
            t.Columns = new() { "Term" }; t.Columns.AddRange(langs);
            t.Editable = Enumerable.Range(1, langs.Count).ToList();
            t.Languages = langs; t.LangWhere = "mLanguages"; t.CanAddLanguage = true;
            var terms = src["mTerms"]["Array"].Children;
            for (int i = 0; i < terms.Count; i++)
            {
                var row = new Row { Id = i.ToString() };
                row.Cells.Add(terms[i]["Term"].AsString);
                var tl = terms[i]["Languages"]["Array"].Children;
                for (int l = 0; l < langs.Count; l++) row.Cells.Add(l < tl.Count ? tl[l].AsString : "");
                t.Rows.Add(row);
            }
            return t;
        }
        // generic: every string in the asset
        var g = new EntryTable { Kind = "generic", Info = $"{(AssetClassID)info.TypeId} — all text fields ({how})", Columns = new() { "Field", "Text" }, Editable = new() { 1 } };
        Walk(f, "", (path, fld) => g.Rows.Add(new Row { Id = path, Cells = new() { path, fld.AsString } }));
        var li = FindLanguages(f);
        if (li != null) { g.Languages = li.Names; g.LangWhere = $"{li.Where} ({li.Cells.Count} × {li.Count})"; g.CanAddLanguage = true; }
        return g;
    }

    // ------------------------------------------------------------------ multilingual arrays
    /// <summary>A multilingual array: every row holds a string array with one entry per language.</summary>
    public sealed class LangInfo
    {
        public int Count;
        public List<AssetTypeValueField> Cells = new();   // the per-row string arrays
        public AssetTypeValueField? NameArray;            // language names (top-level string array of the same length), if any
        public List<string> Names = new();
        public string Where = "";
    }

    static AssetTypeValueField? AsStringArray(AssetTypeValueField f)
    {
        if (f.TemplateField.ValueType == AssetValueType.String) return null;
        var a = f.TemplateField.IsArray ? f : f.Children.Count == 1 && f.Children[0].TemplateField.IsArray ? f.Children[0] : null;
        if (a == null || a.TemplateField.Children.Count < 2 || a.TemplateField.Children[1].ValueType != AssetValueType.String) return null;
        return a;
    }

    static void StringArrays(AssetTypeValueField f, string path, int depth, List<(string path, AssetTypeValueField arr)> res)
    {
        if (depth > 4 || f.TemplateField.ValueType == AssetValueType.String) return;
        var sa = AsStringArray(f);
        if (sa != null) { res.Add((path, sa)); return; }
        if (f.TemplateField.IsArray) return;
        foreach (var c in f.Children)
            if (c.FieldName is not ("m_GameObject" or "m_Script")) StringArrays(c, path == "" ? c.FieldName : path + "." + c.FieldName, depth + 1, res);
    }

    static void StructArrays(AssetTypeValueField f, string path, int depth, List<(string path, AssetTypeValueField arr)> res)
    {
        if (depth > 6 || f.TemplateField.ValueType == AssetValueType.String) return;
        if (f.TemplateField.IsArray)
        {
            if (f.Value?.ValueType == AssetValueType.ByteArray) return;
            var et = f.TemplateField.Children.Count >= 2 ? f.TemplateField.Children[1] : null;
            if (et != null && et.ValueType == AssetValueType.None && !et.IsArray && et.Children.Count > 0) res.Add((path, f));
            return;
        }
        foreach (var c in f.Children)
            if (c.FieldName is not ("m_GameObject" or "m_Script")) StructArrays(c, path == "" ? c.FieldName : path + "." + c.FieldName, depth + 1, res);
    }

    static readonly string[] LangWords = { "lang", "locale", "culture" };

    /// <summary>Finds the biggest array of rows where (almost) every row has a string array of the same length ≥ 2.</summary>
    public static LangInfo? FindLanguages(AssetTypeValueField root)
    {
        var outers = new List<(string path, AssetTypeValueField arr)>();
        StructArrays(root, "", 0, outers);
        LangInfo? best = null;
        foreach (var (path, arr) in outers)
        {
            int rows = arr.Children.Count;
            if (rows < 3) continue;
            var per = new Dictionary<string, List<AssetTypeValueField>>();
            foreach (var el in arr.Children)
            {
                var l = new List<(string path, AssetTypeValueField arr)>();
                StringArrays(el, "", 0, l);
                foreach (var (p, sa) in l) { if (!per.TryGetValue(p, out var list)) per[p] = list = new(); list.Add(sa); }
            }
            foreach (var (sub, list) in per)
            {
                if (list.Count < rows * 0.9) continue;
                var common = list.GroupBy(x => x.Children.Count).OrderByDescending(g => g.Count()).First();
                if (common.Key < 2 || common.Count() < rows * 0.9) continue;
                bool named = LangWords.Any(w => sub.Contains(w, StringComparison.OrdinalIgnoreCase) || path.Contains(w, StringComparison.OrdinalIgnoreCase));
                if (best == null || rows > best.Cells.Count || named && rows == best.Cells.Count)
                    best = new LangInfo { Count = common.Key, Cells = list, Where = $"{path}[].{sub}".Replace(".Array", "") };
            }
        }
        if (best == null) return null;
        var tops = new List<(string path, AssetTypeValueField arr)>();
        StringArrays(root, "", 0, tops);
        best.NameArray = tops.Where(x => x.arr.Children.Count == best.Count && LangWords.Any(w => x.path.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Select(x => x.arr).FirstOrDefault();
        best.Names = best.NameArray != null ? best.NameArray.Children.Select(c => c.AsString).ToList()
            : Enumerable.Range(1, best.Count).Select(i => "#" + i).ToList();
        return best;
    }

    static void AppendString(AssetTypeValueField arr, string value)
    {
        var e = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr); e.AsString = value;
        arr.Children.Add(e); arr.AsArray = new AssetTypeArrayInfo(arr.Children.Count);
    }

    /// <summary>Generic multilingual table: regex for the row ids (field paths) of language <paramref name="lang"/>.</summary>
    public static string LangRowPattern(string where, int lang)
    {
        var w = where.Split(' ')[0];   // "entries[].values (3 × 2)" → "entries[].values"
        return "^" + System.Text.RegularExpressions.Regex.Escape(w).Replace("\\[]", "\\[\\d+]") + "\\[" + lang + "]$";
    }

    public static bool IsUkrainian(string s) =>
        s.StartsWith("uk", StringComparison.OrdinalIgnoreCase) && (s.Length == 2 || !char.IsLetter(s[2])) ||
        s.Contains("Ukrain", StringComparison.OrdinalIgnoreCase) || s.Contains("Україн", StringComparison.OrdinalIgnoreCase) || s.Contains("[uk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Adds a new language column (e.g. Ukrainian) to a multilingual table: I2 LanguageSource (mLanguages + every term)
    /// or rows with per-language string arrays. New cells get the text of column <paramref name="copyFrom"/> (−1 = empty),
    /// so the game shows something until it is translated. Returns the number of rows changed.
    /// </summary>
    public static int AddLanguage(UnityDoc doc, int file, long pathId, string name, string code, int copyFrom)
    {
        var f = doc.Field(file, pathId, out _) ?? throw new Exception("Asset layout unknown");
        int n = AddLanguage(f, name, code, copyFrom);
        if (n > 0) doc.MarkDirty(file, pathId);
        return n;
    }

    public static int AddLanguage(AssetTypeValueField f, string name, string code, int copyFrom)
    {
        int n = 0;
        var src = !f["mSource"].IsDummy ? f["mSource"] : f;
        if (!src["mTerms"].IsDummy && !src["mLanguages"].IsDummy)
        {
            var langs = src["mLanguages"]["Array"];
            foreach (var l in langs.Children)
                if (IsUkrainian(l["Name"].AsString) || !l["Code"].IsDummy && IsUkrainian(l["Code"].AsString))
                    throw new Exception("This table already has the language: " + l["Name"].AsString);
            int old = langs.Children.Count;
            var le = ValueBuilder.DefaultValueFieldFromArrayTemplate(langs);
            le["Name"].AsString = name;
            if (!le["Code"].IsDummy) le["Code"].AsString = code;
            langs.Children.Add(le); langs.AsArray = new AssetTypeArrayInfo(langs.Children.Count);
            foreach (var term in src["mTerms"]["Array"].Children)
            {
                var tl = term["Languages"]["Array"];
                while (tl.Children.Count < old) AppendString(tl, "");
                AppendString(tl, copyFrom >= 0 && copyFrom < tl.Children.Count ? tl.Children[copyFrom].AsString : "");
                if (!term["Languages_Touch"].IsDummy)
                {
                    var touch = term["Languages_Touch"]["Array"];
                    if (touch.Children.Count > 0) { while (touch.Children.Count < old) AppendString(touch, ""); AppendString(touch, ""); }
                }
                if (!term["Flags"].IsDummy)
                {
                    var fl = term["Flags"]["Array"];
                    if (fl.Value?.ValueType == AssetValueType.ByteArray)
                    {
                        var b = fl.AsByteArray ?? Array.Empty<byte>();
                        if (b.Length > 0 && b.Length <= old) { Array.Resize(ref b, old + 1); fl.AsByteArray = b; }
                    }
                    else if (fl.Children.Count > 0 && fl.Children.Count <= old)
                        while (fl.Children.Count <= old)
                        {
                            var e = ValueBuilder.DefaultValueFieldFromArrayTemplate(fl);
                            fl.Children.Add(e); fl.AsArray = new AssetTypeArrayInfo(fl.Children.Count);
                        }
                }
                n++;
            }
        }
        else
        {
            var li = FindLanguages(f) ?? throw new Exception("No multilingual array found in this asset");
            if (li.NameArray != null && li.Names.Any(IsUkrainian)) throw new Exception("This table already has the language: " + li.Names.First(IsUkrainian));
            foreach (var arr in li.Cells)
            {
                while (arr.Children.Count < li.Count) AppendString(arr, "");
                AppendString(arr, copyFrom >= 0 && copyFrom < arr.Children.Count ? arr.Children[copyFrom].AsString : "");
                n++;
            }
            if (li.NameArray != null) AppendString(li.NameArray, name);
        }
        return n;
    }

    static void Walk(AssetTypeValueField f, string path, Action<string, AssetTypeValueField> onString)
    {
        var tf = f.TemplateField;
        if (tf.ValueType == AssetValueType.String && f.Value != null) { onString(path, f); return; }
        if (tf.IsArray)
        {
            if (f.Value?.ValueType == AssetValueType.ByteArray) return;
            for (int i = 0; i < f.Children.Count; i++) Walk(f.Children[i], $"{path}[{i}]", onString);
            return;
        }
        foreach (var c in f.Children)
        {
            if (c.FieldName is "m_GameObject" or "m_Script") continue;
            string p = path == "" ? c.FieldName : path + "." + c.FieldName;
            if (c.Children.Count == 1 && c.Children[0].TemplateField.IsArray && c.TemplateField.ValueType != AssetValueType.String)
                Walk(c.Children[0], p, onString);
            else Walk(c, p, onString);
        }
    }

    static AssetTypeValueField? Resolve(AssetTypeValueField root, string path)
    {
        var cur = root;
        foreach (var part in path.Split('.'))
        {
            string name = part; var idx = new List<int>();
            int b = part.IndexOf('[');
            if (b >= 0) { name = part[..b]; foreach (var m in System.Text.RegularExpressions.Regex.Matches(part[b..], @"\[(\d+)\]")) idx.Add(int.Parse(((System.Text.RegularExpressions.Match)m).Groups[1].Value)); }
            if (name != "")
            {
                cur = cur[name];
                if (cur.IsDummy) return null;
                if (cur.Children.Count == 1 && cur.Children[0].TemplateField.IsArray && cur.TemplateField.ValueType != AssetValueType.String) cur = cur.Children[0];
            }
            foreach (var i in idx)
            {
                if (i >= cur.Children.Count) return null;
                cur = cur.Children[i];
                if (cur.Children.Count == 1 && cur.Children[0].TemplateField.IsArray && cur.TemplateField.ValueType != AssetValueType.String) cur = cur.Children[0];
            }
        }
        return cur;
    }

    // ------------------------------------------------------------------ apply edits
    public sealed record Change(string Id, int Col, string Value);

    public static int Apply(UnityDoc doc, int file, long pathId, EntryTable table, IEnumerable<Change> changes, string? newText = null)
    {
        int n = 0;
        var f = doc.Field(file, pathId, out _);
        if (table.Kind == "raw")
        {
            var raw = doc.RawBytes(file, pathId);
            var map = changes.ToDictionary(c => c.Id, c => c.Value);
            var res = RawApply(raw, map, out n);
            doc.ReplaceRaw(file, pathId, res);
            return n;
        }
        if (f == null) throw new Exception("Asset layout unknown");
        if (table.Kind == "text")
        {
            if (newText == null) return 0;
            f["m_Script"].AsString = newText; n = 1;
        }
        else
        {
            foreach (var c in changes)
            {
                AssetTypeValueField? target = null;
                switch (table.Kind)
                {
                    case "stringtable":
                        target = f["m_TableData"]["Array"].Children[int.Parse(c.Id)]["m_Localized"]; break;
                    case "i2":
                        var src = !f["mSource"].IsDummy ? f["mSource"] : f;
                        var term = src["mTerms"]["Array"].Children[int.Parse(c.Id)];
                        var langs = term["Languages"]["Array"];
                        int li = c.Col - 1;
                        while (langs.Children.Count <= li)
                        {
                            var e = ValueBuilder.DefaultValueFieldFromArrayTemplate(langs); e.AsString = "";
                            langs.Children.Add(e); langs.AsArray = new AssetTypeArrayInfo(langs.Children.Count);
                        }
                        target = langs.Children[li]; break;
                    case "generic":
                        target = Resolve(f, c.Id); break;
                }
                if (target == null) continue;
                if (target.AsString != c.Value) { target.AsString = c.Value; n++; }
            }
        }
        if (n > 0) doc.MarkDirty(file, pathId);
        return n;
    }

    // ------------------------------------------------------------------ text helpers
    public static bool LooksBinary(byte[] b)
    {
        int n = Math.Min(b.Length, 8192), zeros = 0;
        for (int i = 0; i < n; i++) if (b[i] == 0) zeros++;
        if (b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF))) return false;
        return zeros > n / 50;
    }

    public static string DecodeText(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        return Encoding.UTF8.GetString(b);
    }

    // ------------------------------------------------------------------ raw string mode (no layout available)
    /// <summary>Find Unity-serialized strings: int32 length + UTF-8 bytes + zero padding to 4.</summary>
    public static List<(int pos, int len, string text)> RawScan(byte[] d)
    {
        var res = new List<(int, int, string)>();
        var utf8 = new UTF8Encoding(false, true);
        int p = 0;
        while (p + 4 <= d.Length)
        {
            int len = BitConverter.ToInt32(d, p);
            if (len >= 2 && len < 1 << 20 && p + 4 + len <= d.Length)
            {
                int pad = (4 - (len & 3)) & 3;
                bool padOk = p + 4 + len + pad <= d.Length;
                for (int k = 0; padOk && k < pad; k++) if (d[p + 4 + len + k] != 0) padOk = false;
                if (padOk)
                {
                    try
                    {
                        var s = utf8.GetString(d, p + 4, len);
                        int printable = s.Count(ch => !char.IsControl(ch) || ch == '\n' || ch == '\r' || ch == '\t');
                        if (printable == s.Length && s.Any(char.IsLetter))
                        {
                            res.Add((p, len, s));
                            p += 4 + len + pad;
                            continue;
                        }
                    }
                    catch { }
                }
            }
            p += 4;
        }
        return res;
    }

    static EntryTable RawTable(byte[] raw, string info)
    {
        var t = new EntryTable { Kind = "raw", Info = info, Columns = new() { "Offset", "Text" }, Editable = new() { 1 } };
        foreach (var (pos, len, text) in RawScan(raw)) t.Rows.Add(new Row { Id = pos.ToString(), Cells = new() { "0x" + pos.ToString("X"), text } });
        return t;
    }

    static byte[] RawApply(byte[] raw, Dictionary<string, string> changes, out int n)
    {
        n = 0;
        var found = RawScan(raw);
        using var ms = new MemoryStream();
        int last = 0;
        foreach (var (pos, len, text) in found)
        {
            if (!changes.TryGetValue(pos.ToString(), out var nv) || nv == text) continue;
            ms.Write(raw, last, pos - last);
            var bytes = Encoding.UTF8.GetBytes(nv);
            ms.Write(BitConverter.GetBytes(bytes.Length));
            ms.Write(bytes);
            int pad = (4 - (bytes.Length & 3)) & 3; for (int k = 0; k < pad; k++) ms.WriteByte(0);
            int opad = (4 - (len & 3)) & 3;
            last = pos + 4 + len + opad;
            n++;
        }
        ms.Write(raw, last, raw.Length - last);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ export / import of tables
    public static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
    public static string Unesc(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char c = s[++i];
                sb.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '\\' => '\\', _ => c });
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    public static string ExportTxt(EntryTable t)
    {
        if (t.Kind is "text" or "loose") return t.Text ?? "";
        var sb = new StringBuilder();
        sb.Append("#id\t").AppendLine(string.Join("\t", t.Columns.Select(Esc)));
        foreach (var r in t.Rows) sb.Append(Esc(r.Id)).Append('\t').AppendLine(string.Join("\t", r.Cells.Select(Esc)));
        return sb.ToString();
    }

    public static string ExportCsv(EntryTable t)
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.Append(Q("id")).Append(',').AppendLine(string.Join(",", t.Columns.Select(Q)));
        foreach (var r in t.Rows) sb.Append(Q(r.Id)).Append(',').AppendLine(string.Join(",", r.Cells.Select(Q)));
        return sb.ToString();
    }

    public static string ExportJson(EntryTable t)
    {
        var o = new JsonObject { ["format"] = "translation-launcher", ["kind"] = t.Kind };
        if (t.Kind is "text" or "loose") o["text"] = t.Text;
        else
        {
            o["columns"] = new JsonArray(t.Columns.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
            var rows = new JsonArray();
            foreach (var r in t.Rows) rows.Add(new JsonObject { ["id"] = r.Id, ["cells"] = new JsonArray(r.Cells.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()) });
            o["rows"] = rows;
        }
        return o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() });
    }

    /// <summary>Parse an imported strings file into changes for the given table. Rows are matched by id, then by first column (key).</summary>
    public static (List<Change> changes, string? text) ParseImport(EntryTable t, string name, string content)
    {
        var changes = new List<Change>();
        if (t.Kind is "text" or "loose")
        {
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                try { var j = JsonNode.Parse(content); if (j?["format"]?.ToString() == "translation-launcher") return (changes, j["text"]?.ToString() ?? ""); } catch { }
            }
            return (changes, content);
        }
        var byId = t.Rows.ToDictionary(r => r.Id);
        var byKey = new Dictionary<string, Row>();
        foreach (var r in t.Rows) if (r.Cells.Count > 0) byKey.TryAdd(r.Cells[0], r);
        void Put(string id, List<string> cells, List<string>? header)
        {
            Row? row = byId.GetValueOrDefault(id);
            if (row == null && cells.Count > 0) row = byKey.GetValueOrDefault(cells[0]);
            if (row == null) return;
            foreach (var col in t.Editable)
            {
                int src = col;
                if (header != null) { src = header.IndexOf(t.Columns[col]); if (src < 0) continue; }
                if (src < cells.Count && cells[src] != row.Cells[col]) changes.Add(new Change(row.Id, col, cells[src]));
            }
        }
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var j = JsonNode.Parse(content)!;
            if (j["rows"] is JsonArray rows)
            {
                var header = (j["columns"] as JsonArray)?.Select(x => x!.ToString()).ToList();
                foreach (var r in rows) Put(r!["id"]!.ToString(), (r["cells"] as JsonArray)!.Select(x => x?.ToString() ?? "").ToList(), header);
            }
            else if (j is JsonObject map)   // simple {"key": "text"} map
                foreach (var kv in map)
                {
                    var row = byKey.GetValueOrDefault(kv.Key) ?? byId.GetValueOrDefault(kv.Key);
                    if (row == null || t.Editable.Count == 0) continue;
                    var v = kv.Value?.ToString() ?? "";
                    if (row.Cells[t.Editable[0]] != v) changes.Add(new Change(row.Id, t.Editable[0], v));
                }
        }
        else if (name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            var lines = ParseCsv(content);
            if (lines.Count == 0) return (changes, null);
            var header = lines[0].Skip(1).ToList();
            foreach (var l in lines.Skip(1)) if (l.Count > 1) Put(l[0], l.Skip(1).ToList(), header);
        }
        else
        {
            List<string>? header = null;
            foreach (var line in content.Split('\n'))
            {
                var l = line.TrimEnd('\r');
                if (l.Length == 0) continue;
                var parts = l.Split('\t').Select(Unesc).ToList();
                if (l.StartsWith("#id\t")) { header = parts.Skip(1).ToList(); continue; }
                if (parts.Count < 2) continue;
                if (parts.Count == 2 && t.Columns.Count >= 2 && header == null)
                {   // "key<TAB>text" form
                    var row = byId.GetValueOrDefault(parts[0]) ?? byKey.GetValueOrDefault(parts[0]);
                    if (row != null && t.Editable.Count > 0 && row.Cells[t.Editable[0]] != parts[1]) changes.Add(new Change(row.Id, t.Editable[0], parts[1]));
                    continue;
                }
                Put(parts[0], parts.Skip(1).ToList(), header);
            }
        }
        return (changes, null);
    }

    static List<List<string>> ParseCsv(string s)
    {
        var res = new List<List<string>>(); var cur = new List<string>(); var sb = new StringBuilder(); bool q = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (q) { if (c == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(c); }
            else if (c == '"') q = true;
            else if (c == ',') { cur.Add(sb.ToString()); sb.Clear(); }
            else if (c == '\n') { cur.Add(sb.ToString().TrimEnd('\r')); sb.Clear(); res.Add(cur); cur = new(); }
            else sb.Append(c);
        }
        if (sb.Length > 0 || cur.Count > 0) { cur.Add(sb.ToString()); res.Add(cur); }
        return res;
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using TranslationLauncher.Core;

namespace TranslationLauncher;

/// <summary>
/// «Імпорт у гру»: files dropped onto the launcher are matched to their place in the game (assets, bundles, loose files),
/// compared with the current game data and either replaced (same structure) or brought up to the current version.
/// </summary>
public static partial class Api
{
    sealed class Cand
    {
        public string Rel = "", FileName = "", Name = "", Type = "", Script = "";
        public int File = -1, TypeId; public long PathId; public bool Loose;
        public double Score; public string Why = "";
        public string Key => Loose ? "L|" + Rel : $"{Rel}|{File}|{PathId}";
        public JsonObject ToJson() => new()
        {
            ["key"] = Key, ["rel"] = Rel, ["file"] = File, ["pathId"] = PathId.ToString(), ["name"] = Name, ["type"] = Loose ? "File" : Type,
            ["script"] = Script, ["loose"] = Loose, ["score"] = Math.Round(Score, 3), ["why"] = Why, ["fileName"] = FileName
        };
    }

    sealed class BItem
    {
        public string Id = Guid.NewGuid().ToString("N")[..8], Name = "", Path = "", Format = "", NameHint = "";
        public string? FileHint; public long? PidHint; public int? TypeHint;
        public List<Cand> Cands = new(); public Cand? Target; public string? NewRel;
        public string Status = "new", Content = "", Note = ""; public bool Include = true;
        public JsonObject? Stats; public List<string> Sample = new(); public string? Error;
        public long Size;
        public JsonObject ToJson() => new()
        {
            ["id"] = Id, ["name"] = Name, ["size"] = Size, ["format"] = Format, ["nameHint"] = NameHint, ["status"] = Status, ["content"] = Content,
            ["note"] = Note, ["include"] = Include, ["error"] = Error, ["stats"] = Stats?.DeepClone(), ["newRel"] = NewRel,
            ["target"] = Target?.ToJson(), ["cands"] = new JsonArray(Cands.Select(c => (JsonNode?)c.ToJson()).ToArray()),
            ["sample"] = new JsonArray(Sample.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray())
        };
    }

    static readonly Dictionary<string, List<BItem>> batches = new();
    static readonly Dictionary<string, Dictionary<string, (long stamp, List<Cand> items)>> indexes = new();
    static readonly object batchLock = new();

    static List<BItem> Batch(Project p) { lock (batchLock) { if (!batches.TryGetValue(p.Id, out var l)) batches[p.Id] = l = new(); return l; } }
    static string Incoming(Project p) => System.IO.Path.Combine(p.WorkDir, "incoming");

    static JsonObject BatchState(Project p)
    {
        var items = Batch(p);
        bool need = items.Any(NeedsDecision);
        return new JsonObject { ["items"] = new JsonArray(items.Select(i => (JsonNode?)i.ToJson()).ToArray()), ["needDecision"] = need };
    }

    static bool NeedsDecision(BItem i) => i.Include && i.Status != "new" && (i.Status == "notfound" && i.NewRel == null || i.Content is "different" or "unknown" or "error");

    static HttpServer.Resp? BatchRoutes(HttpServer.Req r, string route)
    {
        switch (route)
        {
            case "GET /api/batch": return J(BatchState(Store.Get(r.Q("project"))));
            case "POST /api/batch/add":
            {
                var b = Body(r); var p = Store.Get(b["project"]!.ToString());
                Directory.CreateDirectory(Incoming(p));
                foreach (var f in (b["files"] as JsonArray)!)
                {
                    var name = System.IO.Path.GetFileName(f!["name"]!.ToString());
                    var data = Convert.FromBase64String(f["data"]!.ToString());
                    var path = System.IO.Path.Combine(Incoming(p), Guid.NewGuid().ToString("N")[..6] + "_" + name);
                    File.WriteAllBytes(path, data);
                    var it = new BItem { Name = name, Path = path, Size = data.Length };
                    lock (batchLock) { Batch(p).RemoveAll(x => x.Name == name); Batch(p).Add(it); }
                }
                return J(BatchState(p));
            }
            case "POST /api/batch/remove":
            {
                var b = Body(r); var p = Store.Get(b["project"]!.ToString());
                var id = b["id"]?.ToString();
                lock (batchLock) { if (id == null) Batch(p).Clear(); else Batch(p).RemoveAll(x => x.Id == id); }
                return J(BatchState(p));
            }
            case "POST /api/batch/include":
            {
                var b = Body(r); var p = Store.Get(b["project"]!.ToString());
                var it = Batch(p).First(x => x.Id == b["id"]!.ToString()); it.Include = b["include"]!.GetValue<bool>();
                return J(BatchState(p));
            }
            case "POST /api/batch/analyze":
            {
                var b = Body(r); var p = Store.Get(b["project"]!.ToString());
                var job = Job.Start("batch-analyze", j => { Analyze(p, j); return BatchState(p); });
                return J(new JsonObject { ["job"] = job.Id });
            }
            case "POST /api/batch/target":
            {
                var b = Body(r); var p = Store.Get(b["project"]!.ToString());
                var it = Batch(p).First(x => x.Id == b["id"]!.ToString());
                if (b["newRel"] != null)
                {
                    var nr = b["newRel"]!.ToString().Trim();
                    it.NewRel = nr == "" ? null : p.CheckRel(nr); it.Target = null;
                    it.Content = it.NewRel != null ? "newfile" : ""; it.Status = it.NewRel != null ? "newfile" : "notfound";
                    return J(BatchState(p));
                }
                var key = b["key"]!.ToString();
                var c = it.Cands.FirstOrDefault(x => x.Key == key);
                if (c == null)
                {
                    var idx = IndexFor(p, null).FirstOrDefault(x => x.Key == key) ?? throw new Exception("Target not found");
                    c = Clone(idx); c.Score = Score(it, c); c.Why = "вибрано вручну"; it.Cands.Insert(0, c);
                }
                it.Target = c; it.NewRel = null;
                SetStatus(it);
                Compare(p, it);
                return J(BatchState(p));
            }
            case "GET /api/batch/find":
            {
                var p = Store.Get(r.Q("project")); var q = r.Q("q").Trim();
                var res = new JsonArray();
                foreach (var c in IndexFor(p, null).Where(x => q == "" || (x.Name + " " + x.Rel + " " + x.PathId).Contains(q, StringComparison.OrdinalIgnoreCase)).Take(200))
                    res.Add(c.ToJson());
                return J(res);
            }
            case "POST /api/batch/apply":
            {
                var b = Body(r); var p = Store.Get(b["project"]!.ToString());
                var mode = b["mode"]?.ToString() ?? "update";        // update | check
                bool add = b["add"]?.GetValue<bool>() ?? true;
                var lang = b["lang"]?.ToString() ?? "uk";
                var job = Job.Start("batch-apply", j => ApplyBatch(p, j, mode, add, lang));
                return J(new JsonObject { ["job"] = job.Id });
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ index of everything in the game
    static Cand Clone(Cand c) => new() { Rel = c.Rel, FileName = c.FileName, Name = c.Name, Type = c.Type, Script = c.Script, File = c.File, TypeId = c.TypeId, PathId = c.PathId, Loose = c.Loose };

    static readonly HashSet<string> LooseExt = new(StringComparer.OrdinalIgnoreCase) { ".json", ".txt", ".csv", ".tsv", ".xml", ".lang", ".loc", ".ini", ".yaml", ".yml", ".po", ".strings", ".cfg", ".properties", ".bytes" };
    static readonly HashSet<string> SkipExt = new(StringComparer.OrdinalIgnoreCase) { ".dll", ".exe", ".pdb", ".png", ".jpg", ".wav", ".ogg", ".mp4", ".webm", ".bank", ".ress", ".resource", ".resS", ".so", ".dylib", ".log", ".jpeg", ".tga", ".bmp", ".gif", ".psd", ".mp3", ".flac", ".aiff", ".avi", ".mov", ".mkv", ".usm", ".zip", ".7z", ".rar", ".exr", ".dds", ".ktx" };

    static List<Cand> IndexFor(Project p, Job? j)
    {
        Dictionary<string, (long stamp, List<Cand> items)> idx;
        lock (batchLock) { if (!indexes.TryGetValue(p.Id, out idx!)) indexes[p.Id] = idx = new(); }
        var files = Directory.EnumerateFiles(p.GameRoot, "*", SearchOption.AllDirectories).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < files.Count; i++)
        {
            j?.Checkpoint(); if (j != null) { j.Progress = 0.6 * i / files.Count; j.Message = "Індекс: " + System.IO.Path.GetFileName(files[i]); }
            var f = files[i]; var rel = System.IO.Path.GetRelativePath(p.GameRoot, f); seen.Add(rel);
            var ext = System.IO.Path.GetExtension(f);
            if (SkipExt.Contains(ext)) continue;
            var cur = p.Current(rel);
            long stamp = File.GetLastWriteTimeUtc(cur).Ticks ^ new FileInfo(cur).Length;
            lock (batchLock) if (idx.TryGetValue(rel, out var e) && e.stamp == stamp) continue;
            var list = new List<Cand>();
            var kind = UnityDoc.DetectKind(f);
            if (kind != UnityDoc.FileKind.None)
            {
                try
                {
                    using var d = new UnityDoc(cur, f);
                    foreach (var a in d.ListAssets())
                    {
                        if (a.TypeId is 1 or 4 or 224 or 222) continue;   // GameObject / Transform / RectTransform / CanvasRenderer
                        list.Add(new Cand { Rel = rel, File = a.File, PathId = a.PathId, TypeId = a.TypeId, Type = a.Type, Name = a.Name, Script = a.Script, FileName = System.IO.Path.GetFileName(d.Files[a.File].name) });
                    }
                }
                catch (Exception ex) { j?.Log($"! {rel}: {ex.Message}"); }
            }
            else if (LooseExt.Contains(ext) || new FileInfo(f).Length <= 32 << 20) list.Add(new Cand { Rel = rel, Name = System.IO.Path.GetFileName(f), Loose = true, Type = "File", FileName = System.IO.Path.GetFileName(f) });
            lock (batchLock) idx[rel] = (stamp, list);
        }
        lock (batchLock)
        {
            foreach (var k in idx.Keys.Where(k => !seen.Contains(k)).ToList()) idx.Remove(k);
            return idx.Values.SelectMany(v => v.items).ToList();
        }
    }

    // ------------------------------------------------------------------ analysis
    static readonly Regex UabeaCab = new(@"^(?<name>.*)-(?<file>CAB-[0-9A-Fa-f]+)-(?<pid>-?\d+)$");
    static readonly Regex UabeaName = new(@"^(?<name>.*)-(?<file>[^-]+)-(?<pid>-?\d+)$");

    static void Detect(BItem it)
    {
        var bytes = File.ReadAllBytes(it.Path);
        var ext = System.IO.Path.GetExtension(it.Name).ToLowerInvariant();
        var stem = System.IO.Path.GetFileNameWithoutExtension(it.Name);
        if (stem.EndsWith(".dump", StringComparison.OrdinalIgnoreCase)) stem = stem[..^5];
        var m = UabeaCab.Match(stem); if (!m.Success) m = UabeaName.Match(stem);
        it.NameHint = stem; it.FileHint = null; it.PidHint = null; it.TypeHint = null;
        if (m.Success) { it.NameHint = m.Groups["name"].Value; it.FileHint = m.Groups["file"].Value; it.PidHint = long.Parse(m.Groups["pid"].Value); }
        if (ext == ".dat") { it.Format = "raw"; return; }
        var text = Extract.LooksBinary(bytes) ? null : Extract.DecodeText(bytes);
        if (text == null) { it.Format = "binary"; return; }
        if (ext == ".json")
        {
            JsonNode? jn = null; try { jn = JsonNode.Parse(text); } catch { }
            if (jn is JsonObject jo)
            {
                if (jo["format"]?.ToString() == "translation-launcher") { it.Format = "tl"; return; }
                if (jo.ContainsKey("m_Name") && (jo.ContainsKey("m_Script") || jo.ContainsKey("m_GameObject") || jo.ContainsKey("m_Enabled")) || jo.ContainsKey("m_GameObject"))
                {
                    it.Format = "dump";
                    if (jo["m_Name"] is JsonValue nv && nv.ToString() != "") it.NameHint = nv.ToString();
                    it.TypeHint = jo["m_Script"] is JsonObject ? 114 : jo["m_Script"] is JsonValue ? 49 : null;
                    if (jo.ContainsKey("m_GameObject")) it.TypeHint = 114;
                    return;
                }
            }
            it.Format = jn != null ? "json" : "text"; return;
        }
        if (Dump.IsTextDump(text))   // UABEA "Export Dump" as .txt (any extension)
        {
            it.Format = "dump";
            var nm = System.Text.RegularExpressions.Regex.Match(text, @"^\s*\d string m_Name = ""(.*)""\r?$", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (nm.Success && nm.Groups[1].Value != "") it.NameHint = nm.Groups[1].Value;
            it.TypeHint = Dump.TextDumpType(text) switch { "MonoBehaviour" => 114, "TextAsset" => 49, _ => null };
            return;
        }
        if ((ext == ".txt" && text.StartsWith("#id\t")) || (ext == ".csv" && text.StartsWith("\"id\""))) { it.Format = "tl"; return; }
        it.Format = "text";
    }

    static double Score(BItem it, Cand c)
    {
        double s = c.Loose
            ? Math.Max(JsonMerge.Similarity(it.Name, c.Name), JsonMerge.Similarity(System.IO.Path.GetFileNameWithoutExtension(it.Name), System.IO.Path.GetFileNameWithoutExtension(c.Name)) - 0.02)
            : JsonMerge.Similarity(it.NameHint, c.Name);
        if (!c.Loose && it.FileHint != null && string.Equals(it.FileHint, c.FileName, StringComparison.OrdinalIgnoreCase))
        {
            s += 0.05;
            if (it.PidHint == c.PathId) s += 0.5;
        }
        return s;
    }

    static bool Compatible(BItem it, Cand c)
    {
        switch (it.Format)
        {
            case "dump": return !c.Loose && (it.TypeHint == null || c.TypeId == it.TypeHint);
            case "raw": return !c.Loose;
            case "binary": return c.Loose ? System.IO.Path.GetExtension(c.Name).Equals(System.IO.Path.GetExtension(it.Name), StringComparison.OrdinalIgnoreCase) : c.TypeId == 49;
            case "tl": return c.Loose || c.TypeId is 49 or 114;
            default: return c.Loose || c.TypeId is 49 or 114;   // text / json
        }
    }

    static void SetStatus(BItem it)
    {
        var c = it.Target;
        if (c == null) { it.Status = it.NewRel != null ? "newfile" : "notfound"; return; }
        bool nameEq = string.Equals(c.Loose ? System.IO.Path.GetFileName(it.Name) : it.NameHint, c.Name, StringComparison.OrdinalIgnoreCase)
                      || (c.Loose && string.Equals(System.IO.Path.GetFileNameWithoutExtension(it.Name), System.IO.Path.GetFileNameWithoutExtension(c.Name), StringComparison.OrdinalIgnoreCase));
        bool locEq = !c.Loose && it.FileHint != null && string.Equals(it.FileHint, c.FileName, StringComparison.OrdinalIgnoreCase) && it.PidHint == c.PathId;
        it.Status = nameEq && (locEq || it.FileHint == null || c.Loose) ? "exact" : nameEq ? "moved" : locEq ? "renamed" : "similar";
    }

    static void Analyze(Project p, Job j)
    {
        var items = Batch(p).ToList();
        j.Log($"Файлів: {items.Count}. Будую індекс гри…");
        var index = IndexFor(p, j);
        j.Log($"У грі ассетів і файлів: {index.Count}");
        for (int i = 0; i < items.Count; i++)
        {
            j.Checkpoint(); j.Progress = 0.6 + 0.4 * i / Math.Max(1, items.Count); j.Message = items[i].Name;
            var it = items[i];
            it.Error = null; it.Note = ""; it.Sample.Clear(); it.Stats = null; it.Content = ""; it.NewRel = null;
            try
            {
                Detect(it);
                it.Cands = index.Where(c => Compatible(it, c)).Select(c => { var x = Clone(c); x.Score = Score(it, x); return x; })
                    .Where(c => c.Score >= 0.55).OrderByDescending(c => c.Score).ThenBy(c => c.Rel).Take(8).ToList();
                foreach (var c in it.Cands)
                    c.Why = c.Score >= 1.5 ? "те саме місце (файл і PathID)" : c.Score >= 0.98 ? "та сама назва" : $"схожа назва ({Math.Round(Math.Min(1, c.Score) * 100)}%)";
                it.Target = it.Cands.FirstOrDefault();
                SetStatus(it);
                if (it.Cands.Count > 1 && it.Target != null && Math.Abs(it.Cands[1].Score - it.Target.Score) < 0.005)
                    it.Note = "Кілька однакових кандидатів — перевірте, чи вибрано правильний.";
                if (it.Target != null) Compare(p, it);
                j.Log($"{it.Name}: {it.Status}{(it.Target != null ? " → " + it.Target.Rel + " / " + it.Target.Name : "")} [{it.Content}]");
            }
            catch (Exception e) { it.Error = e.Message; it.Content = "error"; j.Log($"! {it.Name}: {e.Message}"); }
        }
    }

    // ------------------------------------------------------------------ comparison / merge
    sealed class Prep
    {
        public string Mode = "";           // json-asset | text-asset | loose | table | raw-only
        public JsonNode? Game, Trans;
        public JsonMerge.LineDoc? Lines;   // text kept as keyed lines
        public string? GameText;           // for JSON written back as text
        public bool AsJsonText;
        public EntryTable? Table; public List<int>? Cols;
        public byte[]? RawTrans; public bool RawSame;
        public Docs.Open O = null!;
    }

    static Prep Prepare(Project p, BItem it)
    {
        var c = it.Target!;
        var o = Docs.Get(p, c.Rel);
        var pr = new Prep { O = o };
        var bytes = File.ReadAllBytes(it.Path);
        if (it.Format is "dump" or "raw")
        {
            var u = o.Unity ?? throw new Exception("Ціль — не Unity-файл");
            var f = u.Field(c.File, c.PathId, out _);
            if (it.Format == "dump")
            {
                if (f == null) throw new Exception("Структура ассета в грі невідома (немає typetree/скриптів)");
                pr.Mode = "json-asset"; pr.Game = Dump.ToJson(f); pr.Trans = Dump.ParseAny(Extract.DecodeText(bytes)); return pr;
            }
            // raw .dat: read it with the game's layout; if that fails the binary comes from another version
            var cur = u.RawBytes(c.File, c.PathId);
            pr.RawTrans = bytes; pr.RawSame = cur.AsSpan().SequenceEqual(bytes);
            if (f != null)
            {
                try
                {
                    var inst = u.Files[c.File]; var info = u.Info(c.File, c.PathId);
                    var tpl = u.Am.GetTemplateBaseField(inst, info, AssetReadFlags.None);
                    using var ms = new MemoryStream(bytes);
                    var rd = new AssetsFileReader(ms) { BigEndian = inst.file.Reader.BigEndian };
                    var v = tpl.MakeValue(rd, 0, u.Am.GetRefTypeManager(inst));
                    if (ms.Position == bytes.Length) { pr.Mode = "json-asset"; pr.Game = Dump.ToJson(f); pr.Trans = Dump.ToJson(v); return pr; }
                }
                catch { }
            }
            pr.Mode = "raw-only"; return pr;
        }
        // text-like content against a TextAsset / loose file / localization table
        string? gameText = null;
        if (o.Unity == null) { gameText = o.LooseText ?? ""; pr.Mode = "loose"; }
        else
        {
            var t = GetTable(o, c.File, c.PathId);
            if (t.Kind == "text") { if (t.Binary) throw new Exception("TextAsset у грі бінарний — імпортуйте його як .dat"); gameText = t.Text ?? ""; pr.Mode = "text-asset"; }
            else
            {
                pr.Mode = "table"; pr.Table = t;
                var transText = Extract.DecodeText(bytes);
                pr.Game = TableToObj(t, t.Rows.Select(rw => (rw.Id, rw.Cells)).ToList(), t.Columns);
                var (cols, rows) = ParseRows(it.Name, transText, t);
                pr.Trans = TableToObj(t, rows, cols);
                return pr;
            }
        }
        var tt = Extract.DecodeText(bytes);
        if (it.Format == "tl")
        {
            try { var j = JsonNode.Parse(tt); if (j?["format"]?.ToString() == "translation-launcher") tt = j["text"]?.ToString() ?? ""; } catch { }
        }
        pr.GameText = gameText;
        JsonNode? gj = null, tj = null;
        try { gj = JsonNode.Parse(gameText); tj = JsonNode.Parse(tt); } catch { gj = tj = null; }
        if (gj is JsonObject or JsonArray && tj != null) { pr.AsJsonText = true; pr.Game = gj; pr.Trans = tj; return pr; }
        var sep = JsonMerge.DetectSep(gameText);
        pr.Lines = JsonMerge.ToLines(gameText, sep);
        pr.Game = pr.Lines.Obj; pr.Trans = JsonMerge.ToLines(tt, sep).Obj;
        return pr;
    }

    /// <summary>Localization table → {key: [editable cells]} keyed by the visible key (StringTable key / I2 term) — stable across versions.</summary>
    static JsonObject TableToObj(EntryTable t, List<(string id, List<string> cells)> rows, List<string> cols)
    {
        var o = new JsonObject();
        bool byKey = t.Kind is "stringtable" or "i2";
        foreach (var (id, cells) in rows)
        {
            var key = byKey && cells.Count > 0 ? cells[0] : id;
            var arr = new JsonArray();
            foreach (var ec in t.Editable)
            {
                int src = cols.IndexOf(t.Columns[ec]);
                if (src < 0 && cols == t.Columns) src = ec;
                arr.Add(src >= 0 && src < cells.Count ? cells[src] : null);
            }
            var k = key; int n = 2; while (o.ContainsKey(k)) k = key + " #" + n++;
            o[k] = arr;
        }
        return o;
    }

    /// <summary>Rows of an exported file (our TXT/CSV/JSON, a {key:text} map or "key&lt;TAB&gt;text" lines) with column names.</summary>
    static (List<string> cols, List<(string id, List<string> cells)> rows) ParseRows(string name, string content, EntryTable t)
    {
        var rows = new List<(string, List<string>)>();
        var cols = new List<string>(t.Columns);
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var j = JsonNode.Parse(content)!;
            if (j["rows"] is JsonArray ra)
            {
                cols = (j["columns"] as JsonArray)?.Select(x => x!.ToString()).ToList() ?? cols;
                foreach (var r in ra) rows.Add((r!["id"]!.ToString(), (r["cells"] as JsonArray)!.Select(x => x?.ToString() ?? "").ToList()));
            }
            else if (j is JsonObject map)
            {
                cols = new() { t.Columns[0], t.Columns[t.Editable[0]] };
                foreach (var kv in map) rows.Add((kv.Key, new() { kv.Key, kv.Value?.ToString() ?? "" }));
            }
            return (cols, rows);
        }
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            var csv = ParseCsvRows(content);
            if (csv.Count == 0) return (cols, rows);
            cols = csv[0].Skip(1).ToList();
            foreach (var l in csv.Skip(1)) if (l.Count > 1) rows.Add((l[0], l.Skip(1).ToList()));
            return (cols, rows);
        }
        bool header = lines.Count > 0 && lines[0].StartsWith("#id\t");
        if (header) cols = lines[0].Split('\t').Skip(1).Select(Extract.Unesc).ToList();
        foreach (var l in lines.Skip(header ? 1 : 0))
        {
            var parts = l.Split('\t').Select(Extract.Unesc).ToList();
            if (parts.Count < 2) continue;
            if (!header) { rows.Add((parts[0], new() { parts[0], parts[1] })); continue; }
            rows.Add((parts[0], parts.Skip(1).ToList()));
        }
        if (!header) cols = new() { t.Columns[0], t.Columns[t.Editable[0]] };
        return (cols, rows);
    }

    static List<List<string>> ParseCsvRows(string s)
    {
        var res = new List<List<string>>(); var cur = new List<string>(); var sb = new StringBuilder(); bool q = false;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (q) { if (ch == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(ch); }
            else if (ch == '"') q = true;
            else if (ch == ',') { cur.Add(sb.ToString()); sb.Clear(); }
            else if (ch == '\n') { cur.Add(sb.ToString().TrimEnd('\r')); sb.Clear(); res.Add(cur); cur = new(); }
            else sb.Append(ch);
        }
        if (sb.Length > 0 || cur.Count > 0) { cur.Add(sb.ToString()); res.Add(cur); }
        return res;
    }

    static void Compare(Project p, BItem it)
    {
        it.Sample.Clear(); it.Stats = null; it.Error = null;
        try
        {
            var pr = Prepare(p, it);
            if (pr.Mode == "raw-only")
            {
                it.Content = pr.RawSame ? "identical" : "unknown";
                if (!pr.RawSame) it.Sample.Add("Бінарний файл не читається структурою актуальної версії гри — ймовірно, він зі старої версії. Порівняти вміст неможливо; експортуйте його зі старої версії як JSON-дамп.");
                return;
            }
            if (JsonMerge.DeepEqual(pr.Game, pr.Trans)) { it.Content = "identical"; return; }
            var log = new MergeLog();
            JsonMerge.Merge(pr.Game, pr.Trans, "", log, true);
            it.Stats = log.Stats();
            bool same = JsonMerge.SameShape(pr.Game, pr.Trans);
            it.Content = same ? "translation" : "different";
            foreach (var x in log.Changed.Take(4)) it.Sample.Add($"~ {x.path}: «{x.oldV}» → «{x.newV}»");
            foreach (var x in log.NewInGame.Take(3)) it.Sample.Add($"+ лише в грі: {x.path}");
            foreach (var x in log.Added.Take(3)) it.Sample.Add($"− немає в актуальній версії: {x.path}");
            if (log.Order.Count > 0) it.Sample.Add("⇅ інший порядок: " + string.Join(", ", log.Order.Take(3)));
        }
        catch (Exception e) { it.Content = "error"; it.Error = e.Message; }
    }

    // ------------------------------------------------------------------ apply / report
    static JsonNode? ApplyBatch(Project p, Job j, string mode, bool add, string lang)
    {
        var items = Batch(p).Where(i => i.Include).ToList();
        var rep = new StringBuilder();
        bool update = mode == "update";
        rep.AppendLine($"ІМПОРТ ФАЙЛІВ У ГРУ — {(update ? "оновлення до актуальної версії" : "лише перевірка (файли гри не змінювались)")}");
        rep.AppendLine($"Гра: {p.Name} ({p.GameRoot})");
        rep.AppendLine($"Дата: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (update) rep.AppendLine($"Додавати записи, яких немає в актуальній версії: {(add ? "так" : "ні")}");
        rep.AppendLine(new string('═', 78));
        var touched = new HashSet<Docs.Open>(); var newFiles = new List<string>();
        int ok = 0, skipped = 0, failed = 0;
        for (int i = 0; i < items.Count; i++)
        {
            j.Checkpoint(); j.Progress = (double)i / Math.Max(1, items.Count); j.Message = items[i].Name;
            var it = items[i];
            rep.AppendLine();
            rep.AppendLine($"■ {it.Name}");
            try
            {
                if (it.Target == null)
                {
                    if (it.NewRel != null)
                    {
                        rep.AppendLine($"  Новий файл → {it.NewRel}");
                        if (update)
                        {
                            var dst = p.ModPath(it.NewRel); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
                            File.Copy(it.Path, dst, true); newFiles.Add(it.NewRel); rep.AppendLine("  ✔ додано до змінених файлів (буде вбудовано в гру разом з іншими)"); ok++;
                        }
                        else rep.AppendLine("  (перевірка: не додано)");
                    }
                    else { rep.AppendLine("  ✖ Місце в грі не знайдено — пропущено. Виберіть ціль вручну або вкажіть, куди додати файл."); skipped++; }
                    continue;
                }
                var c = it.Target;
                rep.AppendLine($"  Ціль: {c.Rel}" + (c.Loose ? "" : $"  ·  {c.Name} ({c.Type}, PathID {c.PathId}, {c.FileName})"));
                rep.AppendLine($"  Збіг: {StatusText(it.Status)}" + (c.Why != "" && !c.Why.StartsWith("схожа") ? $" — {c.Why}" : c.Why.StartsWith("схожа") ? $" ({Math.Round(Math.Min(1, c.Score) * 100)}%)" : ""));
                if (it.Status is "similar" or "renamed") rep.AppendLine($"  ⚠ УВАГА: назва файлу «{it.NameHint}» не збігається з «{c.Name}». Перевірте, що це той самий ассет (можливо, файл зі старої версії гри або перейменований).");
                if (it.Note != "") rep.AppendLine("  ⚠ " + it.Note);
                var pr = Prepare(p, it);
                if (pr.Mode == "raw-only")
                {
                    if (pr.RawSame) { rep.AppendLine("  = Вміст ідентичний — змін немає."); skipped++; continue; }
                    rep.AppendLine("  ✖ Бінарний файл іншої версії: структура не збігається з актуальною, автоматичне оновлення неможливе. Файл не змінено.");
                    rep.AppendLine("    Порада: експортуйте цей ассет зі старої версії як JSON-дамп (UABEA → Export Dump) і перетягніть його.");
                    failed++; continue;
                }
                if (JsonMerge.DeepEqual(pr.Game, pr.Trans)) { rep.AppendLine("  = Вміст ідентичний — змін немає."); skipped++; continue; }
                bool same = JsonMerge.SameShape(pr.Game, pr.Trans);
                var log = new MergeLog();
                bool canAdd = add && pr.Mode != "table";
                var merged = JsonMerge.Merge(pr.Game, pr.Trans, "", log, canAdd);
                // a full replace only when it is surely the same asset; otherwise texts come from the file, everything else from the game
                bool fullReplace = same && (pr.Mode != "json-asset" || it.Status == "exact");
                var result = fullReplace ? pr.Trans!.DeepClone() : merged;
                if (pr.Mode == "json-asset" && result is JsonObject ro && pr.Game is JsonObject go && go["m_Name"] != null) ro["m_Name"] = go["m_Name"]!.DeepClone();
                rep.AppendLine(same ? (update ? "  Структура однакова — відрізняється лише текст: файл замінено." : "  Структура однакова — відрізняється лише текст (при оновленні файл буде замінено).")
                                    : (update ? "  Структура відрізняється — дані приведено до актуальної версії (порядок як у грі)." : "  Структура відрізняється — при оновленні дані буде приведено до актуальної версії (порядок як у грі)."));
                if (pr.Mode == "table" && add && log.Dropped.Count > 0) rep.AppendLine("  (у таблиці локалізації нові ключі можна додати лише через JSON-дамп; такі записи перелічено нижче як «не перенесено»)");
                WriteLog(rep, log);
                if (!update) { ok++; continue; }
                Write(pr, c, result!);
                touched.Add(pr.O); ok++;
                rep.AppendLine("  ✔ Застосовано.");
            }
            catch (Exception e) { rep.AppendLine("  ✖ ПОМИЛКА: " + e.Message); failed++; AppLog.Error($"batch {it.Name}: {e}"); }
        }
        rep.AppendLine(); rep.AppendLine(new string('═', 78));
        rep.AppendLine($"Підсумок: оброблено {ok}, без змін/пропущено {skipped}, помилок {failed}.");

        var saved = new JsonArray();
        if (update)
        {
            var catalogs = new List<string>();
            foreach (var o in touched) if (o.Dirty) { foreach (var c in Docs.SaveDoc(o)) if (!catalogs.Contains(c)) catalogs.Add(c); saved.Add(o.Rel); }
            foreach (var c in catalogs) { saved.Add(c); rep.AppendLine($"Каталог Addressables оновлено (CRC скинуто): {c}"); }
            foreach (var nf in newFiles) saved.Add(nf);
            if (Store.Settings.ReplaceInPlace) foreach (var nf in newFiles) Store.ApplyToGame(p, nf);
        }
        Directory.CreateDirectory(p.Reports);
        var repName = $"import_{(update ? "update" : "check")}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
        File.WriteAllText(System.IO.Path.Combine(p.Reports, repName), rep.ToString(), new UTF8Encoding(true));
        AppLog.Info($"Batch import ({mode}): ok {ok}, skipped {skipped}, failed {failed}; report {repName}");
        if (update) lock (batchLock) Batch(p).RemoveAll(x => x.Include && failed == 0);
        return new JsonObject
        {
            ["report"] = repName, ["ok"] = ok, ["skipped"] = skipped, ["failed"] = failed, ["saved"] = saved,
            ["askApply"] = saved.Count > 0 && !Store.Settings.ReplaceInPlace && Store.Settings.AskBeforeReplace,
            ["applied"] = Store.Settings.ReplaceInPlace
        };
    }

    static string StatusText(string s) => s switch
    {
        "exact" => "точний (та сама назва і місце)", "moved" => "та сама назва, інше місце (інша версія гри?)",
        "renamed" => "те саме місце, інша назва", "similar" => "схожа назва", "newfile" => "новий файл", _ => "не знайдено"
    };

    static void WriteLog(StringBuilder sb, MergeLog log)
    {
        sb.AppendLine($"  Змінено текстів: {log.Changed.Count}; додано записів: {log.Added.Count}; нові в грі (лишились оригінальними): {log.NewInGame.Count}; не перенесено: {log.Dropped.Count}; інші значення (лишено з гри): {log.Kept.Count}");
        if (log.Order.Count > 0) { sb.AppendLine("  ⇅ Порядок записів відрізнявся — збережено порядок актуальної версії гри у:"); foreach (var x in log.Order) sb.AppendLine("      " + x); }
        if (log.Changed.Count > 0) { sb.AppendLine("  ~ ЗАМІНЕНО (місце: було → стало):"); foreach (var x in log.Changed) sb.AppendLine($"      {x.path}: «{x.oldV}» → «{x.newV}»"); }
        if (log.Added.Count > 0) { sb.AppendLine("  + ДОДАНО (записи з вашого файлу, яких немає в актуальній версії; місце — після вказаного запису):"); foreach (var x in log.Added) sb.AppendLine($"      {x.path}{(x.after != "" ? "  (після " + x.after + ")" : "  (на початку)")}: «{x.value}»"); }
        if (log.NewInGame.Count > 0) { sb.AppendLine("  ○ НОВЕ В ГРІ (у вашому файлі цього немає — лишилось оригінальне, потрібно перекласти):"); foreach (var x in log.NewInGame) sb.AppendLine($"      {x.path}: «{x.value}»"); }
        if (log.Dropped.Count > 0) { sb.AppendLine("  − НЕ ПЕРЕНЕСЕНО (є у вашому файлі, але немає в актуальній версії):"); foreach (var x in log.Dropped) sb.AppendLine($"      {x.path}: «{x.value}»"); }
        if (log.Kept.Count > 0) { sb.AppendLine("  · ІНШІ ЗНАЧЕННЯ (не текст; лишено значення з актуальної версії):"); foreach (var x in log.Kept.Take(200)) sb.AppendLine($"      {x.path}: гра «{x.game}», ваш файл «{x.trans}»"); if (log.Kept.Count > 200) sb.AppendLine($"      … ще {log.Kept.Count - 200}"); }
        if (log.Conflicts.Count > 0) { sb.AppendLine("  ! НЕСУМІСНІ МІСЦЯ (лишено з гри):"); foreach (var x in log.Conflicts) sb.AppendLine("      " + x); }
    }

    static void Write(Prep pr, Cand c, JsonNode result)
    {
        var o = pr.O;
        switch (pr.Mode)
        {
            case "json-asset":
            {
                var u = o.Unity!; var f = u.Field(c.File, c.PathId, out _)!;
                Dump.FromJson(f, result); u.MarkDirty(c.File, c.PathId); o.Tables.Remove((c.File, c.PathId));
                break;
            }
            case "table":
            {
                var t = pr.Table!; var changes = new List<Extract.Change>();
                var res = (JsonObject)result; bool byKey = t.Kind is "stringtable" or "i2";
                var used = new Dictionary<string, int>();
                foreach (var row in t.Rows)
                {
                    var key0 = byKey && row.Cells.Count > 0 ? row.Cells[0] : row.Id;
                    var k = key0; if (used.TryGetValue(key0, out var n)) { used[key0] = n + 1; k = key0 + " #" + (n + 1); } else used[key0] = 1;
                    if (res[k] is not JsonArray arr) continue;
                    for (int e = 0; e < t.Editable.Count && e < arr.Count; e++)
                    {
                        var v = arr[e]?.ToString(); if (v == null) continue;
                        if (row.Cells[t.Editable[e]] != v) changes.Add(new Extract.Change(row.Id, t.Editable[e], v));
                    }
                }
                ApplyChanges(o, c.File, c.PathId, changes, null);
                break;
            }
            default:   // TextAsset or loose text
            {
                string text = pr.AsJsonText ? JsonMerge.WriteJson(result, pr.GameText ?? "") : JsonMerge.FromLines((JsonObject)result, pr.Lines!);
                ApplyChanges(o, o.Unity == null ? -1 : c.File, c.PathId, new(), text);
                break;
            }
        }
    }
}

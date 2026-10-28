using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using TMPFontReplacer;
using TranslationLauncher.Core;

namespace TranslationLauncher;

public static partial class Api
{
    public static string Token = Guid.NewGuid().ToString("N");
    public static DateTime LastSeen = DateTime.UtcNow;
    public static bool Busy => Job.All.Values.Any(j => j.Status is "running" or "paused") || Docs.All().Any(d => d.Dirty);
    public static Action? OnExit;
    public const string Version = "1.2.0";
    // links of the «Донат» and «Bugs (Discord)» buttons — fixed by the developers, not editable in Settings
    public const string DonateUrl = "";
    public const string BugsUrl = "";

    static readonly JsonSerializerOptions JsOpt = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
    static HttpServer.Resp J(JsonNode n) => HttpServer.Resp.Json(n.ToJsonString(JsOpt));
    static HttpServer.Resp Ok(object? extra = null) => HttpServer.Resp.Json(JsonSerializer.Serialize(extra ?? new { ok = true }, JsOpt));
    static JsonNode Body(HttpServer.Req r) => JsonNode.Parse(r.Body.Length == 0 ? "{}" : r.BodyText)!;

    public static HttpServer.Resp Handle(HttpServer.Req r)
    {
        // ---------------- static UI
        if (!r.Path.StartsWith("/api/"))
        {
            var name = r.Path == "/" ? "index.html" : r.Path.TrimStart('/');
            // a file in app\www\ next to the program overrides the built-in one (e.g. i18n.js — the interface texts)
            var disk = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "www", name));
            Stream? res = disk.StartsWith(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "www"))) && File.Exists(disk)
                ? new MemoryStream(File.ReadAllBytes(disk))
                : Assembly.GetExecutingAssembly().GetManifestResourceStream("www." + name.Replace('/', '.'));
            if (res == null) return new HttpServer.Resp { Status = 404, ContentType = "text/plain", Body = Encoding.UTF8.GetBytes("not found") };
            using var ms = new MemoryStream(); res.CopyTo(ms);
            var type = name.EndsWith(".html") ? "text/html; charset=utf-8" : name.EndsWith(".js") ? "text/javascript; charset=utf-8" : name.EndsWith(".css") ? "text/css; charset=utf-8" : name.EndsWith(".svg") ? "image/svg+xml" : name.EndsWith(".png") ? "image/png" : "application/octet-stream";
            return HttpServer.Resp.Bytes(ms.ToArray(), type);
        }
        // ---------------- API (requires the session token: protects against other web pages calling localhost)
        var tok = r.Headers.TryGetValue("X-Token", out var t) ? t : r.Q("t");
        if (tok != Token) return HttpServer.Resp.Json("{\"error\":\"bad token\"}", 403);
        LastSeen = DateTime.UtcNow;
        if (r.Path.StartsWith("/api/batch")) { var br = BatchRoutes(r, r.Method + " " + r.Path); if (br != null) return br; }

        switch (r.Method + " " + r.Path)
        {
            case "GET /api/state": return J(State());
            case "GET /api/catalogs": return J(Store.CatalogStatus(Store.Get(r.Q("project"))));
            case "POST /api/catalogs/patch":
            {
                var p = Store.Get(Body(r)["project"]!.ToString());
                var done = Store.PatchAllCatalogs(p);
                return J(new JsonObject { ["patched"] = new JsonArray(done.Select(c => (JsonNode?)c).ToArray()) });
            }
            case "GET /api/pack": return J(PackPreview(Store.Get(r.Q("project"))));
            case "POST /api/pack": return J(Pack(Body(r)));
            case "POST /api/settings":
            {
                var b = Body(r);
                var s = JsonSerializer.Deserialize<Settings>(b.ToJsonString()) ?? new();
                s.WorkspaceRoot = Store.ResolveWorkspaceRoot(s.WorkspaceRoot); s.WorkspaceOwner = Store.Owner();
                Store.Settings = s; Store.Save();
                return J(State());
            }
            case "POST /api/projects/add":
            {
                var b = Body(r);
                var p = Store.Add(b["path"]!.ToString(), b["name"]?.ToString());
                return J(new JsonObject { ["id"] = p.Id });
            }
            case "POST /api/projects/export-dir":
            {
                var b = Body(r);
                var p = Store.Projects.FirstOrDefault(x => x.Id == b["id"]!.ToString()) ?? throw new Exception("Project not found");
                var dir = b["dir"]?.ToString() ?? "";
                if (dir != "") Directory.CreateDirectory(dir);
                p.ExportDir = dir; Store.Save();
                return J(new JsonObject { ["exports"] = p.Exports });
            }
            case "POST /api/projects/remove":
            {
                var id = Body(r)["id"]!.ToString();
                Store.Projects.RemoveAll(p => p.Id == id); Store.Save();
                return Ok();
            }
            case "GET /api/fs/list": return J(FsList(r.Q("path"), r.Q("files") == "1", r.Q("ext")));
            case "GET /api/game/list": return J(GameList(Store.Get(r.Q("project")), r.Q("rel")));
            case "POST /api/scan":
            {
                var b = Body(r); var p = Store.Get(b["project"]!.ToString());
                var kw = (b["keywords"] as JsonArray)?.Select(x => x!.ToString()).ToList() ?? Store.Settings.CustomKeywords;
                bool defs = b["useDefaults"]?.GetValue<bool>() ?? true, deep = b["deep"]?.GetValue<bool>() ?? true;
                var job = Job.Start("scan", j =>
                {
                    var hits = Scanner.Run(p.GameRoot, kw, defs, deep, j);
                    var arr = new JsonArray(hits.Select(h => (JsonNode?)h.ToJson()).ToArray());
                    File.WriteAllText(Path.Combine(p.WorkDir, "scan.json"), arr.ToJsonString(JsOpt));
                    j.Log($"Found: {hits.Count}");
                    return arr;
                });
                return J(new JsonObject { ["job"] = job.Id });
            }
            case "GET /api/scan/last":
            {
                var p = Store.Get(r.Q("project"));
                var f = Path.Combine(p.WorkDir, "scan.json");
                return File.Exists(f) ? HttpServer.Resp.Json(File.ReadAllText(f)) : HttpServer.Resp.Json("[]");
            }
            case "POST /api/findtext":
            {
                var b = Body(r); var p = Store.Get(b["project"]!.ToString()); var text = b["text"]!.ToString();
                var job = Job.Start("find", j => new JsonArray(Scanner.FindText(p.GameRoot, text, j).Select(h => (JsonNode?)h.ToJson()).ToArray()));
                return J(new JsonObject { ["job"] = job.Id });
            }
            case "GET /api/file/assets": return J(FileAssets(Store.Get(r.Q("project")), r.Q("rel")));
            case "GET /api/asset/table": return J(Table(r));
            case "POST /api/asset/edit": return J(Edit(Body(r)));
            case "POST /api/asset/replacelang": return J(ReplaceLang(Body(r)));
            case "POST /api/dll/langscan":
            {
                var p = Store.Get(Body(r)["project"]!.ToString());
                var job = Job.Start("dllscan", j =>
                {
                    var managed = GameInfo.FindManaged(p.GameRoot);
                    var res = new JsonObject { ["managed"] = managed == null ? null : Path.GetRelativePath(p.GameRoot, managed), ["il2cpp"] = managed == null && GameInfo.FindIl2Cpp(p.GameRoot) != null };
                    var hits = new List<DllLangs.Hit>();
                    if (managed != null)
                    {
                        var dlls = DllLangs.GameDlls(managed);
                        for (int i = 0; i < dlls.Count; i++)
                        {
                            j.Checkpoint(); j.Progress = (double)i / dlls.Count; j.Message = Path.GetFileName(dlls[i]);
                            var rel = Path.GetRelativePath(p.GameRoot, dlls[i]);
                            try { hits.AddRange(DllLangs.Scan(File.ReadAllBytes(p.Current(rel)), rel, managed)); }
                            catch (Exception e) { j.Log($"{rel}: {e.Message}"); }
                        }
                    }
                    j.Log($"Found: {hits.Count}");
                    res["hits"] = new JsonArray(hits.OrderBy(h => h.Kind == "code").ThenBy(h => h.Kind != "enum").Select(h => (JsonNode?)h.ToJson()).ToArray());
                    return res;
                });
                return J(new JsonObject { ["job"] = job.Id });
            }
            case "POST /api/dll/langadd":
            {
                var b = Body(r); var p = Store.Get(b["project"]!.ToString());
                var id = b["id"]!.ToString(); var value = b["value"]?.ToString().Trim() ?? "";
                if (value == "") throw new Exception("Empty value");
                var rel = p.CheckRel(id.Split('|')[1]);
                var managed = GameInfo.FindManaged(p.GameRoot) ?? throw new Exception("Managed folder not found");
                var data = DllLangs.Add(File.ReadAllBytes(p.Current(rel)), id, value, managed);
                Store.BackupOriginal(p, rel);
                var outPath = p.ModPath(rel);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                File.WriteAllBytes(outPath, data);
                AppLog.Info($"Language \"{value}\" added in code: {id}");
                if (Store.Settings.ReplaceInPlace) Store.ApplyToGame(p, rel);
                return J(new JsonObject
                {
                    ["saved"] = new JsonArray(rel), ["applied"] = Store.Settings.ReplaceInPlace,
                    ["askApply"] = !Store.Settings.ReplaceInPlace && Store.Settings.AskBeforeReplace
                });
            }
            case "POST /api/asset/addlang":
            {
                var b = Body(r);
                var (o, file, pid) = Asset(b["project"]!.ToString(), b["rel"]!.ToString(), b["file"]!.ToString(), b["pathId"]!.ToString());
                if (o.Unity == null) throw new Exception("Not a Unity asset");
                int n = Extract.AddLanguage(o.Unity, file, pid, b["name"]?.ToString() ?? "Ukrainian", b["code"]?.ToString() ?? "uk",
                    int.TryParse(b["copyFrom"]?.ToString(), out var cf) ? cf : -1);
                o.Tables.Remove((file, pid));
                AppLog.Info($"Language added to {o.Rel} #{pid}: {n} rows");
                return J(new JsonObject { ["applied"] = n, ["dirty"] = o.Dirty });
            }
            case "GET /api/asset/dump":
            {
                var (o, file, pid) = Asset(r.Q("project"), r.Q("rel"), r.Q("file"), r.Q("pathId"));
                var f = o.Unity!.Field(file, pid, out var how) ?? throw new Exception("Layout unknown: export raw .dat instead");
                return HttpServer.Resp.Json(Dump.ToJsonText(f));
            }
            case "GET /api/asset/export": return Export(r);
            case "POST /api/asset/import": return J(Import(Body(r)));
            case "POST /api/save": return J(Save(Body(r)));
            case "POST /api/close":
            {
                var b = Body(r); var p = Store.Get(b["project"]!.ToString());
                Docs.Close(p, p.CheckRel(b["rel"]!.ToString()));
                return Ok();
            }
            case "POST /api/apply":
            {
                var b = Body(r); var p = Store.Get(b["project"]!.ToString());
                var rels = (b["rels"] as JsonArray)?.Select(x => x!.ToString()).ToList() ?? Store.ChangedFiles(p).Select(x => x!["rel"]!.ToString()).ToList();
                foreach (var rel in rels) Store.ApplyToGame(p, rel);
                return J(new JsonObject { ["applied"] = rels.Count });
            }
            case "POST /api/restore":
            {
                var b = Body(r); var p = Store.Get(b["project"]!.ToString());
                var rels = (b["rels"] as JsonArray)?.Select(x => x!.ToString()).ToList()
                    ?? (Directory.Exists(p.Original) ? Directory.EnumerateFiles(p.Original, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(p.Original, f)).ToList() : new());
                foreach (var rel in rels) { Docs.Close(p, rel); Store.RestoreOriginal(p, rel); }
                return J(new JsonObject { ["restored"] = rels.Count });
            }
            case "POST /api/discard":
            {
                var b = Body(r); var p = Store.Get(b["project"]!.ToString()); var rel = p.CheckRel(b["rel"]!.ToString());
                Docs.Close(p, rel);
                if (b["deleteModified"]?.GetValue<bool>() == true && File.Exists(p.ModPath(rel))) File.Delete(p.ModPath(rel));
                return Ok();
            }
            case "GET /api/changed": return J(Store.ChangedFiles(Store.Get(r.Q("project"))));
            case "GET /api/log": return J(new JsonObject { ["items"] = AppLog.Since(int.Parse(r.Q("from", "0"))), ["count"] = AppLog.Count, ["errors"] = AppLog.Errors });
            case "POST /api/log/clear": AppLog.Clear(); return Ok();
            case "POST /api/fonts/scan": return J(FontScan(Body(r)));
            case "POST /api/fonts/upload":
            {
                var b = Body(r); var p = Store.Get(b["project"]!.ToString());
                var name = Path.GetFileName(b["name"]!.ToString());
                var path = Path.Combine(p.Fonts, name);
                Directory.CreateDirectory(p.Fonts);
                File.WriteAllBytes(path, Convert.FromBase64String(b["data"]!.ToString()));
                return J(new JsonObject { ["path"] = path });
            }
            case "POST /api/fonts/run": return J(FontRun(Body(r)));
            case "GET /api/fonts/atlas": return FontAtlas(r);
            case "GET /api/fonts/ttf": return FontTtf(r);
            case "GET /api/fonts/ttfs":
            {
                var p = Store.Get(r.Q("project"));
                var arr = new JsonArray();
                if (Directory.Exists(p.Fonts)) foreach (var f in Directory.GetFiles(p.Fonts, "*.ttf")) arr.Add(f);
                return J(arr);
            }
            case "GET /api/report":
            {
                var p = Store.Get(r.Q("project"));
                var f = Path.GetFullPath(Path.Combine(p.Reports, Path.GetFileName(r.Q("name"))));
                return File.Exists(f) ? HttpServer.Resp.Bytes(File.ReadAllBytes(f), f.EndsWith(".png") ? "image/png" : "text/plain; charset=utf-8") : new HttpServer.Resp { Status = 404 };
            }
            case "POST /api/open-folder":
            {
                var b = Body(r); var p = Store.Get(b["project"]!.ToString());
                var which = b["which"]?.ToString() ?? "work";
                var dir = which switch { "game" => p.GameRoot, "original" => p.Original, "modified" => p.Modified, "exports" => p.Exports, "reports" => p.Reports, _ => p.WorkDir };
                Directory.CreateDirectory(dir);
                if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
                return J(new JsonObject { ["dir"] = dir });
            }
            case "POST /api/exit":
            {
                var force = Body(r)["force"]?.GetValue<bool>() ?? false;
                var dirty = Docs.All().Where(d => d.Dirty).Select(d => d.Rel).ToList();
                if (dirty.Count > 0 && !force) return J(new JsonObject { ["dirty"] = new JsonArray(dirty.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) });
                Task.Run(async () => { await Task.Delay(400); OnExit?.Invoke(); });
                return Ok();
            }
            case "POST /api/heartbeat": return Ok();
            case "POST /api/open-url":
            {
                var u = Body(r)["url"]?.ToString()?.Trim() ?? "";
                if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) throw new Exception("Invalid link: " + u);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return Ok();
            }
        }
        if (r.Path.StartsWith("/api/jobs/"))
        {
            var parts = r.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var job = Job.All[int.Parse(parts[2])];
            if (parts.Length == 4)
            {
                if (parts[3] == "pause") job.Pause(); else if (parts[3] == "resume") job.Resume(); else if (parts[3] == "cancel") job.Cancel();
            }
            return J(job.ToJson(int.Parse(r.Q("log", "0"))));
        }
        return HttpServer.Resp.Json("{\"error\":\"unknown endpoint\"}", 404);
    }

    // ------------------------------------------------------------------ handlers
    static JsonObject State()
    {
        var s = JsonSerializer.SerializeToNode(Store.Settings)!.AsObject();
        var projects = new JsonArray();
        foreach (var p in Store.Projects)
            projects.Add(new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["gameRoot"] = p.GameRoot, ["workDir"] = p.WorkDir, ["exportDir"] = p.ExportDir, ["exports"] = p.Exports, ["exists"] = Directory.Exists(p.GameRoot) });
        var open = new JsonArray();
        foreach (var d in Docs.All()) open.Add(new JsonObject { ["project"] = d.P.Id, ["rel"] = d.Rel, ["dirty"] = d.Dirty });
        var jobs = new JsonArray();
        foreach (var j in Job.All.Values.Where(j => j.Status is "running" or "paused")) jobs.Add(new JsonObject { ["id"] = j.Id, ["kind"] = j.Kind, ["status"] = j.Status, ["progress"] = j.Progress, ["message"] = j.Message });
        return new JsonObject
        {
            ["version"] = Version, ["settings"] = s, ["projects"] = projects, ["open"] = open, ["jobs"] = jobs, ["errors"] = AppLog.Errors,
            ["links"] = new JsonObject { ["donate"] = DonateUrl, ["bugs"] = BugsUrl }, ["defaultWorkspaceRoot"] = Store.DefaultWorkspaceRoot(),
            ["dataDir"] = Store.DataDir, ["os"] = Environment.OSVersion.ToString(), ["arch"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ["defaultKeywords"] = new JsonArray(Scanner.DefaultWords.Concat(Scanner.LanguageNames).Concat(Scanner.LanguageCodes).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())
        };
    }

    static JsonObject FsList(string path, bool files, string ext)
    {
        var res = new JsonObject();
        var items = new JsonArray();
        if (string.IsNullOrEmpty(path))
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady)) items.Add(new JsonObject { ["name"] = d.Name, ["path"] = d.RootDirectory.FullName, ["dir"] = true });
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var sp in new[] { home, Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
                if (!string.IsNullOrEmpty(sp) && Directory.Exists(sp)) items.Add(new JsonObject { ["name"] = "★ " + sp, ["path"] = sp, ["dir"] = true });
            res["path"] = ""; res["parent"] = null;
        }
        else
        {
            var di = new DirectoryInfo(path);
            res["path"] = di.FullName; res["parent"] = di.Parent?.FullName ?? "";
            try
            {
                foreach (var d in di.EnumerateDirectories().OrderBy(d => d.Name)) if ((d.Attributes & FileAttributes.Hidden) == 0) items.Add(new JsonObject { ["name"] = d.Name, ["path"] = d.FullName, ["dir"] = true });
                if (files)
                    foreach (var f in di.EnumerateFiles().OrderBy(f => f.Name))
                        if (string.IsNullOrEmpty(ext) || ext.Split(',').Any(e => f.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                            items.Add(new JsonObject { ["name"] = f.Name, ["path"] = f.FullName, ["dir"] = false, ["size"] = f.Length });
            }
            catch (Exception e) { res["error"] = e.Message; }
        }
        res["items"] = items;
        return res;
    }

    static readonly HashSet<string> UnityExt = new(StringComparer.OrdinalIgnoreCase) { ".assets", ".bundle", ".unity3d", ".ab", ".asset", "" };

    static JsonObject GameList(Project p, string rel)
    {
        var dir = p.GamePath(rel ?? "");
        var items = new JsonArray();
        var di = new DirectoryInfo(dir);
        foreach (var d in di.EnumerateDirectories().OrderBy(d => d.Name))
            items.Add(new JsonObject { ["name"] = d.Name, ["rel"] = Path.GetRelativePath(p.GameRoot, d.FullName), ["dir"] = true });
        foreach (var f in di.EnumerateFiles().OrderBy(f => f.Name))
        {
            var r = Path.GetRelativePath(p.GameRoot, f.FullName);
            bool likely = UnityExt.Contains(f.Extension) || f.Name.StartsWith("level") || f.Name.StartsWith("sharedassets") || f.Name == "globalgamemanagers";
            items.Add(new JsonObject { ["name"] = f.Name, ["rel"] = r, ["dir"] = false, ["size"] = f.Length, ["unity"] = likely, ["modified"] = File.Exists(p.ModPath(r)) });
        }
        return new JsonObject { ["rel"] = Path.GetRelativePath(p.GameRoot, dir), ["items"] = items };
    }

    static JsonObject FileAssets(Project p, string rel)
    {
        var o = Docs.Get(p, rel);
        if (o.Unity == null) return new JsonObject { ["loose"] = true, ["rel"] = o.Rel };
        var u = o.Unity;
        var arr = new JsonArray();
        foreach (var a in u.ListAssets())
            arr.Add(new JsonObject { ["file"] = a.File, ["pathId"] = a.PathId.ToString(), ["typeId"] = a.TypeId, ["type"] = a.Type, ["name"] = a.Name, ["script"] = a.Script, ["size"] = a.Size });
        return new JsonObject
        {
            ["rel"] = o.Rel, ["unityVersion"] = u.UnityVersion, ["typeSource"] = u.TypeSource,
            ["container"] = u.Legacy != null ? "Legacy bundle " + u.Legacy.Signature : u.Bundle != null ? "UnityFS bundle (" + u.Bundle.originalCompression + ")" : "Serialized file",
            ["inner"] = new JsonArray(u.Files.Select(f => (JsonNode?)JsonValue.Create(f.name)).ToArray()),
            ["source"] = File.Exists(p.ModPath(o.Rel)) ? "modified" : "game",
            ["assets"] = arr, ["dirty"] = o.Dirty
        };
    }

    static (Docs.Open o, int file, long pid) Asset(string project, string rel, string file, string pathId)
    {
        var p = Store.Get(project);
        var o = Docs.Get(p, rel);
        return (o, int.Parse(file), long.Parse(pathId));
    }

    static EntryTable GetTable(Docs.Open o, int file, long pid, bool refresh = false)
    {
        if (o.Unity == null)
            return new EntryTable { Kind = "loose", Info = "Text file", Text = o.LooseText };
        if (!refresh && o.Tables.TryGetValue((file, pid), out var t)) return t;
        t = Extract.Build(o.Unity, file, pid, sid => SharedKeys(o, sid));
        o.Tables[(file, pid)] = t;
        return t;
    }

    /// <summary>Unity Localization: keys live in SharedTableData, often in another bundle next to this one.</summary>
    static Dictionary<long, string>? SharedKeys(Docs.Open o, long sharedPathId)
    {
        Dictionary<long, string>? FromDoc(UnityDoc d)
        {
            for (int fi = 0; fi < d.Files.Count; fi++)
            {
                var info = d.Files[fi].file.GetAssetInfo(sharedPathId);
                if (info == null) continue;
                var f = d.Field(fi, sharedPathId, out _);
                if (f == null || f["m_Entries"].IsDummy) continue;
                return f["m_Entries"]["Array"].Children.ToDictionary(e => e["m_Id"].AsLong, e => e["m_Key"].AsString);
            }
            return null;
        }
        var own = FromDoc(o.Unity!);
        if (own != null) return own;
        var dir = Path.GetDirectoryName(o.P.GamePath(o.Rel))!;
        var cands = Directory.GetFiles(dir).OrderByDescending(f => Path.GetFileName(f).Contains("shared", StringComparison.OrdinalIgnoreCase)).Take(200);
        foreach (var f in cands)
        {
            if (string.Equals(f, o.P.GamePath(o.Rel), StringComparison.OrdinalIgnoreCase)) continue;
            if (UnityDoc.DetectKind(f) == UnityDoc.FileKind.None) continue;
            try
            {
                var rel = Path.GetRelativePath(o.P.GameRoot, f);
                using var d = new UnityDoc(o.P.Current(rel), f);
                var k = FromDoc(d);
                if (k != null) return k;
            }
            catch { }
        }
        return null;
    }

    static JsonObject Table(HttpServer.Req r)
    {
        var p = Store.Get(r.Q("project"));
        var o = Docs.Get(p, r.Q("rel"));
        int file = int.Parse(r.Q("file", "-1")); long pid = long.Parse(r.Q("pathId", "0"));
        var t = GetTable(o, file, pid, r.Q("refresh") == "1");
        var rep = LangMap(p)[LangKey(o.Rel, file, pid)] as JsonObject;
        Func<Core.Row, bool>? where = null;
        if (rep?["pattern"]?.ToString() is { Length: > 0 } pat) { var rx = new System.Text.RegularExpressions.Regex(pat); where = row => rx.IsMatch(row.Id); }
        var j = t.ToJson(int.Parse(r.Q("offset", "0")), int.Parse(r.Q("limit", "500")), r.Q("filter"), where, int.Parse(r.Q("sort", "-1")), r.Q("desc") == "1");
        if (rep != null)
        {
            j["replace"] = new JsonObject { ["col"] = rep["col"]!.GetValue<int>(), ["lang"] = rep["lang"]!.GetValue<int>(), ["name"] = rep["name"]!.ToString() };
            var orig = rep["orig"] as JsonObject;
            foreach (var row in (JsonArray)j["rows"]!) row!["orig"] = orig?[row["id"]!.ToString()]?.ToString() ?? "";
        }
        j["dirty"] = o.Dirty;
        return j;
    }

    // ------------------------------------------------------------------ "replace a language": Ukrainian text goes into an existing language column
    static string LangKey(string rel, int file, long pid) => $"{rel.ToLowerInvariant()}|{file}|{pid}";
    static string LangFile(Project p) => Path.Combine(p.WorkDir, "languages.json");
    static JsonObject LangMap(Project p)
    {
        try { if (File.Exists(LangFile(p)) && JsonNode.Parse(File.ReadAllText(LangFile(p))) is JsonObject m) return m; } catch { }
        return new JsonObject();
    }

    /// <summary>Remember which language column of a table holds the Ukrainian text and keep a copy of its original text to show next to it.</summary>
    static JsonObject ReplaceLang(JsonNode b)
    {
        var p = Store.Get(b["project"]!.ToString());
        var o = Docs.Get(p, b["rel"]!.ToString());
        int file = int.Parse(b["file"]!.ToString()); long pid = long.Parse(b["pathId"]!.ToString());
        var map = LangMap(p); var key = LangKey(o.Rel, file, pid);
        if (b["off"]?.GetValue<bool>() == true) map.Remove(key);
        else
        {
            var t = GetTable(o, file, pid);
            if (t.Languages == null || t.Languages.Count == 0) throw new Exception("This table has no language columns");
            int lang = b["lang"]!.GetValue<int>();
            if (lang < 0 || lang >= t.Languages.Count) throw new Exception("Unknown language");
            int col = t.Kind == "i2" ? lang + 1 : t.Editable.FirstOrDefault(1);
            string? pattern = t.Kind == "generic" ? Extract.LangRowPattern(t.LangWhere, lang) : null;
            // original text: from the untouched copy in original/ if there is one, otherwise from the file as it is now
            var src = t;
            if (o.Unity != null && File.Exists(p.OrigPath(o.Rel)))
                try { using var od = new UnityDoc(p.OrigPath(o.Rel), p.GamePath(o.Rel), null, AppLog.Warn); src = Extract.Build(od, file, pid, sid => SharedKeys(o, sid)); } catch (Exception e) { AppLog.Warn("Original table not read: " + e.Message); }
            var rx = pattern != null ? new System.Text.RegularExpressions.Regex(pattern) : null;
            var orig = new JsonObject();
            foreach (var row in src.Rows) if ((rx == null || rx.IsMatch(row.Id)) && col < row.Cells.Count) orig[row.Id] = row.Cells[col];
            map[key] = new JsonObject { ["col"] = col, ["lang"] = lang, ["name"] = t.Languages[lang], ["pattern"] = pattern, ["orig"] = orig };
            AppLog.Info($"Ukrainian replaces {t.Languages[lang]} in {o.Rel} #{pid}");
        }
        Directory.CreateDirectory(p.WorkDir);
        File.WriteAllText(LangFile(p), map.ToJsonString(JsOpt));
        return new JsonObject { ["ok"] = true };
    }

    static JsonObject Edit(JsonNode b)
    {
        var p = Store.Get(b["project"]!.ToString());
        var o = Docs.Get(p, b["rel"]!.ToString());
        int file = int.Parse(b["file"]?.ToString() ?? "-1"); long pid = long.Parse(b["pathId"]?.ToString() ?? "0");
        var changes = (b["changes"] as JsonArray)?.Select(c => new Extract.Change(c!["id"]!.ToString(), c["col"]!.GetValue<int>(), c["value"]?.ToString() ?? "")).ToList() ?? new();
        string? text = b["text"]?.ToString();
        int n = ApplyChanges(o, file, pid, changes, text);
        return new JsonObject { ["applied"] = n, ["dirty"] = o.Dirty };
    }

    static int ApplyChanges(Docs.Open o, int file, long pid, List<Extract.Change> changes, string? text)
    {
        if (o.Unity == null)
        {
            if (text != null && text != o.LooseText) { o.LooseText = text; o.LooseDirty = true; return 1; }
            return 0;
        }
        var t = GetTable(o, file, pid);
        int n = Extract.Apply(o.Unity, file, pid, t, changes, text);
        // keep the cached table in sync
        if (t.Kind == "text" && text != null) t.Text = text;
        else foreach (var c in changes)
        {
            var row = t.Rows.FirstOrDefault(x => x.Id == c.Id);
            if (row != null && c.Col < row.Cells.Count) row.Cells[c.Col] = c.Value;
        }
        if (t.Kind == "raw" && n > 0) o.Tables.Remove((file, pid));   // offsets changed
        return n;
    }

    static string SafeName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    static HttpServer.Resp Export(HttpServer.Req r)
    {
        var p = Store.Get(r.Q("project"));
        var o = Docs.Get(p, r.Q("rel"));
        int file = int.Parse(r.Q("file", "-1")); long pid = long.Parse(r.Q("pathId", "0"));
        var fmt = r.Q("format", "json");
        string baseName;
        byte[] data; string type = "text/plain; charset=utf-8";
        if (o.Unity == null)
        {
            baseName = Path.GetFileNameWithoutExtension(o.Rel);
            var t = GetTable(o, file, pid);
            data = Encoding.UTF8.GetBytes(fmt == "json" ? Extract.ExportJson(t) : t.Text ?? "");
        }
        else
        {
            var u = o.Unity;
            var name = UnityDoc.ReadName(u.Files[file], u.Info(file, pid));
            baseName = $"{SafeName(name == "" ? "asset" : name)}-{SafeName(u.Files[file].name)}-{pid}";
            switch (fmt)
            {
                case "dat": data = u.RawBytes(file, pid); type = "application/octet-stream"; break;
                case "dump":
                    var f = u.Field(file, pid, out _) ?? throw new Exception("Layout unknown — use .dat");
                    data = Encoding.UTF8.GetBytes(Dump.ToJsonText(f)); break;
                case "txt": data = Encoding.UTF8.GetBytes(Extract.ExportTxt(GetTable(o, file, pid))); break;
                case "csv": data = Encoding.UTF8.GetBytes("﻿" + Extract.ExportCsv(GetTable(o, file, pid))); break;
                default: data = Encoding.UTF8.GetBytes(Extract.ExportJson(GetTable(o, file, pid))); break;
            }
        }
        var ext = fmt switch { "dat" => ".dat", "dump" => ".dump.json", "txt" => ".txt", "csv" => ".csv", _ => ".json" };
        var fname = baseName + ext;
        Directory.CreateDirectory(p.Exports);
        var saved = Path.Combine(p.Exports, fname);
        File.WriteAllBytes(saved, data);
        AppLog.Info($"Exported: {saved}");
        var resp = HttpServer.Resp.Bytes(data, type, fname);
        resp.Headers["X-Saved-To"] = Uri.EscapeDataString(saved);
        return resp;
    }

    static JsonObject Import(JsonNode b)
    {
        var p = Store.Get(b["project"]!.ToString());
        var name = b["name"]!.ToString();
        var data = Convert.FromBase64String(b["data"]!.ToString());
        string rel = b["rel"]?.ToString() ?? "";
        int file = int.Parse(b["file"]?.ToString() ?? "-1"); long pid = long.Parse(b["pathId"]?.ToString() ?? "0");
        // file name like UABEA: Name-CAB-xxxx-123456.ext → find target by path id among open docs
        if (rel == "")
        {
            var m = System.Text.RegularExpressions.Regex.Match(name, @"-(-?\d+)(\.dump)?\.(json|txt|dat|csv)$");
            if (!m.Success) throw new Exception("Select an asset first (or use exported file names: Name-File-PathID.ext)");
            pid = long.Parse(m.Groups[1].Value);
            var hit = Docs.All().Where(d => d.P.Id == p.Id && d.Unity != null)
                .SelectMany(d => d.Unity!.Files.Select((f, i) => (d, i, f))).FirstOrDefault(x => x.f.file.GetAssetInfo(pid) != null);
            if (hit.d == null) throw new Exception("Target asset is not open");
            rel = hit.d.Rel; file = hit.i;
        }
        var o = Docs.Get(p, rel);
        int n;
        if (o.Unity != null && name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
        {
            o.Unity.ReplaceRaw(file, pid, data); o.Tables.Remove((file, pid)); n = 1;
        }
        else
        {
            var text = Extract.DecodeText(data);
            bool isDump = false;
            if (o.Unity != null && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                try { var jn = JsonNode.Parse(text); isDump = jn is JsonObject jo && jo["format"]?.ToString() != "translation-launcher" && (jo.ContainsKey("m_Name") || jo.ContainsKey("m_GameObject") || name.Contains(".dump.")); } catch { }
            }
            if (o.Unity != null && Dump.IsTextDump(text)) isDump = true;   // UABEA text dump (.txt)
            if (isDump)
            {
                var f = o.Unity!.Field(file, pid, out _) ?? throw new Exception("Layout unknown");
                Dump.FromJson(f, Dump.ParseAny(text));
                o.Unity.MarkDirty(file, pid); o.Tables.Remove((file, pid)); n = 1;
            }
            else
            {
                var t = GetTable(o, file, pid);
                var (changes, newText) = Extract.ParseImport(t, name, text);
                n = ApplyChanges(o, file, pid, changes, newText);
            }
        }
        AppLog.Info($"Imported {name} → {rel} ({n} changes)");
        return new JsonObject { ["applied"] = n, ["rel"] = rel, ["file"] = file, ["pathId"] = pid.ToString(), ["dirty"] = o.Dirty };
    }

    static JsonObject Save(JsonNode b)
    {
        var p = Store.Get(b["project"]!.ToString());
        var rel = b["rel"]?.ToString();
        var targets = Docs.All().Where(d => d.P.Id == p.Id && d.Dirty && (rel == null || string.Equals(d.Rel, p.CheckRel(rel), StringComparison.OrdinalIgnoreCase))).ToList();
        var saved = new JsonArray();
        var catalogs = new List<string>();
        foreach (var d in targets) { foreach (var c in Docs.SaveDoc(d)) if (!catalogs.Contains(c)) catalogs.Add(c); saved.Add(d.Rel); }
        foreach (var c in catalogs) saved.Add(c);
        return new JsonObject
        {
            ["catalogs"] = new JsonArray(catalogs.Select(c => (JsonNode?)c).ToArray()),
            ["saved"] = saved,
            ["askApply"] = saved.Count > 0 && !Store.Settings.ReplaceInPlace && Store.Settings.AskBeforeReplace,
            ["applied"] = Store.Settings.ReplaceInPlace
        };
    }

    // ------------------------------------------------------------------ archive for other players (step 6)
    /// <summary>Translated files to share: everything in modified/ under the game's &lt;Name&gt;_Data folder, at its game path.</summary>
    static JsonObject PackPreview(Project p)
    {
        var files = new JsonArray(); var skipped = new JsonArray(); string? data = null; long total = 0;
        foreach (var f in Store.ChangedFiles(p).Select(n => n!.AsObject()))
        {
            var rel = f["rel"]!.ToString().Replace('\\', '/');
            var top = rel.Split('/')[0];
            if (!top.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)) { skipped.Add(rel); continue; }
            data ??= top;
            long size = f["size"]!.GetValue<long>(); total += size;
            files.Add(new JsonObject { ["rel"] = rel, ["size"] = size, ["catalog"] = Catalog.IsCatalogName(Path.GetFileName(rel)) });
        }
        data ??= Directory.Exists(p.GameRoot) ? Directory.GetDirectories(p.GameRoot, "*_Data").Select(Path.GetFileName).FirstOrDefault() : null;
        data ??= p.Name + "_Data";
        bool catalogNeed = Store.CatalogStatus(p).Any(c => c!["crc"]!.GetValue<int>() > 0)
                           && files.Any(f => f!["rel"]!.ToString().EndsWith(".bundle", StringComparison.OrdinalIgnoreCase));
        return new JsonObject { ["name"] = data + ".zip", ["dataDir"] = data, ["files"] = files, ["skipped"] = skipped, ["total"] = total, ["catalogNeed"] = catalogNeed, ["exports"] = p.Exports };
    }

    static JsonObject Pack(JsonNode b)
    {
        var p = Store.Get(b["project"]!.ToString());
        var job = Job.Start("pack", j =>
        {
            var pv = PackPreview(p);
            var files = pv["files"]!.AsArray().Select(n => n!["rel"]!.ToString()).ToList();
            if (files.Count == 0) throw new Exception("Немає змінених файлів у папці " + pv["dataDir"]);
            Directory.CreateDirectory(p.Exports);
            var outPath = Path.Combine(p.Exports, pv["name"]!.ToString());
            var tmp = outPath + ".tmp";
            long total = Math.Max(1, pv["total"]!.GetValue<long>()), done = 0;
            j.Log($"Збираю архів {Path.GetFileName(outPath)}: файлів {files.Count}, {total / 1048576.0:0.#} МБ");
            try
            {
                using (var fs = File.Create(tmp))
                using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
                {
                    var buf = new byte[1 << 20];
                    foreach (var rel in files)
                    {
                        j.Checkpoint(); j.Message = rel;
                        var e = zip.CreateEntry(rel, System.IO.Compression.CompressionLevel.Optimal);
                        var src = p.ModPath(rel);
                        e.LastWriteTime = File.GetLastWriteTime(src);
                        using var input = File.OpenRead(src); using var output = e.Open();
                        int n;
                        while ((n = input.Read(buf, 0, buf.Length)) > 0) { j.Checkpoint(); output.Write(buf, 0, n); done += n; j.Progress = (double)done / total; }
                        j.Log("  + " + rel);
                    }
                }
            }
            catch { try { File.Delete(tmp); } catch { } throw; }
            File.Move(tmp, outPath, true);
            j.Log("Готово: " + outPath);
            return new JsonObject { ["path"] = outPath, ["files"] = files.Count, ["size"] = new FileInfo(outPath).Length };
        });
        return new JsonObject { ["job"] = job.Id };
    }

    // ------------------------------------------------------------------ fonts
    public static readonly string[] FontDefaultWords = { "SDF", "Atlas", "Font" };

    static JsonObject FontScan(JsonNode b)
    {
        var p = Store.Get(b["project"]!.ToString());
        bool useDef = b["useDefault"]?.GetValue<bool>() ?? true;
        var words = new List<string>();
        if (useDef) words.AddRange(FontDefaultWords);
        if (b["words"] is JsonArray wa) foreach (var w in wa) { var x = w?.ToString().Trim(); if (!string.IsNullOrEmpty(x) && !words.Contains(x, StringComparer.OrdinalIgnoreCase)) words.Add(x); }
        bool orphans = b["orphans"]?.GetValue<bool>() ?? false;
        bool byWords = (b["mode"]?.ToString() ?? "all") == "words";
        if (byWords && words.Count == 0) throw new Exception("Add at least one keyword for the keyword search");
        var job = Job.Start("fonts-scan", j =>
        {
            var res = new JsonArray();
            j.Log("Шукаю пари MonoBehaviour (шрифт) + Texture2D (атлас)" + (byWords ? "; лише зі словами: " + string.Join(", ", words) : "; усі пари"));
            var files = Directory.EnumerateFiles(p.GameRoot, "*", SearchOption.AllDirectories).ToList();
            int pairs = 0;
            for (int i = 0; i < files.Count; i++)
            {
                j.Checkpoint(); j.Progress = (double)i / files.Count;
                var f = files[i]; var rel = Path.GetRelativePath(p.GameRoot, f); j.Message = rel;
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is ".ttf" or ".otf")
                {
                    // loose font file in the game folder: listed for export/preview only
                    var fn = Path.GetFileNameWithoutExtension(f);
                    var hit = words.Where(w => fn.Contains(w, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (!byWords || hit.Count > 0)
                        res.Add(new JsonObject { ["rel"] = rel, ["name"] = fn, ["kind"] = "TTF", ["chars"] = 0, ["file"] = 0, ["pathId"] = "", ["script"] = "", ["matched"] = string.Join(", ", hit), ["link"] = "ttf" });
                    continue;
                }
                if (ext is ".dll" or ".exe" or ".ress" or ".resource" or ".png" or ".wav" or ".ogg" or ".json" or ".txt" or ".pdb" or ".mp4" or ".bank") continue;
                if (UnityDoc.DetectKind(f) == UnityDoc.FileKind.None) continue;
                try
                {
                    using var d = new UnityDoc(p.Current(rel), f);
                    var rows = d.ListAssets().ToList();
                    var texes = rows.Where(a => a.TypeId == (int)AssetClassID.Texture2D && a.Name != "").ToList();
                    var paired = new HashSet<(int, long)>();
                    List<string> Hits(params string[] names) => words.Where(w => names.Any(n => n.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
                    JsonObject TexJson(int file, long pid)
                    {
                        var ti = Atlas.Info(d, file, pid);
                        return new JsonObject { ["name"] = ti?.Name ?? "", ["w"] = ti?.Width ?? 0, ["h"] = ti?.Height ?? 0, ["format"] = ti?.FormatName ?? "?", ["pathId"] = pid.ToString(), ["file"] = file };
                    }

                    foreach (var a in rows)
                    try
                    {
                        if (a.TypeId == (int)AssetClassID.Font)
                        {
                            var fld = d.Field(a.File, a.PathId, out _);
                            var fdat = fld == null || fld["m_FontData"].IsDummy ? null : fld["m_FontData"]["Array"];
                            int n = fdat == null ? 0 : fdat.Value?.ValueType == AssetValueType.ByteArray ? fdat.AsByteArray.Length : fdat.Children.Count;
                            if (n > 0 && (!byWords || Hits(a.Name).Count > 0)) { var r0 = Row(rel, a, "Unity Font", 0, Hits(a.Name)); r0["link"] = "ttf"; res.Add(r0); }
                            continue;
                        }
                        if (a.TypeId != (int)AssetClassID.MonoBehaviour || a.Size <= 200 || a.Name == "") continue;

                        // texture paired by name: "<font> Atlas" (best) or identical name
                        UnityDoc.AssetRow? nameTex = null; int best = 0;
                        foreach (var t in texes) { int sc = FontPairs.Match(a.Name, t.Name); if (sc > best) { best = sc; nameTex = t; } }
                        bool fontScript = a.Script.EndsWith("FontAsset");
                        var mbHits = Hits(a.Name);
                        // keyword mode: the word may be in the font name or in its atlas name
                        if (byWords && mbHits.Count == 0 && (nameTex == null || Hits(nameTex.Name).Count == 0)) continue;
                        // unknown script (no type info): big MonoBehaviours are tried as TMP fonts too
                        bool tryUnknown = a.Script == "" && (a.Size > 1000 || mbHits.Count > 0);
                        if (!fontScript && nameTex == null && !tryUnknown && !(byWords && mbHits.Count > 0)) continue;
                        string? wordTex = null;
                        if (nameTex == null && byWords && mbHits.Count > 0)
                        {
                            // pair by the user's word: the texture with the same word and the longest common start of the name
                            var cand = texes.Where(t => Hits(t.Name).Intersect(mbHits).Any()).OrderByDescending(t => FontPairs.CommonPrefix(a.Name, t.Name)).FirstOrDefault();
                            if (cand != null) { nameTex = cand; wordTex = cand.Name; }
                        }

                        var fldm = d.Field(a.File, a.PathId, out _);
                        bool modern = fldm != null && !fldm["m_GlyphTable"].IsDummy && !fldm["m_CharacterTable"].IsDummy;
                        bool legacy = fldm != null && !fldm["m_glyphInfoList"].IsDummy;
                        var ptr = (modern || legacy) ? Atlas.FontAtlases(d, a.File, a.PathId) : new List<Atlas.TexRef>();

                        if (!modern && !legacy)
                        {
                            // layout known but it is not a font (e.g. TMP sprite asset) → skip; unknown layout → trust the name pair
                            if (fldm != null || nameTex == null) continue;
                            if (best < 2 && wordTex == null && !a.Script.Contains("Font", StringComparison.OrdinalIgnoreCase)) continue;
                        }
                        if ((modern || legacy) && ptr.Count == 0 && nameTex == null)
                        { j.Log($"! {rel}: «{a.Name}» — шрифт без атласу, пропущено"); continue; }

                        string kind = modern ? "TMP" : legacy ? "TMP legacy" : "TMP?";
                        int cnt = modern ? fldm!["m_CharacterTable"]["Array"].Children.Count : legacy ? fldm!["m_glyphInfoList"]["Array"].Children.Count : 0;
                        var atl = new JsonArray(); bool ptrHasNameTex = false;
                        foreach (var tr in ptr)
                        {
                            if (tr.File >= 0)
                            {
                                paired.Add((tr.File, tr.PathId));
                                if (nameTex != null && tr.File == nameTex.File && tr.PathId == nameTex.PathId) ptrHasNameTex = true;
                                atl.Add(TexJson(tr.File, tr.PathId));
                            }
                            else atl.Add(new JsonObject { ["name"] = Path.GetFileName(tr.ExternalPath), ["external"] = tr.ExternalPath, ["pathId"] = tr.PathId.ToString() });
                        }
                        if (atl.Count == 0 && nameTex != null) { atl.Add(TexJson(nameTex.File, nameTex.PathId)); paired.Add((nameTex.File, nameTex.PathId)); }
                        else if (nameTex != null) paired.Add((nameTex.File, nameTex.PathId));

                        var row = Row(rel, a, kind, cnt, Hits(a.Name, nameTex?.Name ?? ""));
                        row["atlases"] = atl;
                        row["link"] = ptr.Count > 0 ? (ptrHasNameTex ? (wordTex != null ? "ref+word" : "ref+name") : "ref") : (wordTex != null ? "word" : "name");
                        row["pairTex"] = nameTex?.Name ?? (atl.Count > 0 ? atl[0]!["name"]?.ToString() : "");
                        res.Add(row); pairs++;
                    }
                    catch (Exception e) { j.Log($"! {rel} #{a.PathId} {a.Name}: {e.Message}"); }

                    if (orphans)
                        foreach (var t in texes)
                        {
                            if (paired.Contains((t.File, t.PathId)) || Hits(t.Name).Count == 0) continue;
                            var ti = Atlas.Info(d, t.File, t.PathId);
                            if (ti == null || ti.Width * ti.Height <= 1) continue;
                            var row = Row(rel, t, "Texture2D", 0, Hits(t.Name));
                            row["atlases"] = new JsonArray(TexJson(t.File, t.PathId)); row["link"] = "none";
                            res.Add(row);
                        }
                }
                catch (Exception e) { j.Log($"! {rel}: {e.Message}"); }
            }
            j.Log($"Знайдено шрифтів (пар шрифт + атлас): {pairs}; усього рядків: {res.Count}");
            return res;
        });
        return new JsonObject { ["job"] = job.Id };
    }

    static JsonObject Row(string rel, UnityDoc.AssetRow a, string kind, int chars, List<string> matched) => new()
    {
        ["rel"] = rel, ["name"] = a.Name, ["kind"] = kind, ["chars"] = chars, ["file"] = a.File, ["pathId"] = a.PathId.ToString(),
        ["script"] = a.Script, ["matched"] = string.Join(", ", matched)
    };

    // single cached document for atlas previews (re-opened when the file changes)
    static readonly object atlasLock = new();
    static (string key, UnityDoc doc)? atlasDoc;
    static UnityDoc AtlasDoc(string real, string logical)
    {
        var key = real + "|" + File.GetLastWriteTimeUtc(real).Ticks;
        if (atlasDoc?.key == key) return atlasDoc.Value.doc;
        atlasDoc?.doc.Dispose();
        var d = new UnityDoc(real, logical);
        atlasDoc = (key, d);
        return d;
    }

    /// <summary>TTF/OTF bytes of a legacy Unity Font (m_FontData) or of a loose font file; save=1 writes it to the export folder.</summary>
    static HttpServer.Resp FontTtf(HttpServer.Req r)
    {
        var p = Store.Get(r.Q("project"));
        var rel = p.CheckRel(r.Q("rel"));
        byte[] data; string name;
        if (r.Q("pathId", "") == "") { data = File.ReadAllBytes(p.Current(rel)); name = Path.GetFileNameWithoutExtension(rel); }
        else
        {
            int file = int.Parse(r.Q("file", "0")); long pid = long.Parse(r.Q("pathId"));
            lock (atlasLock)
            {
                var d = AtlasDoc(p.Current(rel), p.GamePath(rel));
                var fld = d.Field(file, pid, out _) ?? throw new Exception("Font layout unknown");
                var arr = fld["m_FontData"]["Array"];
                if (arr.IsDummy) throw new Exception("No font data in this asset");
                data = arr.Value?.ValueType == AssetValueType.ByteArray ? arr.AsByteArray : arr.Children.Select(c => c.AsByte).ToArray();
                name = fld["m_Name"].IsDummy ? "font" : fld["m_Name"].AsString;
            }
        }
        if (data.Length == 0) throw new Exception("No font data in this asset");
        var fname = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))
            + (data.Length > 4 && data[0] == 'O' && data[1] == 'T' && data[2] == 'T' && data[3] == 'O' ? ".otf" : ".ttf");
        if (r.Q("save") == "1")
        {
            Directory.CreateDirectory(p.Exports);
            var outp = Path.Combine(p.Exports, fname); File.WriteAllBytes(outp, data);
            AppLog.Info("Font exported: " + outp);
            return J(new JsonObject { ["path"] = outp });
        }
        return HttpServer.Resp.Bytes(data, "font/ttf", fname);
    }

    static HttpServer.Resp FontAtlas(HttpServer.Req r)
    {
        var p = Store.Get(r.Q("project"));
        var rel = p.CheckRel(r.Q("rel"));
        int file = int.Parse(r.Q("file", "0")); long pid = long.Parse(r.Q("pathId")); int idx = int.Parse(r.Q("i", "0"));
        lock (atlasLock)
        {
            bool orig = r.Q("src") == "orig";
            var real = orig ? (File.Exists(p.OrigPath(rel)) ? p.OrigPath(rel) : p.GamePath(rel)) : p.Current(rel);
            var d = AtlasDoc(real, p.GamePath(rel));
            var info = d.Info(file, pid);
            UnityDoc td = d; int tf = file; long tp = pid; int count = 1; string fontName = "";
            if (info.TypeId != (int)AssetClassID.Texture2D)
            {
                fontName = UnityDoc.ReadName(d.Files[file], info);
                var list = Atlas.FontAtlases(d, file, pid);
                if (list.Count == 0)
                {
                    var pair = TMPFontReplacer.FontPairs.FindAtlas(d.Files, fontName);
                    if (pair != null) list.Add(new Atlas.TexRef(d.Files.IndexOf(pair.Value.inst), pair.Value.info.PathId, null));
                }
                if (list.Count == 0) throw new Exception("Atlas texture not found for this font");
                count = list.Count; var tr = list[Math.Clamp(idx, 0, list.Count - 1)];
                if (tr.File >= 0) { tf = tr.File; tp = tr.PathId; }
                else
                {
                    var name = Path.GetFileName(tr.ExternalPath!.Replace('\\', '/'));
                    var extGame = Path.Combine(Path.GetDirectoryName(p.GamePath(rel))!, name);
                    if (!File.Exists(extGame)) throw new Exception("Atlas is in another file: " + tr.ExternalPath);
                    var extRel = Path.GetRelativePath(p.GameRoot, extGame);
                    var extReal = orig && File.Exists(p.OrigPath(extRel)) ? p.OrigPath(extRel) : p.Current(extRel);
                    td = new UnityDoc(extReal, extGame); tf = 0; tp = tr.PathId;
                }
            }
            try
            {
                var ti = Atlas.Info(td, tf, tp);
                var (rgba, w, h, fmt, alphaOnly) = Atlas.Decode(td, tf, tp, Path.GetDirectoryName(p.GamePath(rel)));
                var png = Atlas.ToPng(rgba, w, h, alphaOnly);
                if (r.Q("save") == "1")
                {
                    Directory.CreateDirectory(p.Exports);
                    var safe = string.Concat((ti?.Name ?? "atlas").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                    var outp = Path.Combine(p.Exports, safe + ".png"); File.WriteAllBytes(outp, png);
                    AppLog.Info("Atlas saved: " + outp);
                    return J(new JsonObject { ["path"] = outp });
                }
                var resp = HttpServer.Resp.Bytes(png, "image/png");
                resp.Headers["X-Atlas"] = Uri.EscapeDataString(new JsonObject
                {
                    ["name"] = ti?.Name ?? "", ["w"] = w, ["h"] = h, ["format"] = Atlas.FormatName(fmt), ["count"] = count, ["index"] = Math.Clamp(idx, 0, count - 1),
                    ["font"] = fontName, ["source"] = orig ? "original" : p.Current(rel) == p.ModPath(rel) ? "modified" : "game",
                    ["hasModified"] = File.Exists(p.ModPath(rel))
                }.ToJsonString());
                resp.Headers["Cache-Control"] = "no-store";
                return resp;
            }
            finally { if (td != d) td.Dispose(); }
        }
    }

    static JsonObject FontRun(JsonNode b)
    {
        var p = Store.Get(b["project"]!.ToString());
        var rel = p.CheckRel(b["rel"]!.ToString());
        var open = Docs.All().FirstOrDefault(d => d.P.Id == p.Id && string.Equals(d.Rel, rel, StringComparison.OrdinalIgnoreCase));
        if (open != null && open.Dirty) throw new Exception("File has unsaved edits — save it first: " + rel);
        var o = new Options
        {
            TtfPath = b["ttf"]!.ToString().Trim().Trim('"'),
            AssetFile = p.Current(rel), LogicalPath = p.GamePath(rel), OutFile = p.ModPath(rel), ReportDir = p.Reports,
            FontName = b["font"]!.ToString(), NoBackup = true,
            FontPathId = long.TryParse(b["pathId"]?.ToString(), out var fpid) ? fpid : null,
            ManagedDir = GameInfo.FindManaged(p.GameRoot),
        };
        if (int.TryParse(b["size"]?.ToString(), out var sz) && sz > 0) o.PointSize = sz;
        if (int.TryParse(b["padding"]?.ToString(), out var pd) && pd > 0) o.Padding = pd;
        var atlas = b["atlas"]?.ToString();
        if (!string.IsNullOrWhiteSpace(atlas) && atlas.Contains('x')) { var wh = atlas.Split('x'); o.AtlasW = int.Parse(wh[0]); o.AtlasH = int.Parse(wh[1]); }
        o.ExtraChars = b["chars"]?.ToString();
        var cf = b["charsFile"]?.ToString(); if (!string.IsNullOrWhiteSpace(cf)) o.CharsFile = cf.Trim().Trim('"');
        o.OnlyOriginalChars = b["onlyOriginal"]?.GetValue<bool>() ?? false;
        o.ForceBitmap = b["bitmap"]?.GetValue<bool>() ?? false;
        o.NoKerning = b["noKerning"]?.GetValue<bool>() ?? false;
        var job = Job.Start("font", j =>
        {
            if (!File.Exists(o.TtfPath)) throw new Exception("TTF not found: " + o.TtfPath);
            Docs.Close(p, rel);
            Store.BackupOriginal(p, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(o.OutFile!)!); Directory.CreateDirectory(p.Reports);
            var fp = new FontPatcher(o, s => j.Log(s));
            fp.Run();
            if (Store.Settings.ReplaceInPlace) Store.ApplyToGame(p, rel);
            var saved = new List<string> { rel }; saved.AddRange(Store.PatchCatalogs(p, rel, s => j.Log(s)));
            return new JsonObject
            {
                ["rel"] = rel, ["out"] = o.OutFile, ["saved"] = new JsonArray(saved.Select(x => (JsonNode?)x).ToArray()),
                ["reportPng"] = fp.LastReportPng != null ? Path.GetFileName(fp.LastReportPng) : null,
                ["reportTxt"] = fp.LastReportTxt != null ? Path.GetFileName(fp.LastReportTxt) : null,
                ["askApply"] = !Store.Settings.ReplaceInPlace && Store.Settings.AskBeforeReplace
            };
        });
        return new JsonObject { ["job"] = job.Id };
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using TranslationLauncher.Core;

namespace TranslationLauncher;

public sealed class Settings
{
    public string Language { get; set; } = "uk";
    public string Theme { get; set; } = "dark";
    public bool BackupOriginals { get; set; } = true;     // copy untouched game files to <workspace>/original
    public bool ReplaceInPlace { get; set; } = false;     // write changed files straight into the game folder
    public bool AskBeforeReplace { get; set; } = true;    // after saving, ask whether to copy into the game folder
    public string WorkspaceRoot { get; set; } = "";       // "" = default (Documents of the current user), see Store.DefaultWorkspaceRoot
    public string WorkspaceOwner { get; set; } = "";      // hash of PC + user that chose WorkspaceRoot (paths from another PC are dropped)
    public List<string> CustomKeywords { get; set; } = new();
    public bool UseDefaultKeywords { get; set; } = true;
    public bool DeepScan { get; set; } = true;
    public List<string> FontKeywords { get; set; } = new();
    public bool FontUseDefault { get; set; } = true;
    public bool FontOrphanTextures { get; set; } = false;
    public string FontSearchMode { get; set; } = "all";
    public bool WelcomeShown { get; set; } = false;
    public Dictionary<string, string> Hotkeys { get; set; } = new();   // only the changed ones; "" = no key (defaults: HOTKEYS in app.js)
}

public sealed class Project
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string GameRoot { get; set; } = "";
    public string WorkDir { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public string Original => Path.Combine(WorkDir, "original");
    public string Modified => Path.Combine(WorkDir, "modified");
    public string ExportDir { get; set; } = "";   // chosen by the user per project; empty = WorkDir/exports
    public string Exports => string.IsNullOrWhiteSpace(ExportDir) ? Path.Combine(WorkDir, "exports") : ExportDir;
    public string Reports => Path.Combine(WorkDir, "reports");
    public string Fonts => Path.Combine(WorkDir, "fonts");

    public string GamePath(string rel) => Path.GetFullPath(Path.Combine(GameRoot, rel));
    public string ModPath(string rel) => Path.GetFullPath(Path.Combine(Modified, rel));
    public string OrigPath(string rel) => Path.GetFullPath(Path.Combine(Original, rel));
    /// <summary>Latest version of a file: the edited copy if there is one, else the game file.</summary>
    public string Current(string rel) => File.Exists(ModPath(rel)) ? ModPath(rel) : GamePath(rel);

    public string CheckRel(string rel)
    {
        var full = GamePath(rel);
        if (!full.StartsWith(Path.GetFullPath(GameRoot), StringComparison.OrdinalIgnoreCase)) throw new Exception("Path outside game folder");
        return Path.GetRelativePath(GameRoot, full);
    }
}

/// <summary>Projects, settings and the original/modified file workflow.</summary>
public static class Store
{
    public static string DataDir = "";
    public static Settings Settings = new();
    public static List<Project> Projects = new();
    static readonly JsonSerializerOptions jo = new() { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };

    public static void Load(string dataDir)
    {
        DataDir = dataDir; Directory.CreateDirectory(dataDir);
        try { Settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(dataDir, "settings.json"))) ?? new(); } catch { }
        try { Projects = JsonSerializer.Deserialize<List<Project>>(File.ReadAllText(Path.Combine(dataDir, "projects.json"))) ?? new(); } catch { }
        // paths chosen on another PC / by another user are not reused; settings from v1.2 (no owner yet) keep a folder only if it exists here
        bool legacy = Settings.WorkspaceOwner == "", foreign = !legacy && Settings.WorkspaceOwner != Owner();
        if (foreign)
        {
            // the program folder was copied from someone else's PC: start clean, nothing of theirs is kept
            Settings = new(); Projects = new();
            foreach (var f in new[] { "projects.json", "instance.json", "log.txt" }) try { File.Delete(Path.Combine(dataDir, f)); } catch { }
            AppLog.Clear();
            try { Directory.Delete(Path.Combine(dataDir, "window-profile"), true); } catch { }
            AppLog.Info("Data from another PC/user found → started with clean settings");
        }
        // the old product name in the default folder → the new default (existing projects keep their folders)
        if (Settings.WorkspaceRoot == LegacyWorkspaceRoot()) Settings.WorkspaceRoot = "";
        bool Here(string? dir) => !foreign && UsableDir(dir) && (!legacy || Directory.Exists(dir));
        if (!string.IsNullOrEmpty(Settings.WorkspaceRoot) && !Here(Settings.WorkspaceRoot))
            AppLog.Warn($"Work files folder is from another PC ({Settings.WorkspaceRoot}) → default");
        Settings.WorkspaceRoot = ResolveWorkspaceRoot(Here(Settings.WorkspaceRoot) ? Settings.WorkspaceRoot : "");
        // projects carried over from another PC: their work folders are re-created under this user's workspace
        foreach (var p in Projects)
            if (!Here(p.WorkDir))
            {
                var name = Path.GetFileName(p.WorkDir.TrimEnd('\\', '/'));
                var moved = Path.Combine(Settings.WorkspaceRoot, string.IsNullOrEmpty(name) ? p.Name + "_" + p.Id : name);
                AppLog.Warn($"Work folder of project {p.Name} is from another PC ({p.WorkDir}) → {moved}");
                p.WorkDir = moved;
                foreach (var d in new[] { p.Original, p.Modified, p.Exports, p.Reports, p.Fonts }) try { Directory.CreateDirectory(d); } catch { }
            }
        Settings.WorkspaceOwner = Owner();
    }

    /// <summary>Anonymous id of this PC + Windows user (no names are stored).</summary>
    public static string Owner() =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Environment.MachineName + "|" + Environment.UserName)))[..16];

    /// <summary>Documents\Tlumach\Workspaces of the user running the program.</summary>
    public static string DefaultWorkspaceRoot()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(docs)) docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(docs)) return Path.Combine(DataDir, "Workspaces");
        return Path.Combine(docs, "Tlumach", "Workspaces");
    }
    /// <summary>Default folder of versions before the rename (Unity Translation Launcher).</summary>
    static string LegacyWorkspaceRoot()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrEmpty(docs) ? "\0" : Path.Combine(docs, "Unity Translation Launcher", "Workspaces");
    }

    /// <summary>The chosen folder, or the default one when it is empty or belongs to another PC / user.</summary>
    public static string ResolveWorkspaceRoot(string? path)
    {
        path = path?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(path) && UsableDir(path)) return Path.GetFullPath(path);
        if (!string.IsNullOrEmpty(path)) AppLog.Warn($"Work files folder is not available on this PC ({path}) → default");
        return DefaultWorkspaceRoot();
    }

    /// <summary>An absolute folder that exists or can be created here, and is not inside another user's profile.</summary>
    static bool UsableDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var me = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var users = string.IsNullOrEmpty(me) ? null : Path.GetDirectoryName(me);
            if (users != null && full.StartsWith(users + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !(full + Path.DirectorySeparatorChar).StartsWith(me + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(Path.Combine(users, "Public") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            if (Directory.Exists(full)) return true;
            var root = Path.GetPathRoot(full);
            return !string.IsNullOrEmpty(root) && Directory.Exists(root);
        }
        catch { return false; }
    }

    public static void Save()
    {
        // the default folder is stored as "" so a copied settings.json never pins another PC's Documents path
        var s = JsonSerializer.SerializeToNode(Settings)!.AsObject();
        if (string.Equals(Settings.WorkspaceRoot, DefaultWorkspaceRoot(), StringComparison.OrdinalIgnoreCase)) s["WorkspaceRoot"] = "";
        File.WriteAllText(Path.Combine(DataDir, "settings.json"), s.ToJsonString(jo));
        File.WriteAllText(Path.Combine(DataDir, "projects.json"), JsonSerializer.Serialize(Projects, jo));
    }

    public static Project Get(string id) => Projects.FirstOrDefault(p => p.Id == id) ?? throw new Exception("Project not found");

    public static Project Add(string gameRoot, string? name)
    {
        gameRoot = Path.GetFullPath(gameRoot.Trim().Trim('"'));
        if (!Directory.Exists(gameRoot)) throw new Exception("Folder not found: " + gameRoot);
        var p = new Project { GameRoot = gameRoot, Name = string.IsNullOrWhiteSpace(name) ? new DirectoryInfo(gameRoot).Name : name.Trim() };
        var safe = string.Concat(p.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        p.WorkDir = Path.Combine(Settings.WorkspaceRoot, safe + "_" + p.Id);
        foreach (var d in new[] { p.Original, p.Modified, p.Exports, p.Reports, p.Fonts }) Directory.CreateDirectory(d);
        Projects.Add(p); Save();
        AppLog.Info($"Project added: {p.Name} → {p.GameRoot}");
        return p;
    }

    // ------------------------------------------------------------------ original / modified workflow
    /// <summary>Copy the untouched game file into original/ (once).</summary>
    public static void BackupOriginal(Project p, string rel)
    {
        if (!Settings.BackupOriginals) return;
        var o = p.OrigPath(rel);
        if (File.Exists(o)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(o)!);
        File.Copy(p.GamePath(rel), o);
        File.SetAttributes(o, FileAttributes.Normal);
        AppLog.Info($"Original saved: {rel}");
    }

    public static void ApplyToGame(Project p, string rel)
    {
        var m = p.ModPath(rel);
        if (!File.Exists(m)) throw new Exception("No edited copy: " + rel);
        BackupOriginal(p, rel);
        var g = p.GamePath(rel);
        if (File.Exists(g)) File.SetAttributes(g, FileAttributes.Normal);
        File.Copy(m, g, true);
        AppLog.Info($"Applied to game: {rel}");
    }

    public static void RestoreOriginal(Project p, string rel)
    {
        var o = p.OrigPath(rel);
        if (!File.Exists(o)) throw new Exception("No original copy: " + rel);
        var g = p.GamePath(rel);
        if (File.Exists(g)) File.SetAttributes(g, FileAttributes.Normal);
        File.Copy(o, g, true);
        AppLog.Info($"Original restored in game: {rel}");
    }

    /// <summary>
    /// After a bundle was re-packed: reset the bundle CRCs in the Addressables catalog(s) found in the bundle folder or
    /// the nearest parent (usually StreamingAssets\aa\catalog.json / .bin). The patched catalog goes into modified/ like
    /// any edited file. Returns the patched catalogs (rel paths); none when the game has no catalog.
    /// </summary>
    public static List<string> PatchCatalogs(Project p, string bundleRel, Action<string>? log = null)
    {
        var res = new List<string>();
        try { if (UnityDoc.DetectKind(p.Current(bundleRel)) != UnityDoc.FileKind.Bundle) return res; } catch { return res; }
        var root = Path.GetFullPath(p.GameRoot).TrimEnd(Path.DirectorySeparatorChar);
        var dir = Path.GetDirectoryName(p.GamePath(bundleRel));
        List<string> found = new();
        while (dir != null && dir.Length >= root.Length && found.Count == 0)
        {
            try { found = Directory.GetFiles(dir).Where(f => Catalog.IsCatalogName(Path.GetFileName(f))).ToList(); } catch { }
            if (string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase)) break;
            dir = Path.GetDirectoryName(dir);
        }
        foreach (var f in found)
        {
            var rel = Path.GetRelativePath(p.GameRoot, f);
            try
            {
                int n = Catalog.PatchCrc(File.ReadAllBytes(p.Current(rel)), out var patched);
                if (n == 0) { AppLog.Info($"Catalog already without CRC: {rel}"); continue; }
                BackupOriginal(p, rel);
                var outPath = p.ModPath(rel);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                File.WriteAllBytes(outPath, patched);
                AppLog.Info($"Catalog CRC reset ({n} bundles): {rel}");
                log?.Invoke($"Каталог Addressables: скинуто CRC (бандлів: {n}) — {rel}");
                if (Settings.ReplaceInPlace) ApplyToGame(p, rel);
                res.Add(rel);
            }
            catch (Exception e) { AppLog.Warn($"Catalog not patched ({rel}): {e.Message}"); log?.Invoke($"⚠ Каталог не змінено ({rel}): {e.Message}"); }
        }
        return res;
    }

    /// <summary>All Addressables catalogs of the game (any folder) and how many bundle CRCs each still checks.</summary>
    public static JsonArray CatalogStatus(Project p)
    {
        var res = new JsonArray();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(p.GameRoot, "catalog*", SearchOption.AllDirectories).Where(f => Catalog.IsCatalogName(Path.GetFileName(f))).ToList(); }
        catch { return res; }
        foreach (var f in files)
        {
            var rel = Path.GetRelativePath(p.GameRoot, f);
            int n = 0; string? err = null;
            try { n = Catalog.PatchCrc(File.ReadAllBytes(p.Current(rel)), out _); } catch (Exception e) { err = e.Message; }
            res.Add(new JsonObject { ["rel"] = rel, ["crc"] = n, ["modified"] = File.Exists(p.ModPath(rel)), ["error"] = err });
        }
        return res;
    }
    /// <summary>Reset the bundle CRCs in every catalog of the game (result goes to modified/). Returns the patched catalogs.</summary>
    public static List<string> PatchAllCatalogs(Project p, Action<string>? log = null)
    {
        var res = new List<string>();
        foreach (var c in CatalogStatus(p).Select(n => n!.AsObject()).Where(n => n["crc"]!.GetValue<int>() > 0))
        {
            var rel = c["rel"]!.ToString();
            try
            {
                Catalog.PatchCrc(File.ReadAllBytes(p.Current(rel)), out var patched);
                BackupOriginal(p, rel);
                var outPath = p.ModPath(rel);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                File.WriteAllBytes(outPath, patched);
                AppLog.Info($"Catalog CRC reset: {rel}"); log?.Invoke($"Каталог Addressables: скинуто CRC — {rel}");
                if (Settings.ReplaceInPlace) ApplyToGame(p, rel);
                res.Add(rel);
            }
            catch (Exception e) { AppLog.Warn($"Catalog not patched ({rel}): {e.Message}"); }
        }
        return res;
    }

    public static JsonArray ChangedFiles(Project p)
    {
        var res = new JsonArray();
        if (!Directory.Exists(p.Modified)) return res;
        foreach (var f in Directory.EnumerateFiles(p.Modified, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(p.Modified, f);
            if (rel.EndsWith(".tmp_save") || rel.EndsWith(".raw")) continue;
            var g = p.GamePath(rel);
            bool applied = File.Exists(g) && new FileInfo(g).Length == new FileInfo(f).Length && FilesEqual(g, f);
            res.Add(new JsonObject
            {
                ["rel"] = rel, ["size"] = new FileInfo(f).Length, ["modifiedAt"] = File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm"),
                ["hasOriginal"] = File.Exists(p.OrigPath(rel)), ["applied"] = applied
            });
        }
        return res;
    }

    static bool FilesEqual(string a, string b)
    {
        try
        {
            using var fa = File.OpenRead(a); using var fb = File.OpenRead(b);
            var ba = new byte[1 << 16]; var bb = new byte[1 << 16];
            while (true)
            {
                int na = fa.Read(ba, 0, ba.Length), nb = fb.Read(bb, 0, bb.Length);
                if (na != nb) return false;
                if (na == 0) return true;
                if (!ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
            }
        }
        catch { return false; }
    }
}

/// <summary>Files opened for editing (kept in memory until saved or closed).</summary>
public static class Docs
{
    public sealed class Open
    {
        public Project P = null!;
        public string Rel = "";
        public UnityDoc? Unity;                         // null for loose text files
        public string? LooseText;
        public bool LooseDirty;
        public readonly Dictionary<(int, long), EntryTable> Tables = new();
        public bool Dirty => (Unity?.HasChanges ?? false) || LooseDirty;
    }

    static readonly Dictionary<string, Open> open = new();
    static string Key(Project p, string rel) => p.Id + "|" + rel.ToLowerInvariant();

    public static Open Get(Project p, string rel)
    {
        rel = p.CheckRel(rel);
        lock (open)
        {
            if (open.TryGetValue(Key(p, rel), out var o)) return o;
            var cur = p.Current(rel);
            o = new Open { P = p, Rel = rel };
            if (UnityDoc.DetectKind(cur) != UnityDoc.FileKind.None) o.Unity = new UnityDoc(cur, p.GamePath(rel), null, AppLog.Warn);
            else o.LooseText = Extract.DecodeText(File.ReadAllBytes(cur));
            open[Key(p, rel)] = o;
            return o;
        }
    }

    public static IEnumerable<Open> All() { lock (open) return open.Values.ToList(); }

    public static void Close(Project p, string rel)
    {
        lock (open)
            if (open.Remove(Key(p, rel), out var o)) o.Unity?.Dispose();
    }

    /// <summary>Write the edited file into modified/ (keeping the original in original/).
    /// Returns the Addressables catalogs that were patched along with a bundle (usually none).</summary>
    public static List<string> SaveDoc(Open o)
    {
        var p = o.P;
        Store.BackupOriginal(p, o.Rel);
        var outPath = p.ModPath(o.Rel);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        if (o.Unity != null) o.Unity.SaveTo(outPath);
        else { File.WriteAllText(outPath, o.LooseText ?? "", new System.Text.UTF8Encoding(false)); o.LooseDirty = false; }
        AppLog.Info($"Saved: {o.Rel} → {outPath}");
        if (Store.Settings.ReplaceInPlace) Store.ApplyToGame(p, o.Rel);
        return o.Unity?.Bundle != null ? Store.PatchCatalogs(p, o.Rel) : new List<string>();
    }
}

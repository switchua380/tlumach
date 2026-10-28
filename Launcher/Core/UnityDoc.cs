using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Cpp2IL;
using TMPFontReplacer;

namespace TranslationLauncher.Core;

/// <summary>One opened Unity file (plain serialized file, UnityFS bundle, or legacy UnityWeb/UnityRaw bundle).</summary>
public sealed class UnityDoc : IDisposable
{
    public readonly string RealPath;     // where the bytes come from (game file or workspace copy)
    public readonly string LogicalPath;  // where it lives in the game (dependencies / Managed are resolved from here)
    public readonly AssetsManager Am = new();
    public readonly List<AssetsFileInstance> Files = new();
    public BundleFileInstance? Bundle;
    public LegacyBundle? Legacy;
    readonly Dictionary<AssetsFileInstance, int> bundleIndex = new(), legacyIndex = new();
    public string UnityVersion = "";
    public string TypeSource = "";
    public readonly Dictionary<(AssetsFileInstance, long), AssetTypeValueField> Cache = new();
    public readonly HashSet<(AssetsFileInstance, long)> Dirty = new();
    public readonly Dictionary<(AssetsFileInstance, long), byte[]> RawReplaced = new();
    public string? ManagedOverride;

    public static string ToolDir => AppContext.BaseDirectory;

    public UnityDoc(string realPath, string logicalPath, string? managed = null, Action<string>? log = null)
    {
        RealPath = Path.GetFullPath(realPath);
        LogicalPath = Path.GetFullPath(logicalPath);
        ManagedOverride = managed;
        Am.LoadClassPackage(Path.Combine(ToolDir, "classdata.tpk"));
        Am.UseTemplateFieldCache = true; Am.UseMonoTemplateFieldCache = true;
        var kind = DetectKind(RealPath);
        if (kind == FileKind.LegacyBundle)
        {
            Legacy = LegacyBundle.Read(RealPath);
            for (int i = 0; i < Legacy.Files.Count; i++)
            {
                var (name, data) = Legacy.Files[i];
                try
                {
                    using (var probe = new AssetsFileReader(new MemoryStream(data)))
                        if (!AssetsFile.IsAssetsFile(probe, 0, data.Length)) continue;
                    var inst = Am.LoadAssetsFile(new MemoryStream(data), Path.Combine(Path.GetDirectoryName(LogicalPath)!, name), false);
                    Files.Add(inst); legacyIndex[inst] = i;
                }
                catch { }
            }
        }
        else if (kind == FileKind.Bundle)
        {
            Bundle = Am.LoadBundleFile(new MemoryStream(File.ReadAllBytes(RealPath)), LogicalPath, true);
            var dirs = Bundle.file.BlockAndDirInfo.DirectoryInfos;
            for (int i = 0; i < dirs.Count; i++)
            {
                if (!Bundle.file.IsAssetsFile(i)) continue;
                var inst = Am.LoadAssetsFileFromBundle(Bundle, i, false);
                Files.Add(inst); bundleIndex[inst] = i;
            }
        }
        else if (kind == FileKind.Serialized)
        {
            Files.Add(Am.LoadAssetsFile(new MemoryStream(File.ReadAllBytes(RealPath)), LogicalPath, false));
        }
        else throw new Exception("Not a Unity file");
        if (Files.Count == 0) throw new Exception("No serialized files inside");

        UnityVersion = Files.Select(i => i.file.Metadata.UnityVersion).FirstOrDefault(v => !string.IsNullOrEmpty(v) && v != "0.0.0") ?? "";
        if (UnityVersion == "" && Bundle != null) UnityVersion = Bundle.file.Header.EngineVersion;
        if (UnityVersion == "" && Legacy != null) UnityVersion = Legacy.EngineVersion;
        if (UnityVersion == "" || UnityVersion == "0.0.0") UnityVersion = GameInfo.FindVersionNearby(LogicalPath) ?? "";
        try { if (UnityVersion != "") Am.LoadClassDatabaseFromPackage(UnityVersion); } catch (Exception e) { log?.Invoke("ClassDB: " + e.Message); }
        if (Files.Any(f => !f.file.Metadata.TypeTreeEnabled)) SetupGenerator(log);
    }

    public enum FileKind { None, Serialized, Bundle, LegacyBundle }

    public static FileKind DetectKind(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 20) return FileKind.None;
            var b = new byte[64]; int n = fs.Read(b, 0, b.Length);
            var s = Encoding.ASCII.GetString(b, 0, Math.Min(n, 8));
            if (s.StartsWith("UnityFS")) return FileKind.Bundle;
            if (s.StartsWith("UnityWeb") || s.StartsWith("UnityRaw")) return FileKind.LegacyBundle;
            fs.Position = 0;
            using var r = new AssetsFileReader(fs);
            return AssetsFile.IsAssetsFile(r, 0, fs.Length) ? FileKind.Serialized : FileKind.None;
        }
        catch { return FileKind.None; }
    }

    void SetupGenerator(Action<string>? log)
    {
        var managed = ManagedOverride ?? GameInfo.FindManaged(LogicalPath);
        if (managed != null) { Am.MonoTempGenerator = new MonoCecilTempGenerator(managed); TypeSource = "Mono: " + managed; return; }
        var il = GameInfo.FindIl2Cpp(LogicalPath);
        if (il != null)
        {
            try
            {
                var g = new Cpp2IlTempGenerator(il.Value.meta, il.Value.asm);
                if (UnityVersion != "") try { g.SetUnityVersion(new UnityVersion(UnityVersion)); } catch { }
                Am.MonoTempGenerator = g; TypeSource = "IL2CPP: " + il.Value.asm;
                return;
            }
            catch (Exception e) { log?.Invoke("Cpp2IL: " + e.Message); }
        }
        TypeSource = "none (built-in TMP schemas / raw strings)";
    }

    // ------------------------------------------------------------------ listing
    public sealed record AssetRow(int File, long PathId, int TypeId, string Type, string Name, string Script, uint Size);

    public IEnumerable<AssetRow> ListAssets()
    {
        for (int fi = 0; fi < Files.Count; fi++)
        {
            var inst = Files[fi];
            foreach (var info in inst.file.AssetInfos)
            {
                int tid = info.TypeId;
                string tname = ((AssetClassID)tid).ToString();
                if (int.TryParse(tname, out _)) tname = "Class" + tid;
                string name = "", script = "";
                try { name = ReadName(inst, info); } catch { }
                if (tid == (int)AssetClassID.MonoBehaviour) script = ScriptName(inst, info);
                yield return new AssetRow(fi, info.PathId, tid, tname, name, script, info.ByteSize);
            }
        }
    }

    static readonly HashSet<int> NamedFirst = new() { 49, 28, 21, 43, 48, 74, 83, 128, 213, 114, 115, 1 };

    /// <summary>m_Name without building a template (fast; works without scripts).</summary>
    public static string ReadName(AssetsFileInstance inst, AssetFileInfo info)
    {
        var r = inst.file.Reader;
        lock (r)
        {
            long pos = info.GetAbsoluteByteOffset(inst.file);
            r.Position = pos;
            int tid = info.TypeId;
            if (tid == (int)AssetClassID.MonoBehaviour)
            {
                int pptr = inst.file.Header.Version >= 14 ? 12 : 8;
                r.Position += pptr + 4 + pptr;
            }
            else if (tid == (int)AssetClassID.GameObject) return "";
            int len = r.ReadInt32();
            if (len < 0 || len > 512 || len > info.ByteSize) return "";
            var bytes = r.ReadBytes(len);
            var s = Encoding.UTF8.GetString(bytes);
            return s.Any(ch => char.IsControl(ch)) ? "" : s;
        }
    }

    readonly Dictionary<(AssetsFileInstance, int), string> scriptNames = new();
    public string ScriptName(AssetsFileInstance inst, AssetFileInfo info)
    {
        int idx = info.GetScriptIndex(inst.file);
        if (scriptNames.TryGetValue((inst, idx), out var n)) return n;
        string res = "";
        try
        {
            var r = AssetHelper.GetAssetsFileScriptInfo(Am, inst, idx);
            if (r != null) res = string.IsNullOrEmpty(r.Namespace) ? r.ClassName : r.Namespace + "." + r.ClassName;
        }
        catch { }
        scriptNames[(inst, idx)] = res;
        return res;
    }

    readonly Dictionary<(AssetsFileInstance, int), HashSet<string>> topFields = new();
    /// <summary>Top-level field names of a MonoBehaviour's layout (typetree or scripts) — cheap structural detection.</summary>
    public HashSet<string> TopFields(int file, long pathId)
    {
        var inst = Files[file]; var info = Info(file, pathId);
        int key = info.TypeId == 114 ? info.GetScriptIndex(inst.file) : -info.TypeId;
        if (topFields.TryGetValue((inst, key), out var hs)) return hs;
        hs = new HashSet<string>();
        try
        {
            var t = Am.GetTemplateBaseField(inst, info, AssetReadFlags.None);
            foreach (var c in t.Children) hs.Add(c.Name);
            if (t["mSource"] != null) foreach (var c in t["mSource"].Children) hs.Add("mSource." + c.Name);
        }
        catch { }
        topFields[(inst, key)] = hs;
        return hs;
    }

    // ------------------------------------------------------------------ reading
    public AssetFileInfo Info(int file, long pathId) => Files[file].file.GetAssetInfo(pathId) ?? throw new Exception($"PathID {pathId} not found");

    public byte[] RawBytes(int file, long pathId)
    {
        var inst = Files[file];
        if (RawReplaced.TryGetValue((inst, pathId), out var rb)) return rb;
        if (Cache.TryGetValue((inst, pathId), out var cached) && Dirty.Contains((inst, pathId)))
            return cached.WriteToByteArray(inst.file.Reader.BigEndian);
        var info = Info(file, pathId);
        var rd = inst.file.Reader;
        lock (rd) { rd.Position = info.GetAbsoluteByteOffset(inst.file); return rd.ReadBytes((int)info.ByteSize); }
    }

    /// <summary>Full typed tree of an asset, or null if its layout is unknown (no typetree / scripts).</summary>
    public AssetTypeValueField? Field(int file, long pathId, out string how)
    {
        var inst = Files[file];
        how = "";
        if (Cache.TryGetValue((inst, pathId), out var c)) { how = "cache"; return c; }
        var info = Info(file, pathId);
        AssetTypeValueField? bf = null;
        try
        {
            var t = Am.GetTemplateBaseField(inst, info, AssetReadFlags.None);
            var raw = RawBytes(file, pathId);
            using var ms = new MemoryStream(raw);
            var r = new AssetsFileReader(ms) { BigEndian = inst.file.Reader.BigEndian };
            var v = t.MakeValue(r, 0, Am.GetRefTypeManager(inst));
            if (ms.Position == raw.Length && (info.TypeId != 114 || v.Children.Count > 4)) { bf = v; how = inst.file.Metadata.TypeTreeEnabled ? "typetree" : "scripts"; }
        }
        catch { }
        if (bf == null && info.TypeId == 114)
        {
            bf = SchemaFallback.TryParse(Am, inst, info, out var sn);
            if (bf != null) how = "builtin TMP schema " + sn;
        }
        if (bf != null) Cache[(inst, pathId)] = bf;
        return bf;
    }

    public void MarkDirty(int file, long pathId) { Dirty.Add((Files[file], pathId)); }

    public void ReplaceRaw(int file, long pathId, byte[] data)
    {
        var inst = Files[file];
        RawReplaced[(inst, pathId)] = data;
        Cache.Remove((inst, pathId));
        Dirty.Remove((inst, pathId));
        Info(file, pathId).SetNewData(data);
    }

    public bool HasChanges => Dirty.Count > 0 || RawReplaced.Count > 0;

    // ------------------------------------------------------------------ saving
    public void SaveTo(string outPath)
    {
        foreach (var key in Dirty)
        {
            var (inst, pid) = key;
            inst.file.GetAssetInfo(pid).SetNewData(Cache[key]);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        string tmp = outPath + ".tmp_save";
        if (Legacy != null)
        {
            foreach (var inst in Files.Where(f => legacyIndex.ContainsKey(f)))
            {
                using var ms = new MemoryStream();
                using (var w = new AssetsFileWriter(ms)) inst.file.Write(w, 0);
                var idx = legacyIndex[inst];
                Legacy.Files[idx] = (Legacy.Files[idx].name, ms.ToArray());
            }
            Legacy.Write(tmp);
        }
        else if (Bundle != null)
        {
            foreach (var inst in Files) Bundle.file.BlockAndDirInfo.DirectoryInfos[bundleIndex[inst]].SetNewData(inst.file);
            string raw = tmp + ".raw";
            using (var w = new AssetsFileWriter(raw)) Bundle.file.Write(w, 0);
            if (Bundle.originalCompression != AssetBundleCompressionType.None)
            {
                var nb = new AssetBundleFile();
                using (var rs = File.OpenRead(raw))
                {
                    nb.Read(new AssetsFileReader(rs));
                    using var w2 = new AssetsFileWriter(tmp);
                    nb.Pack(w2, AssetBundleCompressionType.LZ4, true, null);
                    nb.Close();
                }
                File.Delete(raw);
            }
            else File.Move(raw, tmp, true);
        }
        else
        {
            using var w = new AssetsFileWriter(tmp);
            Files[0].file.Write(w, 0);
        }
        File.Move(tmp, outPath, true);
        Dirty.Clear(); RawReplaced.Clear();
    }

    public void Dispose() { try { Am.UnloadAll(true); } catch { } }
}

/// <summary>Helpers to locate game folders: Managed, IL2CPP files, Unity version.</summary>
public static class GameInfo
{
    public static IEnumerable<string> Candidates(string dir)
    {
        yield return dir;
        foreach (var sub in new[] { "Data", Path.Combine("Contents", "Resources", "Data"), Path.Combine("Resources", "Data") })
            if (Directory.Exists(Path.Combine(dir, sub))) yield return Path.Combine(dir, sub);
        string[] subs;
        try { subs = Directory.GetDirectories(dir, "*_Data"); } catch { subs = Array.Empty<string>(); }
        foreach (var s in subs) yield return s;
    }

    public static string? FindManaged(string anyPath)
    {
        for (var dir = Directory.Exists(anyPath) ? anyPath : Path.GetDirectoryName(anyPath); dir != null; dir = Path.GetDirectoryName(dir))
            foreach (var c in Candidates(dir))
            {
                var m = Path.Combine(c, "Managed");
                if (Directory.Exists(m) && Directory.GetFiles(m, "*.dll").Length > 0) return m;
            }
        return null;
    }

    public static (string meta, string asm)? FindIl2Cpp(string anyPath)
    {
        for (var dir = Directory.Exists(anyPath) ? anyPath : Path.GetDirectoryName(anyPath); dir != null; dir = Path.GetDirectoryName(dir))
            foreach (var c in Candidates(dir))
            {
                try { var r = FindCpp2IlFiles.Find(c); if (r.success) return (r.metaPath, r.asmPath); } catch { }
                var meta = Path.Combine(c, "il2cpp_data", "Metadata", "global-metadata.dat");
                if (File.Exists(meta))
                {
                    var root = Path.GetDirectoryName(c)!;
                    foreach (var n in new[] { "GameAssembly.dll", "GameAssembly.so", "GameAssembly.dylib" })
                        if (File.Exists(Path.Combine(root, n))) return (meta, Path.Combine(root, n));
                }
            }
        return null;
    }

    public static string? FindVersionNearby(string path)
    {
        for (var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path); dir != null; dir = Path.GetDirectoryName(dir))
            foreach (var cand in Candidates(dir))
                foreach (var n in new[] { "globalgamemanagers", "mainData", "data.unity3d" })
                {
                    var f = Path.Combine(cand, n);
                    if (!File.Exists(f)) continue;
                    try
                    {
                        using var fs = File.OpenRead(f);
                        var buf = new byte[Math.Min(4096, (int)fs.Length)]; fs.Read(buf, 0, buf.Length);
                        var m = System.Text.RegularExpressions.Regex.Match(Encoding.ASCII.GetString(buf), @"(\d{1,4}\.\d+\.\d+[abfpx]\d+)");
                        if (m.Success) return m.Groups[1].Value;
                    }
                    catch { }
                }
        return null;
    }
}

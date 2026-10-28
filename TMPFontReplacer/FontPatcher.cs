using System.Globalization;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Cpp2IL;

namespace TMPFontReplacer;

public sealed class Options
{
    public string TtfPath = "", AssetFile = "", FontName = "";
    public string? ManagedDir, OutFile, ExtraChars, CharsFile, UnityVersion, LogicalPath, ReportDir;
    public int? PointSize, Padding, AtlasW, AtlasH;
    public long? FontPathId;
    public bool OnlyOriginalChars, NoKerning, NoBackup, ListOnly, SavePreview = true, ForceBitmap, BuiltinSchemas;
    public double MaxSizeFactor = 1.5;
}

public sealed class FontPatcher
{
    public string? LastReportPng, LastReportTxt;
    readonly Options o;
    readonly Action<string> log;
    AssetsManager am = new();
    BundleFileInstance? bun;
    LegacyBundle? lbun;
    readonly Dictionary<AssetsFileInstance, int> legacyIndex = new();
    readonly List<AssetsFileInstance> insts = new();
    readonly Dictionary<AssetsFileInstance, int> bundleIndex = new();
    readonly HashSet<AssetsFileInstance> modified = new();

    public FontPatcher(Options opt, Action<string> logger) { o = opt; log = logger; }

    // Default extra character set: Basic Latin, Latin-1, Ukrainian/Russian Cyrillic, typographic punctuation, ₴ № ʼ €
    public const string DefaultExtra =
        "32-126,160-255,1025,1028,1030-1031,1040-1103,1105,1108,1110-1111,1168-1169,700,8211-8212,8216-8218,8220-8222,8224-8226,8230,8240,8249-8250,8364,8372,8470,8482";

    static string ToolDir => AppContext.BaseDirectory;

    // ------------------------------------------------------------------ loading
    void Load()
    {
        string tpk = Path.Combine(ToolDir, "classdata.tpk");
        if (!File.Exists(tpk)) throw new Exception("Не знайдено classdata.tpk поруч із програмою.");
        am.LoadClassPackage(tpk);
        am.UseMonoTemplateFieldCache = true;
        am.UseTemplateFieldCache = true;

        string real = Path.GetFullPath(o.AssetFile);
        // LogicalPath: the file's place in the game folder (dependencies, Managed, version are resolved from there)
        string path = Path.GetFullPath(o.LogicalPath ?? o.AssetFile);
        if (LegacyBundle.IsLegacy(real))
        {
            lbun = LegacyBundle.Read(real);
            for (int i = 0; i < lbun.Files.Count; i++)
            {
                var (name, data) = lbun.Files[i];
                try
                {
                    using (var probe = new AssetsFileReader(new MemoryStream(data)))
                        if (!AssetsFile.IsAssetsFile(probe, 0, data.Length)) continue;
                    var inst = am.LoadAssetsFile(new MemoryStream(data), Path.Combine(Path.GetDirectoryName(path)!, name), false);
                    insts.Add(inst); legacyIndex[inst] = i;
                }
                catch { }
            }
            log($"Старий бандл {lbun.Signature} (формат {lbun.Format}, Unity {lbun.EngineVersion}), файлів усередині: {insts.Count}");
        }
        else if (IsBundle(real))
        {
            bun = am.LoadBundleFile(new MemoryStream(File.ReadAllBytes(real)), path, true);
            var dirs = bun.file.BlockAndDirInfo.DirectoryInfos;
            for (int i = 0; i < dirs.Count; i++)
            {
                if (!bun.file.IsAssetsFile(i)) continue;
                var inst = am.LoadAssetsFileFromBundle(bun, i, false);
                insts.Add(inst); bundleIndex[inst] = i;
            }
            log($"Бандл: {Path.GetFileName(path)} (стиснення: {bun.originalCompression}), файлів усередині: {insts.Count}");
        }
        else
        {
            var inst = am.LoadAssetsFile(new MemoryStream(File.ReadAllBytes(real)), path, false);
            insts.Add(inst);
        }
        if (insts.Count == 0) throw new Exception("У файлі не знайдено серіалізованих ассетів Unity.");
        string ver = o.UnityVersion ?? "";
        if (string.IsNullOrEmpty(ver)) ver = insts.Select(i => i.file.Metadata.UnityVersion).FirstOrDefault(v => !string.IsNullOrEmpty(v) && v != "0.0.0") ?? "";
        if (string.IsNullOrEmpty(ver) && bun != null) ver = bun.file.Header.EngineVersion;
        if (string.IsNullOrEmpty(ver) && lbun != null) ver = lbun.EngineVersion;
        if (string.IsNullOrEmpty(ver) || ver == "0.0.0") ver = FindVersionNearby(path) ?? "";
        if (string.IsNullOrEmpty(ver))
            log("УВАГА: версію Unity не визначено (вкажіть --unity-version, напр. 2019.4.40f1).");
        else
        {
            try { am.LoadClassDatabaseFromPackage(ver); }
            catch (Exception e) { log($"УВАГА: немає бази класів для Unity {ver}: {e.Message}"); }
        }
        log($"Версія Unity: {ver}");

        if (insts.Any(i => !i.file.Metadata.TypeTreeEnabled))
            SetupTypeTreeGenerator(path, ver);
    }

    static bool IsBundle(string path)
    {
        using var fs = File.OpenRead(path);
        var b = new byte[8]; int n = fs.Read(b, 0, 8);
        var s = Encoding.ASCII.GetString(b, 0, n);
        return s.StartsWith("UnityFS") || s.StartsWith("UnityWeb") || s.StartsWith("UnityRaw");
    }

    void SetupTypeTreeGenerator(string assetPath, string unityVersion)
    {
        // 1) user override
        if (!string.IsNullOrEmpty(o.ManagedDir))
        {
            if (File.Exists(Path.Combine(o.ManagedDir, "global-metadata.dat")))
            {
                // user pointed at a folder that contains metadata: search GameAssembly upward
                var r = FindIl2Cpp(o.ManagedDir);
                if (r != null) { UseCpp2Il(r.Value.meta, r.Value.asm, unityVersion); return; }
            }
            am.MonoTempGenerator = new MonoCecilTempGenerator(o.ManagedDir);
            log($"Managed DLL: {o.ManagedDir}");
            return;
        }
        // 2) Mono: <Game>_Data/Managed with Assembly-CSharp / Unity.TextMeshPro
        for (var dir = Path.GetDirectoryName(assetPath); dir != null; dir = Path.GetDirectoryName(dir))
        {
            foreach (var cand in Candidates(dir))
            {
                var managed = Path.Combine(cand, "Managed");
                if (Directory.Exists(managed) && Directory.GetFiles(managed, "*.dll").Length > 0)
                {
                    am.MonoTempGenerator = new MonoCecilTempGenerator(managed);
                    log($"Знайдено Managed DLL: {managed}");
                    return;
                }
            }
        }
        // 3) IL2CPP
        var il = FindIl2Cpp(Path.GetDirectoryName(assetPath)!);
        if (il != null) { UseCpp2Il(il.Value.meta, il.Value.asm, unityVersion); return; }
        log("Папку Managed / GameAssembly.dll не знайдено — використаю вбудовані схеми TMP (усі версії 1.1 … Unity 6).");
    }

    /// <summary>Stripped bundles carry no version: read it from globalgamemanagers / data.unity3d nearby.</summary>
    string? FindVersionNearby(string path)
    {
        for (var dir = Path.GetDirectoryName(path); dir != null; dir = Path.GetDirectoryName(dir))
            foreach (var cand in Candidates(dir))
                foreach (var n in new[] { "globalgamemanagers", "mainData", "data.unity3d" })
                {
                    var f = Path.Combine(cand, n);
                    if (!File.Exists(f)) continue;
                    try
                    {
                        using var fs = File.OpenRead(f);
                        var buf = new byte[Math.Min(4096, (int)fs.Length)]; fs.Read(buf, 0, buf.Length);
                        var txt = Encoding.ASCII.GetString(buf);
                        var m = System.Text.RegularExpressions.Regex.Match(txt, @"(\d{1,4}\.\d+\.\d+[abfpx]\d+)");
                        if (m.Success) return m.Groups[1].Value;
                    }
                    catch { }
                }
        return null;
    }

    static IEnumerable<string> Candidates(string dir)
    {
        yield return dir;
        foreach (var sub in new[] { "Data", Path.Combine("Contents", "Resources", "Data"), Path.Combine("Resources", "Data") })
            if (Directory.Exists(Path.Combine(dir, sub))) yield return Path.Combine(dir, sub);
        string[] subs;
        try { subs = Directory.GetDirectories(dir, "*_Data"); } catch { subs = Array.Empty<string>(); }
        foreach (var s in subs) yield return s;
    }

    static (string meta, string asm)? FindIl2Cpp(string start)
    {
        for (var dir = start; dir != null; dir = Path.GetDirectoryName(dir))
        {
            foreach (var cand in Candidates(dir))
            {
                try
                {
                    var r = FindCpp2IlFiles.Find(cand);
                    if (r.success) return (r.metaPath, r.asmPath);
                }
                catch { }
                var meta = Path.Combine(cand, "il2cpp_data", "Metadata", "global-metadata.dat");
                if (File.Exists(meta))
                {
                    var root = Path.GetDirectoryName(cand)!;
                    var ga = Path.Combine(root, "GameAssembly.dll");
                    if (File.Exists(ga)) return (meta, ga);
                }
            }
        }
        return null;
    }

    void UseCpp2Il(string meta, string asm, string ver)
    {
        Cpp2IlTempGenerator gen;
        try { gen = new Cpp2IlTempGenerator(meta, asm); }
        catch (Exception e) { log($"IL2CPP-метадані не читаються ({e.Message}) — використаю вбудовані схеми TMP."); return; }
        if (!string.IsNullOrEmpty(ver) && ver != "0.0.0") { try { gen.SetUnityVersion(new UnityVersion(ver)); } catch { } }
        am.MonoTempGenerator = gen;
        log($"IL2CPP: {asm}");
    }

    // ------------------------------------------------------------------ search
    IEnumerable<(AssetsFileInstance inst, AssetFileInfo info, string name)> MonoBehaviours()
    {
        foreach (var inst in insts)
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
            {
                string name;
                try { name = ReadMonoBehaviourName(inst, info); } catch { continue; }
                yield return (inst, info, name);
            }
    }

    static string ReadMonoBehaviourName(AssetsFileInstance inst, AssetFileInfo info)
    {
        var r = inst.file.Reader;
        lock (r)
        {
            r.Position = info.GetAbsoluteByteOffset(inst.file);
            // PPtr<GameObject> + m_Enabled (1, aligned to 4) + PPtr<MonoScript>; PPtr = int + (int64 since format 14, int32 before)
            int pptr = inst.file.Header.Version >= 14 ? 12 : 8;
            r.Position += pptr + 4 + pptr;
            int len = r.ReadInt32();
            if (len < 0 || len > 1024) return "";
            return Encoding.UTF8.GetString(r.ReadBytes(len));
        }
    }

    /// <summary>Read a MonoBehaviour as a TMP font: game scripts first, built-in TMP layouts as a fallback.</summary>
    AssetTypeValueField? ReadTmpFont(AssetsFileInstance inst, AssetFileInfo info, out string how)
    {
        how = "";
        if (!o.BuiltinSchemas)
        {
            try
            {
                // read with the game's own script layout, and make sure it consumes the asset exactly
                var t = am.GetTemplateBaseField(inst, info, AssetReadFlags.None);
                var bf = SchemaFallback.ParseExact(inst, info, t);
                if (bf != null && (!bf["m_GlyphTable"].IsDummy || !bf["m_glyphInfoList"].IsDummy)) { how = "скрипти гри"; return bf; }
            }
            catch { }
        }
        var v = SchemaFallback.TryParse(am, inst, info, out var sname);
        if (v != null) { how = "вбудована схема: " + sname; return v; }
        return null;
    }

    public List<string> ListFonts()
    {
        Load();
        var res = new List<string>();
        foreach (var (inst, info, name) in MonoBehaviours())
        {
            if (string.IsNullOrEmpty(name)) continue;
            var bf = ReadTmpFont(inst, info, out _);
            if (bf == null) continue;
            bool modern = !bf["m_GlyphTable"].IsDummy, legacy = !bf["m_glyphInfoList"].IsDummy;
            if (!modern && !legacy) continue;
            int cnt = modern ? bf["m_CharacterTable"]["Array"].Children.Count : bf["m_glyphInfoList"]["Array"].Children.Count;
            res.Add($"{name}   ({(modern ? "TMP" : "TMP legacy")}, символів: {cnt}, PathID {info.PathId}{(insts.Count > 1 ? ", " + inst.name : "")})");
        }
        foreach (var inst in insts)
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Font))
            {
                try
                {
                    var bf = am.GetBaseField(inst, info);
                    int n = bf["m_FontData"]["Array"].IsDummy ? 0 : bf["m_FontData"]["Array"].Children.Count;
                    if (n > 0) res.Add($"{bf["m_Name"].AsString}   (звичайний Unity Font, вбудований TTF {n / 1024} КБ, PathID {info.PathId})");
                }
                catch { }
            }
        am.UnloadAll(true);
        return res;
    }

    // ------------------------------------------------------------------ main
    public void Run()
    {
        var ttfBytes = File.ReadAllBytes(o.TtfPath);
        var ttf = new TrueTypeFont(ttfBytes);
        log($"Новий шрифт: {ttf.FamilyName} {ttf.StyleName} ({Path.GetFileName(o.TtfPath)})");

        Load();

        // ---- find the font asset
        (AssetsFileInstance inst, AssetFileInfo info, AssetTypeValueField bf)? found = null;
        var candidates = MonoBehaviours().Where(m => m.name == o.FontName).ToList();
        if (o.FontPathId.HasValue)
        {
            var exact = MonoBehaviours().Where(m => m.info.PathId == o.FontPathId.Value).ToList();
            if (exact.Count > 0) candidates = exact.Concat(candidates.Where(c => c.info.PathId != o.FontPathId.Value)).ToList();
        }
        if (candidates.Count == 0) candidates = MonoBehaviours().Where(m => string.Equals(m.name, o.FontName, StringComparison.OrdinalIgnoreCase)).ToList();
        string readHow = "";
        foreach (var c in candidates)
        {
            var bf = ReadTmpFont(c.inst, c.info, out readHow);
            if (bf != null) { found = (c.inst, c.info, bf); break; }
        }
        if (found == null)
        {
            var uf = FindUnityFont(o.FontName);
            if (uf != null) { ReplaceUnityFont(uf.Value.inst, uf.Value.info, uf.Value.bf, ttf, ttfBytes); Save(Path.GetFullPath(o.OutFile ?? o.AssetFile)); return; }
        }
        if (found == null && candidates.Count > 0)
            throw new Exception($"Ассет «{o.FontName}» знайдено, але не вдалося прочитати його поля (немає даних скрипта). " +
                "Запускайте програму на файлі, що лежить у своїй папці гри (поруч мають бути globalgamemanagers / інші .assets та папка Managed), або вкажіть --managed.");
        if (found == null) throw new Exception($"TMP-шрифт «{o.FontName}» не знайдено у файлі. Запустіть із --list, щоб побачити список.");
        var (finst, finfo, fbf) = found.Value;
        bool hasModern = !fbf["m_GlyphTable"].IsDummy, hasLegacy = !fbf["m_glyphInfoList"].IsDummy;
        // where the original data lives (TMP 1.4+ keeps legacy fields; an un-upgraded asset still has data only there)
        bool modern = hasModern && (!hasLegacy || fbf["m_GlyphTable"]["Array"].Children.Count > 0 || fbf["m_glyphInfoList"]["Array"].Children.Count == 0);
        string kind = modern ? (!fbf["m_Material"].IsDummy ? "TMP / TextCore (Unity 2023–6)" : "TMP 1.4–3.x (Unity 2018.3–2022)") : "TMP legacy 1.0–1.3 (Unity 5–2018)";
        log($"Знайдено шрифт «{o.FontName}» (PathID {finfo.PathId}), формат: {kind}; прочитано через: {readHow}");

        // ---- original settings
        int atlasW, atlasH, padding, origSize; int renderMode = 4165;
        var origChars = new List<int>();
        AssetTypeValueField texPtr, matPtr;
        if (modern)
        {
            atlasW = fbf["m_AtlasWidth"].AsInt; atlasH = fbf["m_AtlasHeight"].AsInt; padding = fbf["m_AtlasPadding"].AsInt;
            origSize = (int)Math.Round(Num(fbf["m_FaceInfo"]["m_PointSize"]));
            if (!fbf["m_AtlasRenderMode"].IsDummy) renderMode = fbf["m_AtlasRenderMode"].AsInt;
            foreach (var c in fbf["m_CharacterTable"]["Array"].Children) origChars.Add((int)c["m_Unicode"].AsUInt);
            texPtr = fbf["m_AtlasTextures"]["Array"].Children.Count > 0 ? fbf["m_AtlasTextures"]["Array"].Children[0] : fbf["atlas"];
        }
        else
        {
            var fi = fbf["m_fontInfo"];
            atlasW = (int)Num(fi["AtlasWidth"]); atlasH = (int)Num(fi["AtlasHeight"]); padding = (int)Num(fi["Padding"]);
            origSize = (int)Math.Round(Num(fi["PointSize"]));
            foreach (var c in fbf["m_glyphInfoList"]["Array"].Children) origChars.Add(c["id"].AsInt);
            texPtr = fbf["atlas"];
        }
        matPtr = !fbf["m_Material"].IsDummy ? fbf["m_Material"] : fbf["material"];
        // tab width convention differs between TMP versions -> keep the original ratio tab/space
        double tabRatio = modern ? 1 : 10;
        try
        {
            double origTab, origSpace = 0;
            if (modern)
            {
                origTab = Num(fbf["m_FaceInfo"]["m_TabWidth"]);
                var sp = fbf["m_CharacterTable"]["Array"].Children.FirstOrDefault(c => c["m_Unicode"].AsUInt == 32);
                if (sp != null)
                {
                    var gi = sp["m_GlyphIndex"].AsUInt;
                    var g = fbf["m_GlyphTable"]["Array"].Children.FirstOrDefault(x => x["m_Index"].AsUInt == gi);
                    if (g != null) origSpace = Num(g["m_Metrics"]["m_HorizontalAdvance"]);
                }
            }
            else
            {
                origTab = Num(fbf["m_fontInfo"]["TabWidth"]);
                var g = fbf["m_glyphInfoList"]["Array"].Children.FirstOrDefault(c => c["id"].AsInt == 32);
                if (g != null) origSpace = Num(g["xAdvance"]);
            }
            if (origTab > 0 && origSpace > 0) tabRatio = Math.Round(origTab / origSpace) >= 5 ? 10 : 1;
        }
        catch { }
        if (o.AtlasW.HasValue) atlasW = o.AtlasW.Value;
        if (o.AtlasH.HasValue) atlasH = o.AtlasH.Value;
        if (o.Padding.HasValue) padding = o.Padding.Value;
        if (atlasW <= 0 || atlasH <= 0) { atlasW = atlasH = 1024; }
        if (padding <= 0) padding = 5;
        if (origSize <= 0) origSize = 48;
        log($"Оригінал: атлас {atlasW}x{atlasH}, padding {padding}, розмір {origSize}, символів {origChars.Count}, render mode {renderMode}");

        // ---- texture & material
        AssetExternal tex = default;
        if (texPtr != null && !texPtr.IsDummy && texPtr["m_PathID"].AsLong != 0)
            try { tex = am.GetExtAsset(finst, texPtr, false); } catch { }
        if (tex.baseField == null)
        {
            // no usable reference → the atlas is the Texture2D named like the font ("<font> Atlas")
            var pair = FontPairs.FindAtlas(new[] { finst }.Concat(insts.Where(i => i != finst)), o.FontName);
            if (pair == null) throw new Exception("Не знайдено текстуру атласу: у шрифті немає посилання на неї і немає Texture2D з назвою «" + o.FontName + " Atlas».");
            var (pi, pinf, pn) = pair.Value;
            tex = am.GetExtAsset(pi, 0, pinf.PathId, false);
            if (tex.baseField == null) throw new Exception("Не вдалося прочитати текстуру атласу «" + pn + "».");
            log($"Атлас знайдено за назвою: «{pn}» (PathID {pinf.PathId})");
            if (texPtr != null && !texPtr.IsDummy && pi == finst) { texPtr["m_FileID"].AsInt = 0; texPtr["m_PathID"].AsLong = pinf.PathId; }
        }
        AssetExternal mat = default;
        float gradientScale = padding + 1;
        bool haveMat = false;
        if (matPtr != null && !matPtr.IsDummy && matPtr["m_PathID"].AsLong != 0)
        {
            try { mat = am.GetExtAsset(finst, matPtr, false); haveMat = mat.baseField != null; } catch { haveMat = false; }
        }
        if (haveMat && !o.Padding.HasValue)
        {
            var gs = MatFloat(mat.baseField, "_GradientScale");
            if (gs.HasValue && gs.Value > 0) gradientScale = gs.Value;
        }

        // ---- character set
        var wanted = new SortedSet<int>();
        foreach (var c in origChars) wanted.Add(c);
        if (!o.OnlyOriginalChars) foreach (var c in ParseRanges(DefaultExtra)) wanted.Add(c);
        if (!string.IsNullOrEmpty(o.ExtraChars)) foreach (var ch in EnumerateCodepoints(o.ExtraChars)) wanted.Add(ch);
        if (!string.IsNullOrEmpty(o.CharsFile)) foreach (var ch in EnumerateCodepoints(File.ReadAllText(o.CharsFile))) wanted.Add(ch);
        var missing = wanted.Where(c => !ttf.HasChar(c) && c > 32).ToList();
        var chars = wanted.Where(c => ttf.HasChar(c)).ToList();
        if (!chars.Contains(32) && ttf.HasChar(32)) chars.Insert(0, 32);
        var glyphIds = chars.Select(ttf.GlyphOf).Distinct().OrderBy(x => x).ToList();
        log($"Символів у новому атласі: {chars.Count} (гліфів: {glyphIds.Count})");

        // ---- layout & render
        AtlasResult? atlas;
        if (o.PointSize.HasValue)
        {
            atlas = AtlasBuilder.TryLayout(ttf, glyphIds, o.PointSize.Value, padding, atlasW, atlasH)
                ?? throw new Exception($"Розмір {o.PointSize} не вміщується в атлас {atlasW}x{atlasH}. Зменште --size або збільште --atlas.");
        }
        else
        {
            int maxSize = Math.Max(origSize, (int)Math.Ceiling(origSize * o.MaxSizeFactor));
            atlas = AtlasBuilder.FitLargest(ttf, glyphIds, 6, maxSize, padding, atlasW, atlasH)
                ?? throw new Exception($"Символи не вміщуються в атлас {atlasW}x{atlasH} навіть на малому розмірі. Збільште --atlas або зменште набір символів.");
        }
        bool sdf;
        if (o.ForceBitmap) sdf = false;
        else if (haveMat && MatFloat(mat.baseField, "_GradientScale") is float) sdf = true;              // SDF shaders have _GradientScale
        else if (haveMat && MatFloat(mat.baseField, "_GradientScale") == null && MatHasFloats(mat.baseField)) sdf = false; // Bitmap shader
        else sdf = !modern || (renderMode & 0x60) != 0;
        log($"Розмір семплінгу: {atlas.PointSize} (оригінал {origSize}); рендер: {(sdf ? "SDF" : "bitmap")}");
        AtlasBuilder.Render(atlas, sdf, gradientScale);
        var byGlyph = atlas.Glyphs.ToDictionary(g => g.Index);

        // ---- face metrics
        double s = (double)atlas.PointSize / ttf.UnitsPerEm;
        var face = BuildFace(ttf, s, byGlyph);

        // ---- write font asset
        // write every layout the asset has, so any TMP version (and its upgrade path) sees the same data
        var kern = o.NoKerning ? new Dictionary<(int, int), int>() : ttf.ReadKerning(new HashSet<int>(atlas.Glyphs.Select(g => g.Index)));
        if (hasModern) WriteModern(fbf, ttf, atlas, chars, byGlyph, face, s, modern ? tabRatio : 1, kern);
        if (hasLegacy) WriteLegacy(fbf, ttf, atlas, chars, byGlyph, face, !modern ? tabRatio : 10, kern, s);
        if (kern.Count > 0) log($"Кернінг: {kern.Count} пар");
        finfo.SetNewData(fbf);
        modified.Add(finst);

        // ---- write texture
        WriteTexture(tex.baseField, atlas);
        tex.info.SetNewData(tex.baseField);
        modified.Add(tex.file);

        // ---- material (only atlas size / gradient scale)
        if (haveMat)
        {
            bool ch = SetMatFloat(mat.baseField, "_TextureWidth", atlas.Width) | SetMatFloat(mat.baseField, "_TextureHeight", atlas.Height);
            if (o.Padding.HasValue) ch |= SetMatFloat(mat.baseField, "_GradientScale", padding + 1);
            if (ch) { mat.info.SetNewData(mat.baseField); modified.Add(mat.file); }
        }

        // ---- result report: what got in, what was skipped
        string outPath = Path.GetFullPath(o.OutFile ?? o.AssetFile);
        IEnumerable<int>? reqFile = string.IsNullOrEmpty(o.CharsFile) ? null : EnumerateCodepoints(File.ReadAllText(o.CharsFile)).ToList();
        var rep = Report.Analyze(ttf, atlas, wanted, chars, reqFile, origChars);
        Report.Print(rep, log);
        if (o.SavePreview)
        {
            string baseName = Path.Combine(o.ReportDir ?? Path.GetDirectoryName(outPath)!, SafeName(o.FontName));
            Png.WriteGray(baseName + "_atlas.png", atlas.Alpha, atlas.Width, atlas.Height);
            Report.WritePng(baseName + "_report.png", rep, ttf, atlas, sdf, gradientScale, $"{o.FontName}  ←  {Path.GetFileName(o.TtfPath)}  (розмір {atlas.PointSize}, атлас {atlas.Width}x{atlas.Height})");
            Report.WriteText(baseName + "_report.txt", rep, o.FontName, Path.GetFileName(o.TtfPath), atlas);
            LastReportPng = baseName + "_report.png"; LastReportTxt = baseName + "_report.txt";
            log($"Звіт: {baseName}_report.png  (усі символи, намальовані з нового атласу, і пропущені — червоним)");
            log($"      {baseName}_report.txt  ·  атлас: {baseName}_atlas.png");
        }

        Save(outPath);
    }

    // ------------------------------------------------------------------ UnityEngine.Font (legacy uGUI Text / dynamic fonts)
    (AssetsFileInstance inst, AssetFileInfo info, AssetTypeValueField bf)? FindUnityFont(string name)
    {
        foreach (var inst in insts)
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Font))
            {
                AssetTypeValueField bf;
                try { bf = am.GetBaseField(inst, info); } catch { continue; }
                if (string.Equals(bf["m_Name"].AsString, name, StringComparison.OrdinalIgnoreCase)) return (inst, info, bf);
            }
        return null;
    }

    void ReplaceUnityFont(AssetsFileInstance inst, AssetFileInfo info, AssetTypeValueField bf, TrueTypeFont ttf, byte[] ttfBytes)
    {
        var data = bf["m_FontData"]["Array"];
        if (data.IsDummy) throw new Exception("Цей Font-ассет не має поля m_FontData.");
        if (data.Children.Count == 0 && !bf["m_CharacterRects"].IsDummy && bf["m_CharacterRects"]["Array"].Children.Count > 0)
            throw new Exception("Це растровий (не динамічний) Unity Font — його не можна замінити TTF-файлом.");
        log($"Знайдено звичайний Unity Font «{bf["m_Name"].AsString}» — замінюю вбудований TTF (динамічний шрифт).");
        var list = new List<AssetTypeValueField>(ttfBytes.Length);
        foreach (var b in ttfBytes)
        {
            var e = ValueBuilder.DefaultValueFieldFromArrayTemplate(data);
            SetNum(e, unchecked((sbyte)b));
            if (e.Value.ValueType == AssetValueType.UInt8) e.AsByte = b;
            list.Add(e);
        }
        SetArray(data, list);
        double size = Num(bf["m_FontSize"]); if (size <= 0) size = 16;
        double sc = size / ttf.UnitsPerEm;
        SetNum(bf["m_Ascent"], ttf.Ascender * sc);
        SetNum(bf["m_Descent"], ttf.Descender * sc);
        SetNum(bf["m_LineSpacing"], (ttf.Ascender - ttf.Descender + ttf.LineGap) * sc);
        info.SetNewData(bf);
        modified.Add(inst);
    }

    static bool MatHasFloats(AssetTypeValueField mat)
    {
        var arr = mat["m_SavedProperties"]["m_Floats"]["Array"];
        return !arr.IsDummy && arr.Children.Count > 0;
    }

    // ------------------------------------------------------------------ metrics
    sealed class Face { public double Line, Asc, Desc, Cap, Mean, UlOff, UlTh, StOff, StTh, Tab; }

    static Face BuildFace(TrueTypeFont f, double s, Dictionary<int, GlyphResult> byGlyph)
    {
        var face = new Face();
        face.Asc = f.Ascender * s; face.Desc = f.Descender * s;
        face.Line = (f.Ascender - f.Descender + f.LineGap) * s;
        double H(int cp, int fallbackUnits)
        {
            int g = f.GlyphOf(cp);
            if (g != 0) { f.GetSegments(g, s, out _, out _, out _, out var yMax); if (yMax > 0) return Math.Round(yMax); }
            return Math.Round(fallbackUnits * s);
        }
        face.Cap = H('H', f.CapHeight > 0 ? f.CapHeight : (int)(f.Ascender * 0.7));
        face.Mean = H('x', f.XHeight > 0 ? f.XHeight : (int)(f.Ascender * 0.5));
        face.UlOff = (f.UnderlinePos - f.UnderlineThickness / 2.0) * s;
        face.UlTh = f.UnderlineThickness * s;
        face.StOff = face.Mean / 2.5;
        face.StTh = face.UlTh;
        face.Tab = Math.Round(f.Advance(f.GlyphOf(32)) * s);
        return face;
    }

    void WriteModern(AssetTypeValueField bf, TrueTypeFont ttf, AtlasResult a, List<int> chars, Dictionary<int, GlyphResult> byGlyph, Face face, double s, double tabRatio, Dictionary<(int, int), int> kern)
    {
        var fi = bf["m_FaceInfo"];
        SetStr(fi["m_FamilyName"], ttf.FamilyName); SetStr(fi["m_StyleName"], ttf.StyleName);
        SetNum(fi["m_PointSize"], a.PointSize); SetNum(fi["m_Scale"], 1);
        SetNum(fi["m_LineHeight"], face.Line); SetNum(fi["m_AscentLine"], face.Asc);
        SetNum(fi["m_CapLine"], face.Cap); SetNum(fi["m_MeanLine"], face.Mean); SetNum(fi["m_Baseline"], 0);
        SetNum(fi["m_DescentLine"], face.Desc);
        SetNum(fi["m_SuperscriptOffset"], face.Asc); SetNum(fi["m_SuperscriptSize"], 0.5);
        SetNum(fi["m_SubscriptOffset"], face.Desc); SetNum(fi["m_SubscriptSize"], 0.5);
        SetNum(fi["m_UnderlineOffset"], face.UlOff); SetNum(fi["m_UnderlineThickness"], face.UlTh);
        SetNum(fi["m_StrikethroughOffset"], face.StOff); SetNum(fi["m_StrikethroughThickness"], face.StTh);
        SetNum(fi["m_TabWidth"], face.Tab * tabRatio);

        // glyph table
        var gArr = bf["m_GlyphTable"]["Array"];
        var gList = new List<AssetTypeValueField>();
        foreach (var g in a.Glyphs.OrderBy(x => x.Index))
        {
            var e = ValueBuilder.DefaultValueFieldFromArrayTemplate(gArr);
            SetNum(e["m_Index"], g.Index);
            var m = e["m_Metrics"];
            SetNum(m["m_Width"], g.Width); SetNum(m["m_Height"], g.Height);
            SetNum(m["m_HorizontalBearingX"], g.BearingX); SetNum(m["m_HorizontalBearingY"], g.BearingY);
            SetNum(m["m_HorizontalAdvance"], g.Advance);
            var r = e["m_GlyphRect"];
            SetNum(r["m_X"], g.RectW > 0 ? g.RectX : 0); SetNum(r["m_Y"], g.RectW > 0 ? g.RectY : 0);
            SetNum(r["m_Width"], g.RectW); SetNum(r["m_Height"], g.RectH);
            SetNum(e["m_Scale"], 1); SetNum(e["m_AtlasIndex"], 0); SetNum(e["m_ClassDefinitionType"], 0);
            gList.Add(e);
        }
        SetArray(gArr, gList);

        // character table
        var cArr = bf["m_CharacterTable"]["Array"];
        var cList = new List<AssetTypeValueField>();
        foreach (var cp in chars)
        {
            var e = ValueBuilder.DefaultValueFieldFromArrayTemplate(cArr);
            SetNum(e["m_ElementType"], 1); SetNum(e["m_Unicode"], cp);
            SetNum(e["m_GlyphIndex"], ttf.GlyphOf(cp)); SetNum(e["m_Scale"], 1);
            cList.Add(e);
        }
        SetArray(cArr, cList);

        // atlas bookkeeping
        var texArr = bf["m_AtlasTextures"]["Array"];
        if (!texArr.IsDummy && texArr.Children.Count > 1) SetArray(texArr, new List<AssetTypeValueField> { texArr.Children[0] });
        SetNum(bf["m_AtlasTextureIndex"], 0);
        SetNum(bf["m_IsMultiAtlasTexturesEnabled"], 0);
        SetNum(bf["m_AtlasPopulationMode"], 0);   // static: never rasterize from the old source font at runtime
        SetNum(bf["m_AtlasWidth"], a.Width); SetNum(bf["m_AtlasHeight"], a.Height); SetNum(bf["m_AtlasPadding"], a.Padding);
        FillRects(bf["m_UsedGlyphRects"], a.UsedRects);
        FillRects(bf["m_FreeGlyphRects"], a.FreeRects);

        // font features: old glyph indices are meaningless now -> rebuild kerning from the new TTF
        var ft = bf["m_FontFeatureTable"];
        if (!ft.IsDummy)
        {
            foreach (var c in ft.Children)
                if (c.Children.Count == 1 && c.Children[0].TemplateField.IsArray) SetArray(c.Children[0], new List<AssetTypeValueField>());
            var pairs = ft["m_GlyphPairAdjustmentRecords"]["Array"];
            if (!pairs.IsDummy)
            {
                var list = new List<AssetTypeValueField>();
                foreach (var kv in kern)
                {
                    var e = ValueBuilder.DefaultValueFieldFromArrayTemplate(pairs);
                    SetNum(e["m_FirstAdjustmentRecord"]["m_GlyphIndex"], kv.Key.Item1);
                    SetNum(e["m_FirstAdjustmentRecord"]["m_GlyphValueRecord"]["m_XAdvance"], Math.Round(kv.Value * s, 3));
                    SetNum(e["m_SecondAdjustmentRecord"]["m_GlyphIndex"], kv.Key.Item2);
                    SetNum(e["m_FeatureLookupFlags"], 0);
                    list.Add(e);
                }
                SetArray(pairs, list);
            }
        }
        // TMP 1.4–2.x still carries the legacy kerning table (keyed by unicode)
        FillLegacyKerning(bf["m_KerningTable"], kern, ttf, chars, s);

        // creation settings (informational)
        var cs = bf["m_CreationSettings"];
        if (!cs.IsDummy)
        {
            SetStr(cs["sourceFontFileName"], Path.GetFileName(o.TtfPath));
            SetNum(cs["pointSize"], a.PointSize); SetNum(cs["padding"], a.Padding);
            SetNum(cs["atlasWidth"], a.Width); SetNum(cs["atlasHeight"], a.Height);
            SetStr(cs["characterSequence"], ToRanges(chars));
        }
    }

    void WriteLegacy(AssetTypeValueField bf, TrueTypeFont ttf, AtlasResult a, List<int> chars, Dictionary<int, GlyphResult> byGlyph, Face face, double tabRatio, Dictionary<(int, int), int> kern, double s)
    {
        var fi = bf["m_fontInfo"];
        SetStr(fi["Name"], $"{ttf.FamilyName} {ttf.StyleName}");
        SetNum(fi["PointSize"], a.PointSize); SetNum(fi["Scale"], 1);
        SetNum(fi["CharacterCount"], chars.Count);
        SetNum(fi["LineHeight"], face.Line); SetNum(fi["Baseline"], 0);
        SetNum(fi["Ascender"], face.Asc); SetNum(fi["CapHeight"], face.Cap); SetNum(fi["Descender"], face.Desc);
        SetNum(fi["CenterLine"], 0);
        SetNum(fi["SuperscriptOffset"], face.Asc); SetNum(fi["SubscriptOffset"], face.Desc); SetNum(fi["SubSize"], 0.5);
        SetNum(fi["Underline"], face.UlOff); SetNum(fi["UnderlineThickness"], face.UlTh);
        SetNum(fi["strikethrough"], face.StOff); SetNum(fi["strikethroughThickness"], face.StTh);
        SetNum(fi["TabWidth"], face.Tab * tabRatio);
        SetNum(fi["Padding"], a.Padding); SetNum(fi["AtlasWidth"], a.Width); SetNum(fi["AtlasHeight"], a.Height);

        var arr = bf["m_glyphInfoList"]["Array"];
        var list = new List<AssetTypeValueField>();
        foreach (var cp in chars)
        {
            var g = byGlyph[ttf.GlyphOf(cp)];
            var e = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
            SetNum(e["id"], cp);
            SetNum(e["x"], g.RectW > 0 ? g.RectX : 0);
            SetNum(e["y"], g.RectW > 0 ? a.Height - g.RectY - g.RectH : 0);
            SetNum(e["width"], g.RectW); SetNum(e["height"], g.RectH);
            SetNum(e["xOffset"], g.BearingX); SetNum(e["yOffset"], g.BearingY);
            SetNum(e["xAdvance"], g.Advance); SetNum(e["scale"], 1);
            list.Add(e);
        }
        SetArray(arr, list);
        FillLegacyKerning(bf["m_kerningInfo"], kern, ttf, chars, s);
        var cs = bf["fontCreationSettings"];
        if (!cs.IsDummy)
        {
            SetNum(cs["fontSize"], a.PointSize); SetNum(cs["fontPadding"], a.Padding);
            SetNum(cs["fontAtlasWidth"], a.Width); SetNum(cs["fontAtlasHeight"], a.Height);
        }
    }

    static void FillLegacyKerning(AssetTypeValueField table, Dictionary<(int, int), int> kern, TrueTypeFont ttf, List<int> chars, double s)
    {
        if (table == null || table.IsDummy || table["kerningPairs"].IsDummy) return;
        var arr = table["kerningPairs"]["Array"];
        var byGlyph = chars.GroupBy(ttf.GlyphOf).ToDictionary(g => g.Key, g => g.ToList());
        var list = new List<AssetTypeValueField>();
        foreach (var kv in kern)
        {
            if (!byGlyph.TryGetValue(kv.Key.Item1, out var ls) || !byGlyph.TryGetValue(kv.Key.Item2, out var rs)) continue;
            float v = (float)Math.Round(kv.Value * s, 3);
            foreach (var l in ls) foreach (var r in rs)
            {
                if (list.Count >= 30000) break;
                var e = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
                SetNum(e["AscII_Left"], l); SetNum(e["AscII_Right"], r); SetNum(e["XadvanceOffset"], v);   // TMP <= 1.2
                SetNum(e["m_FirstGlyph"], l); SetNum(e["m_SecondGlyph"], r); SetNum(e["xOffset"], v);      // TMP 1.3
                SetNum(e["m_FirstGlyphAdjustments"]["xAdvance"], v);
                list.Add(e);
            }
        }
        SetArray(arr, list);
    }

    void WriteTexture(AssetTypeValueField t, AtlasResult a)
    {
        int fmt = t["m_TextureFormat"].AsInt;
        byte[] data;
        switch (fmt)
        {
            case 1: case 63: data = a.Alpha; break;                       // Alpha8 / R8
            case 3: data = Expand(a.Alpha, 3, -1); break;                 // RGB24
            case 4: data = Expand(a.Alpha, 4, 3); break;                  // RGBA32
            case 5: data = Expand(a.Alpha, 4, 0); break;                  // ARGB32
            case 14: data = Expand(a.Alpha, 4, 3); break;                 // BGRA32
            case 2: case 13: data = Pack4444(a.Alpha, fmt == 2); break;   // ARGB4444 / RGBA4444
            default:
                log($"Формат текстури {fmt} (стиснений/нестандартний) замінено на Alpha8 — його підтримують усі версії Unity.");
                fmt = 1; data = a.Alpha; break;
        }
        SetNum(t["m_TextureFormat"], fmt);
        SetNum(t["m_Width"], a.Width); SetNum(t["m_Height"], a.Height);
        SetNum(t["m_CompleteImageSize"], data.Length);
        SetNum(t["m_MipCount"], 1);
        if (!t["m_MipMap"].IsDummy) t["m_MipMap"].AsBool = false;
        if (!t["m_ImageCount"].IsDummy) SetNum(t["m_ImageCount"], 1);
        t["image data"].AsByteArray = data;
        var sd = t["m_StreamData"];
        if (!sd.IsDummy) { SetNum(sd["offset"], 0); SetNum(sd["size"], 0); sd["path"].AsString = ""; }
    }

    /// <summary>Gray SDF into multi-channel formats: every colour channel = value, alpha = value.</summary>
    static byte[] Expand(byte[] alpha, int bpp, int alphaIndex)
    {
        var r = new byte[alpha.Length * bpp];
        for (int i = 0; i < alpha.Length; i++)
            for (int c = 0; c < bpp; c++) r[i * bpp + c] = alpha[i];
        return r;
    }

    static byte[] Pack4444(byte[] alpha, bool argb)
    {
        var r = new byte[alpha.Length * 2];
        for (int i = 0; i < alpha.Length; i++)
        {
            int v = alpha[i] >> 4;
            ushort px = (ushort)((v << 12) | (v << 8) | (v << 4) | v);
            r[i * 2] = (byte)px; r[i * 2 + 1] = (byte)(px >> 8);
        }
        return r;
    }

    // ------------------------------------------------------------------ save
    void Save(string outPath)
    {
        string src = Path.GetFullPath(o.AssetFile);
        bool inPlace = string.Equals(outPath, src, StringComparison.OrdinalIgnoreCase);
        string tmp = outPath + ".tmp_tfr";
        if (lbun != null)
        {
            foreach (var inst in modified)
            {
                if (!legacyIndex.TryGetValue(inst, out var idx)) throw new Exception("Змінений ассет знаходиться поза бандлом — такий випадок не підтримується.");
                using var ms = new MemoryStream();
                using (var w = new AssetsFileWriter(ms)) inst.file.Write(w, 0);
                lbun.Files[idx] = (lbun.Files[idx].name, ms.ToArray());
            }
            am.UnloadAll(true);
            lbun.Write(tmp);
        }
        else if (bun != null)
        {
            foreach (var inst in modified)
            {
                if (!bundleIndex.TryGetValue(inst, out var idx)) throw new Exception("Змінений ассет знаходиться поза бандлом — такий випадок не підтримується.");
                bun.file.BlockAndDirInfo.DirectoryInfos[idx].SetNewData(inst.file);
            }
            string tmpRaw = tmp + ".raw";
            using (var w = new AssetsFileWriter(tmpRaw)) bun.file.Write(w, 0);
            var comp = bun.originalCompression;
            am.UnloadAll(true);
            if (comp != AssetBundleCompressionType.None)
            {
                log("Стискаю бандл (LZ4)...");
                var nb = new AssetBundleFile();
                using (var rs = File.OpenRead(tmpRaw))
                {
                    nb.Read(new AssetsFileReader(rs));
                    using var w2 = new AssetsFileWriter(tmp);
                    nb.Pack(w2, AssetBundleCompressionType.LZ4, true, null);
                    nb.Close();
                }
                File.Delete(tmpRaw);
            }
            else File.Move(tmpRaw, tmp, true);
        }
        else
        {
            var extra = modified.Where(i => i != insts[0]).ToList();
            using (var w = new AssetsFileWriter(tmp)) insts[0].file.Write(w, 0);
            var extraTmp = new List<(string tmp, string dst)>();
            foreach (var e in extra)
            {
                var et = e.path + ".tmp_tfr";
                using (var w = new AssetsFileWriter(et)) e.file.Write(w, 0);
                extraTmp.Add((et, e.path));
            }
            am.UnloadAll(true);
            foreach (var (et, dst) in extraTmp) { Backup(dst); File.Move(et, dst, true); log($"Оновлено також: {dst}"); }
        }
        if (inPlace) Backup(src);
        File.Move(tmp, outPath, true);
        log($"Готово: {outPath}");
    }

    void Backup(string path)
    {
        if (o.NoBackup || !File.Exists(path)) return;
        var bak = path + ".bak";
        if (!File.Exists(bak)) { File.Copy(path, bak); log($"Резервна копія: {bak}"); }
        else log($"Резервна копія вже існує (не перезаписую): {bak}");
    }

    // ------------------------------------------------------------------ helpers
    static double Num(AssetTypeValueField f)
    {
        if (f == null || f.IsDummy || f.Value == null) return 0;
        return f.Value.ValueType switch
        {
            AssetValueType.Float => f.AsFloat,
            AssetValueType.Double => f.AsDouble,
            AssetValueType.Int64 or AssetValueType.UInt64 => f.AsLong,
            AssetValueType.Bool => f.AsBool ? 1 : 0,
            _ => f.AsInt,
        };
    }

    static void SetNum(AssetTypeValueField f, double v)
    {
        if (f == null || f.IsDummy || f.Value == null) return;
        switch (f.Value.ValueType)
        {
            case AssetValueType.Float: f.AsFloat = (float)v; break;
            case AssetValueType.Double: f.AsDouble = v; break;
            case AssetValueType.Bool: f.AsBool = v != 0; break;
            case AssetValueType.Int8: f.AsSByte = (sbyte)Math.Round(v); break;
            case AssetValueType.UInt8: f.AsByte = (byte)Math.Round(v); break;
            case AssetValueType.Int16: f.AsShort = (short)Math.Round(v); break;
            case AssetValueType.UInt16: f.AsUShort = (ushort)Math.Round(v); break;
            case AssetValueType.Int32: f.AsInt = (int)Math.Round(v); break;
            case AssetValueType.UInt32: f.AsUInt = (uint)Math.Round(v); break;
            case AssetValueType.Int64: f.AsLong = (long)Math.Round(v); break;
            case AssetValueType.UInt64: f.AsULong = (ulong)Math.Round(v); break;
        }
    }

    static void SetStr(AssetTypeValueField f, string v) { if (f != null && !f.IsDummy) f.AsString = v; }

    static void SetArray(AssetTypeValueField arr, List<AssetTypeValueField> items)
    {
        if (arr == null || arr.IsDummy) return;
        arr.Children = items;
        arr.AsArray = new AssetTypeArrayInfo(items.Count);
    }

    static void FillRects(AssetTypeValueField field, List<(int x, int y, int w, int h)> rects)
    {
        if (field == null || field.IsDummy) return;
        var arr = field["Array"];
        var list = new List<AssetTypeValueField>();
        foreach (var r in rects)
        {
            var e = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
            SetNum(e["m_X"], r.x); SetNum(e["m_Y"], r.y); SetNum(e["m_Width"], r.w); SetNum(e["m_Height"], r.h);
            list.Add(e);
        }
        SetArray(arr, list);
    }

    static float? MatFloat(AssetTypeValueField mat, string name)
    {
        var arr = mat["m_SavedProperties"]["m_Floats"]["Array"];
        if (arr.IsDummy) return null;
        foreach (var p in arr.Children) if (p["first"].AsString == name) return p["second"].AsFloat;
        return null;
    }

    static bool SetMatFloat(AssetTypeValueField mat, string name, float v)
    {
        var arr = mat["m_SavedProperties"]["m_Floats"]["Array"];
        if (arr.IsDummy) return false;
        foreach (var p in arr.Children)
            if (p["first"].AsString == name) { if (Math.Abs(p["second"].AsFloat - v) < 1e-4) return false; p["second"].AsFloat = v; return true; }
        return false;
    }

    public static IEnumerable<int> ParseRanges(string s)
    {
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ab = part.Split('-', StringSplitOptions.TrimEntries);
            int a = int.Parse(ab[0], CultureInfo.InvariantCulture);
            int b = ab.Length > 1 ? int.Parse(ab[1], CultureInfo.InvariantCulture) : a;
            for (int c = a; c <= b; c++) yield return c;
        }
    }

    static IEnumerable<int> EnumerateCodepoints(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            int cp = char.ConvertToUtf32(s, i);
            if (char.IsHighSurrogate(s[i])) i++;
            if (cp >= 32) yield return cp;
        }
    }

    static string ToRanges(List<int> chars)
    {
        var sorted = chars.Distinct().OrderBy(x => x).ToList();
        var sb = new StringBuilder();
        for (int i = 0; i < sorted.Count;)
        {
            int j = i; while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(sorted[i]); if (j > i) sb.Append(" - ").Append(sorted[j]);
            i = j + 1;
        }
        return sb.ToString();
    }

    static string Preview(List<int> cps) => string.Concat(cps.Take(40).Select(c => char.ConvertFromUtf32(c))) + (cps.Count > 40 ? "…" : "");
    static string SafeName(string n) => string.Concat(n.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

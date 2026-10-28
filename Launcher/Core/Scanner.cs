using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AssetsTools.NET.Extra;

namespace TranslationLauncher.Core;

/// <summary>Automatic search for translation data in a game folder.</summary>
public static class Scanner
{
    public static readonly string[] DefaultWords =
    {
        "language", "languages", "lang", "localization", "localisation", "localize", "localized", "locale", "locales",
        "translation", "translations", "i18n", "l10n", "strings", "stringtable", "string table", "string localization",
        "I2Languages", "I2Language", "I2Loc", "LanguageSource", "LanguageSourceAsset", "SharedTableData",
        "dialog", "dialogue", "dialogues", "subtitle", "subtitles", "texts"
    };

    public static readonly string[] LanguageNames =
    {
        "English", "Ukrainian", "Українська", "German", "Deutsch", "French", "Français", "Spanish", "Español",
        "Italian", "Italiano", "Polish", "Polski", "Portuguese", "Português", "Brazilian", "Japanese", "日本語", "Korean", "한국어",
        "Chinese", "中文", "简体中文", "繁體中文", "Turkish", "Türkçe", "Czech", "Čeština", "Dutch", "Nederlands", "Swedish", "Svenska",
        "Norwegian", "Norsk", "Danish", "Dansk", "Finnish", "Suomi", "Hungarian", "Magyar", "Romanian", "Română", "Greek", "Ελληνικά",
        "Arabic", "العربية", "Thai", "ไทย", "Vietnamese", "Tiếng Việt", "Indonesian", "Bahasa", "Kazakh", "Bulgarian"
    };

    public static readonly string[] LanguageCodes =
    {
        "EN", "UK", "UA", "DE", "FR", "ES", "IT", "PL", "PT", "JA", "JP", "KO", "KR", "ZH", "CN", "TW", "TR", "CS", "NL", "SV", "NO",
        "DA", "FI", "HU", "RO", "EL", "AR", "TH", "VI", "ID", "KK", "BG",
        "en-US", "en_US", "en-GB", "uk-UA", "uk_UA", "de-DE", "fr-FR", "es-ES", "pt-BR", "zh-CN", "zh-TW", "ja-JP", "ko-KR"
    };

    static readonly string[] KnownClasses =
    {
        "LanguageSourceAsset", "LanguageSource", "StringTable", "SharedTableData", "LocalizationAsset", "LocalizationSettings",
        "LocalizedStringDatabase", "Localization", "LocalizationData", "LanguageData", "TranslationData", "LocalizedText", "TextDatabase"
    };

    static readonly HashSet<string> SkipExt = new(StringComparer.OrdinalIgnoreCase)
    { ".dll", ".exe", ".pdb", ".so", ".dylib", ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds", ".wav", ".ogg", ".mp3", ".mp4", ".webm",
      ".bank", ".fsb", ".ttf", ".otf", ".ress", ".resource", ".pak", ".zip", ".7z", ".bak", ".config", ".manifest", ".hash", ".pdf", ".ico" };

    static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    { ".json", ".txt", ".csv", ".tsv", ".xml", ".yaml", ".yml", ".ini", ".po", ".pot", ".loc", ".lang", ".language", ".properties", ".strings", ".resx", ".xliff", ".xlf", ".lua", ".ink" };

    sealed class Matcher
    {
        readonly List<(string w, int weight)> words = new();
        readonly List<Regex> codes = new();
        public Matcher(IEnumerable<string> custom, bool useDefaults)
        {
            if (useDefaults)
            {
                foreach (var w in DefaultWords) words.Add((w, 20));
                foreach (var l in LanguageNames) words.Add((l, 8));
                foreach (var c in LanguageCodes) codes.Add(new Regex($@"(?<![A-Za-z]){Regex.Escape(c)}(?![A-Za-z])"));
            }
            foreach (var c in custom.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                var k = c.Trim();
                if (k.Length <= 3 || k.StartsWith("\"") || k.StartsWith("(")) codes.Add(new Regex(Regex.Escape(k)));
                else words.Add((k, 25));
            }
        }
        public (int score, List<string> hits) Match(string text, int mult = 1)
        {
            int s = 0; var hits = new List<string>();
            if (string.IsNullOrEmpty(text)) return (0, hits);
            foreach (var (w, weight) in words)
                if (text.Contains(w, StringComparison.OrdinalIgnoreCase)) { s += weight * mult; hits.Add(w); }
            foreach (var r in codes)
                if (r.IsMatch(text)) { s += 6 * mult; hits.Add(r.ToString().Replace("(?<![A-Za-z])", "").Replace("(?![A-Za-z])", "").Replace("\\", "")); }
            return (s, hits);
        }
        public int CountLanguages(string text) => LanguageNames.Count(l => text.Contains(l, StringComparison.Ordinal));
    }

    public sealed class Hit
    {
        public string Rel = "", Kind = "", Name = "", Type = "", Script = "";
        public int File; public long PathId; public int Score; public long Size; public int Count = 1;
        public List<string> Hits = new();
        public JsonObject ToJson() => new()
        {
            ["rel"] = Rel, ["kind"] = Kind, ["name"] = Name, ["type"] = Type, ["script"] = Script, ["file"] = File,
            ["pathId"] = PathId.ToString(), ["score"] = Score, ["size"] = Size, ["count"] = Count,
            ["hits"] = new JsonArray(Hits.Distinct().Take(12).Select(h => (JsonNode?)JsonValue.Create(h)).ToArray())
        };
    }

    public static List<Hit> Run(string root, IEnumerable<string> customKeywords, bool useDefaults, bool deep, Job job)
    {
        var m = new Matcher(customKeywords, useDefaults);
        var hits = new List<Hit>();
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !SkipExt.Contains(Path.GetExtension(f)) && !f.Contains(Path.DirectorySeparatorChar + "MonoBleedingEdge" + Path.DirectorySeparatorChar))
            .ToList();
        job.Log($"Files to check: {files.Count}");
        for (int i = 0; i < files.Count; i++)
        {
            job.Checkpoint();
            job.Progress = (double)i / files.Count;
            var path = files[i];
            var rel = Path.GetRelativePath(root, path);
            job.Message = rel;
            try
            {
                var ext = Path.GetExtension(path);
                var kind = UnityDoc.DetectKind(path);
                if (kind != UnityDoc.FileKind.None) ScanUnity(path, rel, m, deep, hits, job);
                else if (TextExt.Contains(ext) || ext == "")
                {
                    var fi = new FileInfo(path);
                    if (fi.Length == 0 || fi.Length > 200_000_000) continue;
                    var (s1, h1) = m.Match(rel, 2);
                    string sample;
                    using (var fs = File.OpenRead(path))
                    {
                        var buf = new byte[Math.Min(256 * 1024, fs.Length)]; fs.Read(buf, 0, buf.Length);
                        if (Extract.LooksBinary(buf)) { if (!TextExt.Contains(ext)) continue; sample = ""; }
                        else sample = Extract.DecodeText(buf);
                    }
                    var (s2, h2) = m.Match(sample);
                    int langs = m.CountLanguages(sample);
                    int score = s1 + Math.Min(s2, 60) + langs * 6;
                    if (score > 0 && (s1 > 0 || s2 >= 20 || langs >= 2))
                        hits.Add(new Hit { Rel = rel, Kind = "loose", Name = Path.GetFileName(path), Type = ext.TrimStart('.').ToUpperInvariant(), Score = score, Size = fi.Length, Hits = h1.Concat(h2).ToList(), File = -1, PathId = 0 });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { job.Log($"! {rel}: {e.Message}"); }
        }
        job.Progress = 1;
        return hits.OrderByDescending(h => h.Score).ToList();
    }

    static readonly string[] ComponentHints = { "LocalizeStringEvent", "LocalizeComponent", "LocalizeEvent", "Localize", "LocalizedTextComponent", "LocalizeTarget", "SetLanguage", "LanguageDropdown" };

    static void ScanUnity(string path, string rel, Matcher m, bool deep, List<Hit> hits, Job job)
    {
        using var doc = new UnityDoc(path, path);
        var (fileScore, fileHits) = m.Match(rel, 1);
        var groups = new Dictionary<string, Hit>();   // collapse identical components (same script, no name)
        foreach (var a in doc.ListAssets())
        {
            job.Checkpoint();
            if (a.TypeId is not ((int)AssetClassID.TextAsset or (int)AssetClassID.MonoBehaviour)) continue;
            var (s, h) = m.Match(a.Name, 2);
            var (s2, h2) = m.Match(a.Script, 1);
            int score = s + s2;
            var all = h.Concat(h2).ToList();
            string kind = a.TypeId == (int)AssetClassID.TextAsset ? "textasset" : "monobehaviour";
            var cls = a.Script.Split('.').Last();
            if (cls is "TMP_FontAsset" or "TMP_SpriteAsset" or "TMP_StyleSheet" or "TMP_ColorGradient" || a.Name.EndsWith(" SDF")) continue;
            bool component = ComponentHints.Any(c => cls.Equals(c, StringComparison.OrdinalIgnoreCase) || (cls.StartsWith("Localize") && !cls.Contains("Table") && !cls.Contains("Asset") && !cls.Contains("Data")));
            bool settingsLike = cls.Contains("Settings") || cls == "Locale" || cls.EndsWith("LocaleSelector");
            if (a.TypeId == (int)AssetClassID.MonoBehaviour)
            {
                var tf = doc.TopFields(a.File, a.PathId);
                if (tf.Contains("m_TableData") && tf.Contains("m_SharedData")) { kind = "stringtable"; score += 140; all.Add("StringTable"); component = false; settingsLike = false; }
                else if (tf.Contains("m_Entries") && tf.Contains("m_TableCollectionName")) { kind = "monobehaviour"; score += 25; all.Add("SharedTableData (keys)"); }
                else if (tf.Contains("mSource.mTerms") || (tf.Contains("mTerms") && tf.Contains("mLanguages"))) { kind = "i2"; score += 150; all.Add("I2 LanguageSource"); component = false; settingsLike = false; }
                else if (tf.Contains("m_LocaleId") && !tf.Contains("m_TableData")) settingsLike = true;
            }
            if (KnownClasses.Any(k => string.Equals(k, cls, StringComparison.OrdinalIgnoreCase))) { score += 40; all.Add("class " + cls); }
            if (deep && a.Size > 64 && !component)
            {
                try
                {
                    var raw = doc.RawBytes(a.File, a.PathId);
                    var sample = Encoding.UTF8.GetString(raw, 0, Math.Min(raw.Length, 512 * 1024));
                    var (s3, h3) = m.Match(sample);
                    int langs = m.CountLanguages(sample);
                    int strCount = a.TypeId == (int)AssetClassID.MonoBehaviour ? Extract.RawScan(raw.Length > 2_000_000 ? raw[..2_000_000] : raw).Count : 0;
                    score += Math.Min(s3, 40) + langs * 6 + Math.Min(strCount / 20, 30);
                    all.AddRange(h3);
                    if (langs >= 2) all.Add($"{langs} languages");
                    if (strCount >= 40) all.Add($"{strCount} strings");
                }
                catch { }
            }
            if (component) score = Math.Min(score, 12);
            if (settingsLike) score = Math.Min(score, 30);
            if (score <= 0 || (score < 20 && all.Count < 2)) continue;
            var hit = new Hit { Rel = rel, Kind = kind, Name = a.Name, Type = a.Type, Script = a.Script, File = a.File, PathId = a.PathId, Score = score + fileScore / 2, Size = a.Size, Hits = all.Concat(fileHits).ToList() };
            if (component && string.IsNullOrEmpty(a.Name))
            {
                var key = a.Script;
                if (groups.TryGetValue(key, out var g)) { g.Count++; continue; }
                hit.Count = 1; groups[key] = hit;
            }
            hits.Add(hit);
        }
        foreach (var g in groups.Values) if (g.Count > 1) g.Name = $"({g.Count}×) {g.Name}".Trim();
    }

    /// <summary>Manual: find a text anywhere in the game's Unity files (like UABEA search).</summary>
    public static List<Hit> FindText(string root, string needle, Job job)
    {
        var res = new List<Hit>();
        var nb = Encoding.UTF8.GetBytes(needle);
        var nbl = Encoding.UTF8.GetBytes(needle.ToLowerInvariant());
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => !SkipExt.Contains(Path.GetExtension(f))).ToList();
        for (int i = 0; i < files.Count; i++)
        {
            job.Checkpoint(); job.Progress = (double)i / files.Count;
            var rel = Path.GetRelativePath(root, files[i]); job.Message = rel;
            try
            {
                var kind = UnityDoc.DetectKind(files[i]);
                if (kind == UnityDoc.FileKind.None)
                {
                    if (!TextExt.Contains(Path.GetExtension(files[i]))) continue;
                    var txt = File.ReadAllText(files[i]);
                    if (txt.Contains(needle, StringComparison.OrdinalIgnoreCase))
                        res.Add(new Hit { Rel = rel, Kind = "loose", Name = Path.GetFileName(files[i]), Type = "file", File = -1, Hits = new() { Snip(txt, needle) } });
                    continue;
                }
                using var doc = new UnityDoc(files[i], files[i]);
                foreach (var a in doc.ListAssets())
                {
                    job.Checkpoint();
                    if (a.Size < 8) continue;
                    var raw = doc.RawBytes(a.File, a.PathId);
                    int idx = IndexOf(raw, nb);
                    if (idx < 0 && nbl.Length == nb.Length) idx = IndexOfCI(raw, nbl);
                    if (idx < 0) continue;
                    int s0 = Math.Max(0, idx - 40), s1 = Math.Min(raw.Length, idx + nb.Length + 60);
                    var snip = new string(Encoding.UTF8.GetString(raw, s0, s1 - s0).Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray());
                    res.Add(new Hit { Rel = rel, Kind = a.TypeId == 49 ? "textasset" : a.TypeId == 114 ? "monobehaviour" : "asset", Name = a.Name, Type = a.Type, Script = a.Script, File = a.File, PathId = a.PathId, Size = a.Size, Hits = new() { snip.Trim() } });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { job.Log($"! {rel}: {e.Message}"); }
        }
        job.Progress = 1;
        return res;
    }

    static string Snip(string t, string n)
    {
        int i = t.IndexOf(n, StringComparison.OrdinalIgnoreCase);
        int a = Math.Max(0, i - 40), b = Math.Min(t.Length, i + n.Length + 60);
        return t[a..b].Replace('\n', ' ').Replace('\r', ' ');
    }

    static int IndexOf(byte[] h, byte[] n)
    {
        if (n.Length == 0) return -1;
        return h.AsSpan().IndexOf(n);
    }

    static int IndexOfCI(byte[] h, byte[] lower)
    {
        for (int i = 0; i + lower.Length <= h.Length; i++)
        {
            int k = 0;
            for (; k < lower.Length; k++) { byte b = h[i + k]; if (b >= 'A' && b <= 'Z') b = (byte)(b + 32); if (b != lower[k]) break; }
            if (k == lower.Length) return i;
        }
        return -1;
    }
}

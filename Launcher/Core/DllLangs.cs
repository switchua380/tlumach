using System.Text.Json.Nodes;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TranslationLauncher.Core;

/// <summary>
/// Language lists in the game code (Mono builds: Managed\Assembly-CSharp.dll etc.). Finds enums with language members and
/// string arrays / List&lt;string&gt; initializers with language names or codes; can add Ukrainian to them.
/// Other places that mention several languages (switches, comparisons) are only reported.
/// </summary>
public static class DllLangs
{
    public sealed class Hit
    {
        public string Id = "", Dll = "", Kind = "", Where = "", Suggest = "";
        public List<string> Items = new();
        public bool CanAdd, HasUk;
        public JsonObject ToJson() => new()
        {
            ["id"] = Id, ["dll"] = Dll, ["kind"] = Kind, ["where"] = Where, ["suggest"] = Suggest, ["canAdd"] = CanAdd, ["hasUk"] = HasUk,
            ["items"] = new JsonArray(Items.Select(x => (JsonNode?)x).ToArray())
        };
    }

    static readonly string[] Native = { "Deutsch", "Français", "Español", "Italiano", "Polski", "Português", "Русский", "日本語", "한국어", "中文", "简体中文", "繁體中文", "Türkçe", "Čeština", "Nederlands", "Svenska", "Norsk", "Dansk", "Suomi", "Magyar", "Română", "Ελληνικά" };
    static readonly HashSet<string> Codes = new(Scanner.LanguageCodes.Concat(new[] { "RU", "BE", "HE", "HI", "SK", "SR", "HR", "LT", "LV", "ET", "SL" }), StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Names = new(Scanner.LanguageNames.Concat(Native).Concat(new[] { "Russian", "Simplified Chinese", "Traditional Chinese", "ChineseSimplified", "ChineseTraditional", "SChinese", "TChinese", "Portuguese_BR", "BrazilianPortuguese" }), StringComparer.OrdinalIgnoreCase);

    static readonly string[] SkipPrefixes = { "Unity", "System", "mscorlib", "Mono.", "netstandard", "Microsoft", "Newtonsoft", "DOTween", "Rewired", "Steamworks", "FMOD", "Facepunch", "Sirenix" };

    public static bool IsLanguage(string s)
    {
        s = s.Trim();
        if (s.Length == 0 || s.Length > 30) return false;
        if (Names.Contains(s) || Names.Contains(s.Replace("_", " "))) return true;
        if (s.Length == 2 && Codes.Contains(s)) return true;
        if (s.Length is 5 && (s[2] == '-' || s[2] == '_') && Codes.Contains(s[..2])) return true;   // en-US, zh_CN
        return false;
    }

    public static bool IsUkrainian(string s) => Extract.IsUkrainian(s) || s.Equals("ua", StringComparison.OrdinalIgnoreCase);

    /// <summary>Ukrainian written the same way as the other entries: name, native name, 2-letter code or culture code.</summary>
    public static string Suggest(IEnumerable<string> items)
    {
        var l = items.Where(IsLanguage).ToList();
        if (l.Count == 0) return "Ukrainian";
        if (l.Count(x => x.Length == 5 && (x[2] == '-' || x[2] == '_')) * 2 >= l.Count) { var x = l.First(x => x.Length == 5); return x[2] == '_' ? "uk_UA" : "uk-UA"; }
        if (l.Count(x => x.Length == 2) * 2 >= l.Count) return l.All(x => x.ToUpperInvariant() == x) ? "UK" : "uk";
        if (l.Any(x => Native.Contains(x, StringComparer.OrdinalIgnoreCase))) return "Українська";
        return "Ukrainian";
    }

    static string EnumName(string s) => s == "Українська" ? "Ukrainian" : s.Replace("-", "_");

    static bool LangContext(string s) => s.Contains("lang", StringComparison.OrdinalIgnoreCase) || s.Contains("locale", StringComparison.OrdinalIgnoreCase) || s.Contains("locali", StringComparison.OrdinalIgnoreCase);

    public static List<string> GameDlls(string managedDir) =>
        Directory.GetFiles(managedDir, "*.dll")
            .Where(f => { var n = Path.GetFileName(f); return !SkipPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) && new FileInfo(f).Length < 60_000_000; })
            .OrderByDescending(f => Path.GetFileName(f).StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase)).ToList();

    static AssemblyDefinition Load(byte[] data, string managedDir)
    {
        var res = new DefaultAssemblyResolver(); res.AddSearchDirectory(managedDir);
        return AssemblyDefinition.ReadAssembly(new MemoryStream(data), new ReaderParameters { AssemblyResolver = res, ReadingMode = ReadingMode.Immediate });
    }

    // ------------------------------------------------------------------ scan
    /// <param name="dllRel">name shown / used in ids (path relative to the game folder)</param>
    public static List<Hit> Scan(byte[] data, string dllRel, string managedDir)
    {
        var hits = new List<Hit>();
        using var asm = Load(data, managedDir);
        foreach (var type in asm.MainModule.GetTypes())
        {
            if (type.IsEnum)
            {
                var members = type.Fields.Where(f => f.IsStatic && f.IsLiteral).ToList();
                int n = members.Count(f => IsLanguage(f.Name));
                if (n >= 3 || n >= 2 && LangContext(type.Name))
                    hits.Add(new Hit
                    {
                        Id = $"enum|{dllRel}|{type.FullName}", Dll = dllRel, Kind = "enum", Where = type.FullName, CanAdd = true,
                        Items = members.Select(f => $"{f.Name} = {f.Constant}").ToList(), HasUk = members.Any(f => IsUkrainian(f.Name)),
                        Suggest = EnumName(Suggest(members.Select(f => f.Name)))
                    });
                continue;
            }
            foreach (var m in type.Methods)
            {
                if (!m.HasBody) continue;
                var ins = m.Body.Instructions;
                var inInit = new HashSet<Instruction>();
                int index = -1;
                foreach (var init in Initializers(m))
                {
                    index++;
                    var strs = init.Strings;
                    int n = strs.Count(IsLanguage);
                    if (n >= 3 || n >= 2 && (LangContext(m.Name) || LangContext(type.Name) || init.Field != null && LangContext(init.Field)))
                    {
                        hits.Add(new Hit
                        {
                            Id = $"{init.Kind}|{dllRel}|{m.FullName}|{index}", Dll = dllRel, Kind = init.Kind,
                            Where = $"{type.FullName}.{m.Name}" + (init.Field != null ? " → " + init.Field : ""), CanAdd = true,
                            Items = strs, HasUk = strs.Any(IsUkrainian), Suggest = Suggest(strs)
                        });
                        foreach (var i in init.Covered) inInit.Add(i);
                    }
                }
                // mentions only (switch / comparisons): report, no automatic change
                var loose = ins.Where(i => i.OpCode == OpCodes.Ldstr && !inInit.Contains(i)).Select(i => (string)i.Operand).Where(IsLanguage).Distinct().ToList();
                if (loose.Count >= 3)
                    hits.Add(new Hit
                    {
                        Id = $"code|{dllRel}|{m.FullName}", Dll = dllRel, Kind = "code", Where = $"{type.FullName}.{m.Name}",
                        Items = loose, HasUk = loose.Any(IsUkrainian), Suggest = Suggest(loose)
                    });
            }
        }
        return hits;
    }

    sealed class Init
    {
        public string Kind = "";                 // array | list
        public Instruction Start = null!;        // newarr / newobj
        public Instruction Last = null!;         // last stelem.ref / Add call of the literal elements
        public List<string> Strings = new();
        public List<Instruction> Covered = new();
        public string? Field;
    }

    static int? ConstInt(Instruction i) => i.OpCode.Code switch
    {
        Code.Ldc_I4_0 => 0, Code.Ldc_I4_1 => 1, Code.Ldc_I4_2 => 2, Code.Ldc_I4_3 => 3, Code.Ldc_I4_4 => 4,
        Code.Ldc_I4_5 => 5, Code.Ldc_I4_6 => 6, Code.Ldc_I4_7 => 7, Code.Ldc_I4_8 => 8,
        Code.Ldc_I4_S => (sbyte)i.Operand, Code.Ldc_I4 => (int)i.Operand, _ => null
    };

    /// <summary>new string[] { "a", "b" } and new List&lt;string&gt; { "a", "b" } with literal elements only.</summary>
    static IEnumerable<Init> Initializers(MethodDefinition m)
    {
        foreach (var i in m.Body.Instructions)
        {
            if (i.OpCode == OpCodes.Newarr && i.Operand is TypeReference tr && tr.FullName == "System.String" && i.Previous != null && ConstInt(i.Previous) is int count && count > 0)
            {
                var it = new Init { Kind = "array", Start = i };
                var cur = i.Next;
                for (int k = 0; k < count; k++)
                {
                    if (cur?.OpCode != OpCodes.Dup || ConstInt(cur.Next) != k || cur.Next.Next?.OpCode != OpCodes.Ldstr || cur.Next.Next.Next?.OpCode != OpCodes.Stelem_Ref) { it = null; break; }
                    it.Strings.Add((string)cur.Next.Next.Operand);
                    it.Covered.Add(cur.Next.Next);
                    it.Last = cur.Next.Next.Next;
                    cur = it.Last.Next;
                }
                if (it == null) continue;
                if (cur?.Operand is FieldReference f && (cur.OpCode == OpCodes.Stsfld || cur.OpCode == OpCodes.Stfld)) it.Field = f.Name;
                yield return it;
            }
            else if (i.OpCode == OpCodes.Newobj && i.Operand is MethodReference ctor && ctor.DeclaringType.FullName == "System.Collections.Generic.List`1<System.String>" && ctor.Parameters.Count == 0)
            {
                var it = new Init { Kind = "list", Start = i };
                var cur = i.Next;
                while (cur?.OpCode == OpCodes.Dup && cur.Next?.OpCode == OpCodes.Ldstr && cur.Next.Next?.Operand is MethodReference add && add.Name == "Add" && cur.Next.Next.OpCode.Code is Code.Callvirt or Code.Call)
                {
                    it.Strings.Add((string)cur.Next.Operand);
                    it.Covered.Add(cur.Next);
                    it.Last = cur.Next.Next;
                    cur = it.Last.Next;
                }
                if (it.Strings.Count == 0) continue;
                if (cur?.Operand is FieldReference f && (cur.OpCode == OpCodes.Stsfld || cur.OpCode == OpCodes.Stfld)) it.Field = f.Name;
                yield return it;
            }
        }
    }

    // ------------------------------------------------------------------ add Ukrainian
    /// <summary>Adds <paramref name="value"/> to the enum / array / list named by <paramref name="id"/>; returns the new dll bytes.</summary>
    public static byte[] Add(byte[] data, string id, string value, string managedDir)
    {
        var parts = id.Split('|');
        if (parts.Length < 3) throw new Exception("Bad id");
        using var asm = Load(data, managedDir);
        var mod = asm.MainModule;
        if (parts[0] == "enum")
        {
            var type = mod.GetTypes().FirstOrDefault(t => t.FullName == parts[2]) ?? throw new Exception("Enum not found: " + parts[2]);
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$")) throw new Exception("An enum member name may contain only Latin letters, digits and _");
            if (type.Fields.Any(f => f.Name == value)) throw new Exception("Already exists: " + value);
            var under = type.Fields.First(f => !f.IsStatic).FieldType;   // value__
            long max = type.Fields.Where(f => f.IsStatic && f.IsLiteral).Select(f => Convert.ToInt64(f.Constant)).DefaultIfEmpty(-1).Max();
            bool flags = type.CustomAttributes.Any(a => a.AttributeType.FullName == "System.FlagsAttribute");
            long next = flags ? (max <= 0 ? 1 : 1L << (int)Math.Ceiling(Math.Log2(max + 1))) : max + 1;
            object constant = under.FullName switch
            {
                "System.Byte" => (object)(byte)next, "System.SByte" => (sbyte)next, "System.Int16" => (short)next, "System.UInt16" => (ushort)next,
                "System.UInt32" => (uint)next, "System.Int64" => next, "System.UInt64" => (ulong)next, _ => (int)next
            };
            type.Fields.Add(new FieldDefinition(value, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, type) { Constant = constant });
        }
        else if (parts[0] is "array" or "list" && parts.Length >= 4)
        {
            int index = int.Parse(parts[^1]);   // n-th string initializer in the method (stays the same after adding)
            string methodName = string.Join("|", parts[2..^1]);
            var m = mod.GetTypes().SelectMany(t => t.Methods).FirstOrDefault(x => x.FullName == methodName && x.HasBody) ?? throw new Exception("Method not found: " + methodName);
            var init = Initializers(m).Skip(index).FirstOrDefault() is { } x0 && x0.Kind == parts[0] ? x0 : null;
            init = init ?? throw new Exception("List not found (the dll has changed?) — search again");
            if (init.Strings.Contains(value)) throw new Exception("Already exists: " + value);
            var il = m.Body.GetILProcessor();
            if (init.Kind == "array")
            {
                var size = init.Start.Previous;
                size.OpCode = OpCodes.Ldc_I4; size.Operand = init.Strings.Count + 1;
                var seq = new[] { il.Create(OpCodes.Dup), il.Create(OpCodes.Ldc_I4, init.Strings.Count), il.Create(OpCodes.Ldstr, value), il.Create(OpCodes.Stelem_Ref) };
                var after = init.Last; foreach (var x in seq) { il.InsertAfter(after, x); after = x; }
            }
            else
            {
                var call = init.Last;
                var seq = new[] { il.Create(OpCodes.Dup), il.Create(OpCodes.Ldstr, value), il.Create(call.OpCode, (MethodReference)call.Operand) };
                var after = init.Last; foreach (var x in seq) { il.InsertAfter(after, x); after = x; }
            }
            m.Body.MaxStackSize = Math.Max(m.Body.MaxStackSize, 4);
        }
        else throw new Exception("This place cannot be changed automatically — edit it in dnSpy");
        var ms = new MemoryStream();
        asm.Write(ms);
        return ms.ToArray();
    }
}

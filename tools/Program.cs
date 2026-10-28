// Build-time tool: turns TMP + TextCore C# sources into serialization schemas for TMP_FontAsset.
// usage: schemagen <workDir> <mscorlib.dll> <outSchema> <unityVersion> <tmpSrcDir> [textCoreSrcDir]
//        schemagen --from-dll <managedDir> <outSchema> <unityVersion>
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args[0] == "--from-dll")
{
    var gen0 = new MonoCecilTempGenerator(args[1]);
    var t0 = gen0.Read(Path.Combine(args[1], "Unity.TextMeshPro.dll"), "TMPro", "TMP_FontAsset", new UnityVersion(args[3]));
    File.WriteAllText(args[2], Schema(t0));
    Console.WriteLine($"ok {args[2]} fields={t0.Count}");
    return 0;
}

string work = args[0], corlib = args[1], outSchema = args[2], uver = args[3];
var srcDirs = args.Skip(4).ToArray();
Directory.CreateDirectory(work);

// ---- preprocessor symbols for the target Unity version
var parts = uver.Split('.');
int major = int.Parse(parts[0]), minor = int.Parse(parts[1]);
var syms = new List<string> { "UNITY_STANDALONE", "UNITY_STANDALONE_WIN", "ENABLE_MONO", "NET_4_6", "UNITY_5_3_OR_NEWER" };
foreach (var y in new[] { 5, 2017, 2018, 2019, 2020, 2021, 2022, 2023, 6000 })
    for (int m = 0; m <= 6; m++)
    {
        bool le = y < major || (y == major && m <= minor);
        if (le) syms.Add($"UNITY_{y}_{m}_OR_NEWER");
    }
syms.Add($"UNITY_{major}_{minor}");
syms.Add($"UNITY_{major}");
var popt = new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: syms);

// ---- collect type declarations
var decls = new Dictionary<string, (BaseTypeDeclarationSyntax decl, string ns)>();
foreach (var dir in srcDirs)
    foreach (var f in Directory.GetFiles(dir, "*.cs"))
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(f), popt);
        foreach (var t in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (t.Parent is BaseTypeDeclarationSyntax) continue; // skip nested (not used by TMP_FontAsset fields)
            string ns = t.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "";
            string name = t.Identifier.Text;
            if (!decls.ContainsKey(name) || ns.StartsWith("TMPro")) decls[name] = (t, ns);
        }
    }

var unityKnown = new HashSet<string> { "Object", "ScriptableObject", "Component", "Behaviour", "MonoBehaviour", "GameObject", "Material", "Shader",
    "Texture", "Texture2D", "Font", "Sprite", "TextAsset", "Color", "Color32", "Vector2", "Vector3", "Vector4", "Rect", "Matrix4x4" };
var prim = new HashSet<string> { "int", "uint", "float", "double", "bool", "byte", "sbyte", "short", "ushort", "long", "ulong", "char", "string",
    "List", "Dictionary", "Int32", "UInt32", "Single", "String", "Boolean", "Byte" };

var included = new List<string>();
var assumedEnums = new HashSet<string>();
var queue = new Queue<string>(); queue.Enqueue("TMP_FontAsset");
while (queue.Count > 0)
{
    var n = queue.Dequeue();
    if (included.Contains(n)) continue;
    if (!decls.TryGetValue(n, out var d)) { if (!unityKnown.Contains(n) && !prim.Contains(n)) assumedEnums.Add(n); continue; }
    included.Add(n);
    if (d.decl is TypeDeclarationSyntax td)
    {
        if (td.BaseList != null)
            foreach (var bt in td.BaseList.Types.Take(1)) foreach (var id in Idents(bt.Type)) queue.Enqueue(id);
        foreach (var fd in td.Members.OfType<FieldDeclarationSyntax>())
        {
            if (fd.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ConstKeyword))) continue;
            foreach (var id in Idents(fd.Declaration.Type)) queue.Enqueue(id);
        }
    }
}

static IEnumerable<string> Idents(TypeSyntax t)
{
    foreach (var n in t.DescendantNodesAndSelf().OfType<SimpleNameSyntax>()) yield return n.Identifier.Text;
}

// ---- emit stub source
var sb = new StringBuilder();
sb.AppendLine("using System; using System.Collections.Generic; using UnityEngine;");
sb.AppendLine("using UnityEngine.TextCore; using UnityEngine.TextCore.LowLevel; using TMPro;");
sb.AppendLine("namespace UnityEngine.TextCore { } namespace UnityEngine.TextCore.LowLevel { } namespace TMPro { }");
foreach (var name in included)
{
    var (decl, ns) = decls[name];
    sb.AppendLine($"namespace {(ns == "" ? "TMPro" : ns)} {{");
    bool serial = decl.AttributeLists.SelectMany(a => a.Attributes).Any(a => a.Name.ToString().Contains("Serializable"));
    if (serial) sb.Append("[System.Serializable] ");
    if (decl is EnumDeclarationSyntax ed)
    {
        string bt = ed.BaseList?.Types.FirstOrDefault()?.ToString() ?? "int";
        sb.AppendLine($"public enum {name} : {bt} {{ }}");
    }
    else if (decl is TypeDeclarationSyntax td)
    {
        string kind = td is StructDeclarationSyntax ? "struct" : "class";
        string bases = "";
        if (kind == "class" && td.BaseList != null)
        {
            var first = td.BaseList.Types.First().Type.ToString();
            var simple = first.Split('.').Last();
            if (included.Contains(simple) || unityKnown.Contains(simple)) bases = " : " + first;
        }
        string abs = td.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)) ? "abstract " : "";
        sb.AppendLine($"public {abs}{kind} {name}{bases} {{");
        foreach (var fd in td.Members.OfType<FieldDeclarationSyntax>())
        {
            var mods = fd.Modifiers.Select(m => m.Text).Where(m => m is "public" or "private" or "internal" or "protected" or "readonly" or "static" or "const").ToList();
            if (mods.Contains("static") || mods.Contains("const")) continue;
            var attrs = fd.AttributeLists.SelectMany(a => a.Attributes).Select(a => a.Name.ToString()).ToList();
            var keep = new List<string>();
            if (attrs.Any(a => a.EndsWith("SerializeField"))) keep.Add("UnityEngine.SerializeField");
            if (attrs.Any(a => a.EndsWith("SerializeReference"))) keep.Add("UnityEngine.SerializeReference");
            if (attrs.Any(a => a.EndsWith("NonSerialized"))) keep.Add("System.NonSerialized");
            string a2 = keep.Count > 0 ? "[" + string.Join(", ", keep) + "] " : "";
            var access = mods.FirstOrDefault(m => m is "public" or "private" or "internal" or "protected") ?? "private";
            if (mods.Contains("protected") && mods.Contains("internal")) access = "protected internal";
            string ro = mods.Contains("readonly") ? "readonly " : "";
            foreach (var v in fd.Declaration.Variables)
                sb.AppendLine($"  {a2}{access} {ro}{fd.Declaration.Type} {v.Identifier.Text};");
        }
        sb.AppendLine("}");
    }
    sb.AppendLine("}");
}
foreach (var e in assumedEnums) sb.AppendLine($"namespace TMPro {{ public enum {e} : int {{ }} }}");
File.WriteAllText(Path.Combine(work, "stub.cs"), sb.ToString());

// ---- compile stub UnityEngine + TMP
var corRef = MetadataReference.CreateFromFile(corlib);
var copt = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable)
    .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> { ["CS0169"] = ReportDiagnostic.Suppress, ["CS0649"] = ReportDiagnostic.Suppress, ["CS0414"] = ReportDiagnostic.Suppress });
string ueDll = Path.Combine(work, "UnityEngine.CoreModule.dll");
if (!File.Exists(ueDll))
{
    var ue = CSharpCompilation.Create("UnityEngine.CoreModule", new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UnityStub.cs"))) }, new[] { corRef }, copt);
    var r = ue.Emit(ueDll);
    if (!r.Success) { foreach (var d in r.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error)) Console.WriteLine(d); return 1; }
}
File.Copy(corlib, Path.Combine(work, "mscorlib.dll"), true);
var coreLibDir = Path.GetDirectoryName(corlib)!;
File.Copy(Path.Combine(coreLibDir, "System.Core.dll"), Path.Combine(work, "System.Core.dll"), true);
File.Copy(Path.Combine(coreLibDir, "System.dll"), Path.Combine(work, "System.dll"), true);
var tmpComp = CSharpCompilation.Create("Unity.TextMeshPro", new[] { CSharpSyntaxTree.ParseText(sb.ToString()) },
    new[] { corRef, MetadataReference.CreateFromFile(ueDll), MetadataReference.CreateFromFile(Path.Combine(coreLibDir, "System.Core.dll")), MetadataReference.CreateFromFile(Path.Combine(coreLibDir, "System.dll")) }, copt);
var tmpDll = Path.Combine(work, "Unity.TextMeshPro.dll");
var res = tmpComp.Emit(tmpDll);
if (!res.Success)
{
    foreach (var d in res.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error).Take(30)) Console.WriteLine(d);
    return 1;
}

// ---- template via the same generator UABEA uses
var gen = new MonoCecilTempGenerator(work);
var temps = gen.Read(tmpDll, "TMPro", "TMP_FontAsset", new UnityVersion(uver));
gen.Dispose();
File.WriteAllText(outSchema, Schema(temps));
Console.WriteLine($"ok {outSchema} fields={temps.Count} types={included.Count} assumedEnums=[{string.Join(",", assumedEnums)}]");
return 0;

static string Schema(List<AssetTypeTemplateField> fields)
{
    var sb = new StringBuilder();
    void W(AssetTypeTemplateField f, int d)
    {
        sb.Append(d).Append('\t').Append(f.Type).Append('\t').Append(f.Name).Append('\t').Append((int)f.ValueType).Append('\t')
          .Append(f.IsArray ? 1 : 0).Append('\t').Append(f.IsAligned ? 1 : 0).Append('\t').Append(f.HasValue ? 1 : 0).Append('\n');
        foreach (var c in f.Children) W(c, d + 1);
    }
    foreach (var f in fields) W(f, 0);
    return sb.ToString();
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace TranslationLauncher.Core;

/// <summary>UABEA-style JSON dump of a whole asset (export + import).</summary>
public static class Dump
{
    public static JsonNode? ToJson(AssetTypeValueField f)
    {
        var tf = f.TemplateField;
        if (tf.IsArray)
        {
            if (f.Value?.ValueType == AssetValueType.ByteArray)
            {
                var a = new JsonArray(); foreach (var b in f.AsByteArray) a.Add(b); return a;
            }
            var arr = new JsonArray();
            foreach (var c in f.Children) arr.Add(ToJson(c));
            return arr;
        }
        if (f.Children.Count == 0 || tf.ValueType == AssetValueType.String)
        {
            if (f.Value == null) return null;
            return f.Value.ValueType switch
            {
                AssetValueType.String => JsonValue.Create(f.AsString),
                AssetValueType.Bool => JsonValue.Create(f.AsBool),
                AssetValueType.Float => JsonValue.Create(f.AsFloat),
                AssetValueType.Double => JsonValue.Create(f.AsDouble),
                AssetValueType.Int8 => JsonValue.Create(f.AsSByte),
                AssetValueType.UInt8 => JsonValue.Create(f.AsByte),
                AssetValueType.Int16 => JsonValue.Create(f.AsShort),
                AssetValueType.UInt16 => JsonValue.Create(f.AsUShort),
                AssetValueType.Int32 => JsonValue.Create(f.AsInt),
                AssetValueType.UInt32 => JsonValue.Create(f.AsUInt),
                AssetValueType.Int64 => JsonValue.Create(f.AsLong),
                AssetValueType.UInt64 => JsonValue.Create(f.AsULong),
                AssetValueType.ByteArray => new JsonArray(f.AsByteArray.Select(b => (JsonNode?)JsonValue.Create(b)).ToArray()),
                _ => JsonValue.Create(f.Value.AsObject?.ToString())
            };
        }
        var o = new JsonObject();
        foreach (var c in f.Children)
        {
            // vector / map / string wrappers -> {"Array":[...]} like UABEA
            if (c.Children.Count == 1 && c.Children[0].TemplateField.IsArray && c.TemplateField.ValueType != AssetValueType.String)
                o[c.FieldName] = new JsonObject { ["Array"] = ToJson(c.Children[0]) };
            else o[c.FieldName] = ToJson(c);
        }
        return o;
    }

    // ------------------------------------------------------------------ UABEA text dump (.txt)
    // "0 MonoBehaviour Base" / " 1 string m_Name = \"x\"" / " 0 Array Array (3 items)" + " 0 int size = 3" + " [0]" ...
    static readonly System.Text.RegularExpressions.Regex TxtField = new(@"^(?<a>\d) (?<rest>.+)$");
    public static bool IsTextDump(string text)
    {
        var first = text.TrimStart('﻿').Split('\n', 2)[0].TrimEnd('\r');
        return System.Text.RegularExpressions.Regex.IsMatch(first, @"^\d \S.* Base$");
    }
    /// <summary>Root type of a text dump ("MonoBehaviour", "TextAsset" …), or null.</summary>
    public static string? TextDumpType(string text)
    {
        if (!IsTextDump(text)) return null;
        var first = text.TrimStart('﻿').Split('\n', 2)[0].TrimEnd('\r');
        return first[2..^5].Trim();
    }
    /// <summary>Converts a UABEA text dump into the same JSON layout as <see cref="ToJson"/>.</summary>
    public static JsonNode? FromText(string text)
    {
        var lines = text.TrimStart('﻿').Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToArray();
        if (lines.Length == 0 || !IsTextDump(lines[0])) return null;
        int pos = 0;
        return Node(ref pos, lines);
    }
    static int Depth(string l) { int d = 0; while (d < l.Length && l[d] == ' ') d++; return d; }
    static JsonNode? Node(ref int pos, string[] lines)
    {
        var line = lines[pos]; int depth = Depth(line);
        var m = TxtField.Match(line.Trim()); pos++;
        if (!m.Success) return null;
        var rest = m.Groups["rest"].Value;
        int eq = IndexOfAssign(rest);
        if (eq >= 0) return Leaf(rest[(eq + 3)..]);
        var arr = System.Text.RegularExpressions.Regex.Match(rest, @"\((\d+) items\)$");
        if (arr.Success)
        {
            var a = new JsonArray();
            while (pos < lines.Length && Depth(lines[pos]) > depth)
            {
                var t = lines[pos].Trim();
                if (t.StartsWith('[') && t.EndsWith(']')) { pos++; if (pos < lines.Length && Depth(lines[pos]) > depth) a.Add(Node(ref pos, lines)); }
                else pos++;   // "0 int size = N"
            }
            return a;
        }
        var o = new JsonObject();
        while (pos < lines.Length && Depth(lines[pos]) > depth)
        {
            var cm = TxtField.Match(lines[pos].Trim());
            if (!cm.Success) { pos++; continue; }
            var cr = cm.Groups["rest"].Value; int ce = IndexOfAssign(cr);
            var head = ce >= 0 ? cr[..ce] : System.Text.RegularExpressions.Regex.Replace(cr, @" \(\d+ items\)$", "");
            var name = head[(head.LastIndexOf(' ') + 1)..];
            o[name] = Node(ref pos, lines);
        }
        return o;
    }
    static int IndexOfAssign(string rest)
    {
        // the first " = " that is outside of the type / name part (names and types never contain " = ")
        return rest.IndexOf(" = ", StringComparison.Ordinal);
    }
    static JsonNode? Leaf(string v)
    {
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 1; i < v.Length - 1; i++)
            {
                if (v[i] == '\\' && i + 1 < v.Length - 1)
                {
                    char n = v[++i];
                    sb.Append(n switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0', _ => n });
                }
                else sb.Append(v[i]);
            }
            return JsonValue.Create(sb.ToString());
        }
        if (v == "true" || v == "false") return JsonValue.Create(v == "true");
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        if (ulong.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ul)) return JsonValue.Create(ul);
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
        return JsonValue.Create(v);
    }
    /// <summary>JSON dump or UABEA text dump → JSON layout.</summary>
    public static JsonNode? ParseAny(string text) => IsTextDump(text) ? FromText(text) : JsonNode.Parse(text);

    public static string ToJsonText(AssetTypeValueField f) =>
        ToJson(f)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() });

    /// <summary>Fill an existing value tree from a dump (same layout). Missing keys keep current values.</summary>
    public static void FromJson(AssetTypeValueField f, JsonNode? n)
    {
        if (n == null) return;
        var tf = f.TemplateField;
        if (tf.IsArray)
        {
            var arr = n as JsonArray ?? (n is JsonObject jo && jo["Array"] is JsonArray ja ? ja : null);
            if (arr == null) return;
            if (f.Value?.ValueType == AssetValueType.ByteArray) { f.AsByteArray = arr.Select(x => (byte)Num(x!.AsValue())).ToArray(); return; }
            var list = new List<AssetTypeValueField>();
            foreach (var item in arr)
            {
                var e = ValueBuilder.DefaultValueFieldFromArrayTemplate(f);
                FromJson(e, item);
                list.Add(e);
            }
            f.Children = list; f.AsArray = new AssetTypeArrayInfo(list.Count);
            return;
        }
        if (f.Children.Count == 0 || tf.ValueType == AssetValueType.String)
        {
            if (f.Value == null) return;
            var v = n.AsValue();
            switch (f.Value.ValueType)
            {
                case AssetValueType.String: f.AsString = v.ToString(); break;
                case AssetValueType.Bool: f.AsBool = v.GetValueKind() == JsonValueKind.True || (v.GetValueKind() == JsonValueKind.Number && Num(v) != 0); break;
                case AssetValueType.Float: f.AsFloat = (float)Num(v); break;
                case AssetValueType.Double: f.AsDouble = Num(v); break;
                case AssetValueType.Int8: f.AsSByte = (sbyte)Num(v); break;
                case AssetValueType.UInt8: f.AsByte = (byte)Num(v); break;
                case AssetValueType.Int16: f.AsShort = (short)Num(v); break;
                case AssetValueType.UInt16: f.AsUShort = (ushort)Num(v); break;
                case AssetValueType.Int32: f.AsInt = (int)Num(v); break;
                case AssetValueType.UInt32: f.AsUInt = (uint)Num(v); break;
                case AssetValueType.Int64: f.AsLong = long.Parse(v.ToJsonString().Trim('"'), CultureInfo.InvariantCulture); break;
                case AssetValueType.UInt64: f.AsULong = ulong.Parse(v.ToJsonString().Trim('"'), CultureInfo.InvariantCulture); break;
            }
            return;
        }
        if (n is not JsonObject obj) return;
        foreach (var c in f.Children)
        {
            if (!obj.TryGetPropertyValue(c.FieldName, out var sub)) continue;
            if (c.Children.Count == 1 && c.Children[0].TemplateField.IsArray && c.TemplateField.ValueType != AssetValueType.String)
                FromJson(c.Children[0], sub is JsonObject so && so["Array"] != null ? so["Array"] : sub);
            else FromJson(c, sub);
        }
    }

    static double Num(JsonValue v)
    {
        var k = v.GetValueKind();
        if (k == JsonValueKind.Number) return double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
        if (k == JsonValueKind.True) return 1;
        if (k == JsonValueKind.False) return 0;
        return double.Parse(v.ToString(), CultureInfo.InvariantCulture);
    }
}

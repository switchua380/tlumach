using System.Text;
using TMPFontReplacer;

Console.OutputEncoding = Encoding.UTF8;
try { Console.InputEncoding = Encoding.UTF8; } catch { }

const string Help = @"TMP Font Replacer — генерує TextMeshPro SDF-атлас із TTF і вбудовує його в файл гри.

Використання:
  TMPFontReplacer <новий.ttf> <файл-ассетів> <назва TMP-шрифту> [опції]
  TMPFontReplacer --list <файл-ассетів>          показати TMP-шрифти у файлі
  TMPFontReplacer                                 інтерактивний режим (питає все по черзі)

Файл-ассетів: sharedassetsN.assets / resources.assets / levelN / data.unity3d / .bundle / .unity3d
(UnityFS, а також старі UnityWeb/UnityRaw) — будь-яка версія Unity від 3.x до 6.x.
Назва: TMP-шрифт (TMP_FontAsset, TextCore FontAsset) або звичайний Unity Font (для uGUI Text).

Опції:
  --out <файл>          зберегти в інший файл (за замовчуванням — замінити вихідний, з .bak-копією)
  --managed <папка>     папка Managed (Mono) або папка з global-metadata.dat (IL2CPP), якщо не знайдено автоматично
  --unity-version <v>   версія Unity, якщо її не видно у файлі (напр. 2019.4.40f1)
  --size <N>            фіксований розмір семплінгу (за замовчуванням — найбільший, що вміщується)
  --padding <N>         padding (за замовчуванням — як в оригіналі)
  --atlas <W>x<H>       розмір атласу (за замовчуванням — як в оригіналі)
  --chars ""<текст>""     додати ці символи
  --chars-file <файл>   додати всі символи з текстового файлу (наприклад, з перекладу)
  --only-original       тільки символи оригінального шрифту (без автододавання кирилиці)
  --no-kerning          не переносити кернінг із TTF
  --no-backup           не створювати .bak
  --no-preview          не зберігати PNG-прев'ю атласу
  --bitmap              растровий атлас замість SDF
  --builtin-schemas     не читати скрипти гри, а одразу брати вбудовані схеми TMP
";

static string Unquote(string s) => s.Trim().Trim('"', '\'').Trim();

var opt = new Options();
var pos = new List<string>();
bool interactive = false;
try
{
    for (int i = 0; i < args.Length; i++)
    {
        string a = args[i];
        string Next() => i + 1 < args.Length ? args[++i] : throw new Exception($"Після {a} потрібне значення");
        switch (a.ToLowerInvariant())
        {
            case "-h": case "--help": case "/?": Console.WriteLine(Help); return 0;
            case "--list": opt.ListOnly = true; break;
            case "--out": opt.OutFile = Unquote(Next()); break;
            case "--managed": opt.ManagedDir = Unquote(Next()); break;
            case "--unity-version": opt.UnityVersion = Next(); break;
            case "--size": opt.PointSize = int.Parse(Next()); break;
            case "--padding": opt.Padding = int.Parse(Next()); break;
            case "--atlas":
                var wh = Next().ToLowerInvariant().Split('x');
                opt.AtlasW = int.Parse(wh[0]); opt.AtlasH = int.Parse(wh.Length > 1 ? wh[1] : wh[0]); break;
            case "--chars": opt.ExtraChars = Next(); break;
            case "--chars-file": opt.CharsFile = Unquote(Next()); break;
            case "--only-original": opt.OnlyOriginalChars = true; break;
            case "--no-kerning": opt.NoKerning = true; break;
            case "--no-backup": opt.NoBackup = true; break;
            case "--no-preview": opt.SavePreview = false; break;
            case "--bitmap": opt.ForceBitmap = true; break;
            case "--builtin-schemas": opt.BuiltinSchemas = true; break;
            default: pos.Add(Unquote(a)); break;
        }
    }

    if (opt.ListOnly)
    {
        if (pos.Count < 1) throw new Exception("Вкажіть файл: --list <файл-ассетів>");
        opt.AssetFile = pos[^1];
        foreach (var f in new FontPatcher(opt, Console.WriteLine).ListFonts()) Console.WriteLine("  " + f);
        return 0;
    }

    if (pos.Count < 3)
    {
        // interactive: ask only for what is missing (files may be dropped onto the .bat)
        interactive = true;
        foreach (var p in pos)
        {
            if (p.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)) opt.TtfPath = p;
            else if (File.Exists(p)) opt.AssetFile = p;
        }
        Console.WriteLine("=== TMP Font Replacer ===  (шляхи можна перетягувати у вікно мишкою)\n");
        if (opt.TtfPath == "") { Console.Write("1) Новий шрифт .ttf: "); opt.TtfPath = Unquote(Console.ReadLine() ?? ""); }
        else Console.WriteLine("1) Новий шрифт: " + opt.TtfPath);
        if (opt.AssetFile == "") { Console.Write("2) Файл гри, де лежить шрифт (.assets / .bundle): "); opt.AssetFile = Unquote(Console.ReadLine() ?? ""); }
        else Console.WriteLine("2) Файл гри: " + opt.AssetFile);
        Console.WriteLine("   Шукаю TMP-шрифти у файлі...");
        var fonts = new FontPatcher(new Options { AssetFile = opt.AssetFile, ManagedDir = opt.ManagedDir }, _ => { }).ListFonts();
        for (int k = 0; k < fonts.Count; k++) Console.WriteLine($"   [{k + 1}] {fonts[k]}");
        Console.Write("3) Назва шрифту (або номер зі списку): ");
        var ans = Unquote(Console.ReadLine() ?? "");
        if (int.TryParse(ans, out var num) && num >= 1 && num <= fonts.Count) ans = fonts[num - 1].Split("   (")[0];
        opt.FontName = ans;
        Console.WriteLine();
    }
    else
    {
        opt.TtfPath = pos[0]; opt.AssetFile = pos[1]; opt.FontName = pos[2];
    }

    if (!File.Exists(opt.TtfPath)) throw new Exception($"Файл шрифту не знайдено: {opt.TtfPath}");
    if (!File.Exists(opt.AssetFile)) throw new Exception($"Файл ассетів не знайдено: {opt.AssetFile}");

    var sw = System.Diagnostics.Stopwatch.StartNew();
    new FontPatcher(opt, Console.WriteLine).Run();
    Console.WriteLine($"Час: {sw.Elapsed.TotalSeconds:0.0} c");
    if (interactive) { Console.WriteLine("\nНатисніть Enter, щоб закрити."); Console.ReadLine(); }
    return 0;
}
catch (Exception e)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("ПОМИЛКА: " + e.Message);
    Console.ResetColor();
    if (Environment.GetEnvironmentVariable("TFR_DEBUG") == "1") Console.WriteLine(e);
    if (interactive) { Console.WriteLine("\nНатисніть Enter, щоб закрити."); Console.ReadLine(); }
    return 2;
}

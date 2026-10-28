using System.Diagnostics;
using System.Text;
using TranslationLauncher;

// Tlumach.exe is a GUI apphost: there is no console, and setting the encoding then throws
try { Console.OutputEncoding = Encoding.UTF8; } catch { }
var appDir = AppContext.BaseDirectory;
// portable: keep data next to the program when possible, else in %APPDATA%
string dataDir = Path.Combine(appDir, "data");
try { Directory.CreateDirectory(dataDir); File.WriteAllText(Path.Combine(dataDir, ".write_test"), "ok"); File.Delete(Path.Combine(dataDir, ".write_test")); }
catch
{
    var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    dataDir = Path.Combine(roaming, "Tlumach");
    var old = Path.Combine(roaming, "UnityTranslationLauncher");   // name before the rename: keep existing settings
    if (!Directory.Exists(dataDir) && Directory.Exists(old)) dataDir = old;
}
AppLog.Init(dataDir);
Store.Load(dataDir);
Store.Save();

// ---- single instance: if the launcher is already running, just open its window again
var instFile = Path.Combine(dataDir, "instance.json");
try
{
    if (File.Exists(instFile) && !args.Contains("--port"))
    {
        var inst = System.Text.Json.JsonDocument.Parse(File.ReadAllText(instFile)).RootElement;
        var oldUrl = inst.GetProperty("url").GetString()!;
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var tok = inst.GetProperty("token").GetString()!;
        var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, new Uri(new Uri(oldUrl), "/api/heartbeat"));
        req.Headers.Add("X-Token", tok); req.Content = new System.Net.Http.StringContent("{}");
        if (http.Send(req).IsSuccessStatusCode) { if (!args.Contains("--no-browser")) OpenWindow(oldUrl); return 0; }
    }
}
catch { }

var cts = new CancellationTokenSource();
var server = new HttpServer(args.Contains("--port") ? int.Parse(args[Array.IndexOf(args, "--port") + 1]) : 47120, Api.Handle);
if (args.Contains("--token")) Api.Token = args[Array.IndexOf(args, "--token") + 1];
Api.OnExit = () => { cts.Cancel(); server.Stop(); };
var url = $"http://127.0.0.1:{server.Port}/?t={Api.Token}";
AppLog.Info($"Launcher started: {url}  data: {dataDir}");
Console.WriteLine("Tlumach (Тлумач) " + Api.Version);
Console.WriteLine("Інтерфейс: " + url);
Console.WriteLine("Не закривайте це вікно, поки працюєте. Для виходу — кнопка «Вихід» у програмі.");

try { File.WriteAllText(instFile, new System.Text.Json.Nodes.JsonObject { ["url"] = url, ["token"] = Api.Token, ["pid"] = Environment.ProcessId }.ToJsonString()); } catch { }
if (!args.Contains("--no-browser")) OpenWindow(url);
// the window was closed without «Exit»: stop after a while if nothing is left unsaved / running
// (with unsaved edits the launcher keeps running — starting it again reopens the same session)
if (!args.Contains("--no-browser") && !args.Contains("--stay"))
    _ = Task.Run(async () =>
    {
        while (!cts.IsCancellationRequested)
        {
            await Task.Delay(15000);
            if ((DateTime.UtcNow - Api.LastSeen).TotalSeconds > 150 && !Api.Busy) { AppLog.Info("Window closed — stopping"); Api.OnExit?.Invoke(); break; }
        }
    });
server.Run(cts.Token);
try { File.Delete(instFile); } catch { }
AppLog.Info("Launcher stopped");
return 0;

static void OpenWindow(string url)
{
    try
    {
        if (OperatingSystem.IsWindows())
        {
            // Edge / Chrome "app" mode = a normal program window without browser toolbars (works offline)
            var cands = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
            };
            var exe = cands.FirstOrDefault(File.Exists);
            if (exe != null)
            {
                var profile = Path.Combine(Store.DataDir, "window-profile");
                Process.Start(new ProcessStartInfo(exe, $"--app=\"{url}\" --window-size=1400,900 --user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check --disable-sync --disable-features=Translate,msImplicitSignin,msEdgeSyncConsent,msSignInPromo") { UseShellExecute = false });
                return;
            }
        }
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch (Exception e) { Console.WriteLine("Відкрийте у браузері: " + url + " (" + e.Message + ")"); }
}

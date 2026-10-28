using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace TranslationLauncher;

/// <summary>Background operation with progress, pause/resume and cancel.</summary>
public sealed class Job
{
    static int nextId;
    public readonly int Id = Interlocked.Increment(ref nextId);
    public string Kind = "";
    public volatile string Status = "running";   // running | paused | cancelled | done | error
    public double Progress;
    public string Message = "";
    public JsonNode? Result;
    public string? Error;
    readonly List<string> log = new();
    readonly CancellationTokenSource cts = new();
    readonly ManualResetEventSlim resume = new(true);
    public DateTime Started = DateTime.Now;

    public void Log(string s) { lock (log) log.Add($"[{DateTime.Now:HH:mm:ss}] {s}"); AppLog.Info($"[{Kind}#{Id}] {s}"); }
    public List<string> LogLines(int from) { lock (log) return log.Skip(from).ToList(); }

    /// <summary>Call often inside long loops: blocks while paused, throws when cancelled.</summary>
    public void Checkpoint()
    {
        cts.Token.ThrowIfCancellationRequested();
        if (!resume.IsSet) { resume.Wait(cts.Token); }
    }

    public void Pause() { if (Status == "running") { resume.Reset(); Status = "paused"; Log("⏸ paused"); } }
    public void Resume() { if (Status == "paused") { resume.Set(); Status = "running"; Log("▶ resumed"); } }
    public void Cancel() { if (Status is "running" or "paused") { cts.Cancel(); resume.Set(); } }

    public JsonObject ToJson(int logFrom = 0) => new()
    {
        ["id"] = Id, ["kind"] = Kind, ["status"] = Status, ["progress"] = Math.Round(Progress, 4), ["message"] = Message,
        ["error"] = Error, ["result"] = Result?.DeepClone(), ["log"] = new JsonArray(LogLines(logFrom).Select(l => (JsonNode?)JsonValue.Create(l)).ToArray())
    };

    public static readonly ConcurrentDictionary<int, Job> All = new();

    public static Job Start(string kind, Func<Job, JsonNode?> work)
    {
        var j = new Job { Kind = kind };
        All[j.Id] = j;
        j.Log("start");
        Task.Run(() =>
        {
            try { j.Result = work(j); j.Status = "done"; j.Progress = 1; j.Log("done"); }
            catch (OperationCanceledException) { j.Status = "cancelled"; j.Log("⏹ cancelled"); }
            catch (Exception e) { j.Status = "error"; j.Error = e.Message; j.Log("ERROR: " + e.Message); AppLog.Error(e.ToString()); }
        });
        return j;
    }
}

/// <summary>Application log (shown in the UI log panel and written to data/log.txt).</summary>
public static class AppLog
{
    static readonly List<(DateTime t, string level, string msg)> items = new();
    static string? file;
    public static void Init(string dataDir) { file = Path.Combine(dataDir, "log.txt"); try { if (File.Exists(file) && new FileInfo(file).Length > 5_000_000) File.Delete(file); } catch { } }
    public static void Info(string m) => Add("info", m);
    public static void Warn(string m) => Add("warn", m);
    public static void Error(string m) => Add("error", m);
    static void Add(string level, string m)
    {
        lock (items)
        {
            items.Add((DateTime.Now, level, m));
            if (items.Count > 5000) items.RemoveRange(0, 1000);
            try { if (file != null) File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {m}{Environment.NewLine}"); } catch { }
        }
        if (level != "info") Console.WriteLine($"[{level}] {m}");
    }
    public static JsonArray Since(int from)
    {
        lock (items)
            return new JsonArray(items.Skip(from).Select(i => (JsonNode?)new JsonObject { ["t"] = i.t.ToString("HH:mm:ss"), ["level"] = i.level, ["msg"] = i.msg }).ToArray());
    }
    public static int Count { get { lock (items) return items.Count; } }
    public static int Errors { get { lock (items) return items.Count(i => i.level == "error"); } }
    public static void Clear() { lock (items) items.Clear(); }
}

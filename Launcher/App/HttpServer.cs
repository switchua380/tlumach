using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TranslationLauncher;

/// <summary>Tiny HTTP/1.1 server on 127.0.0.1 (no http.sys / admin rights needed, works on x86 and x64).</summary>
public sealed class HttpServer
{
    public sealed class Req
    {
        public string Method = "", Path = "";
        public Dictionary<string, string> Query = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = Array.Empty<byte>();
        public string BodyText => Encoding.UTF8.GetString(Body);
        public string Q(string k, string def = "") => Query.TryGetValue(k, out var v) ? v : def;
    }

    public sealed class Resp
    {
        public int Status = 200;
        public string ContentType = "application/json; charset=utf-8";
        public byte[] Body = Array.Empty<byte>();
        public Dictionary<string, string> Headers = new();
        public static Resp Json(string json, int status = 200) => new() { Status = status, Body = Encoding.UTF8.GetBytes(json) };
        public static Resp Bytes(byte[] b, string type, string? downloadName = null)
        {
            var r = new Resp { Body = b, ContentType = type };
            if (downloadName != null) r.Headers["Content-Disposition"] = "attachment; filename*=UTF-8''" + Uri.EscapeDataString(downloadName);
            return r;
        }
    }

    readonly TcpListener listener;
    readonly Func<Req, Resp> handler;
    public int Port { get; }

    public HttpServer(int preferredPort, Func<Req, Resp> handler)
    {
        this.handler = handler;
        for (int p = preferredPort; ; p++)
        {
            try { listener = new TcpListener(IPAddress.Loopback, p); listener.Start(); Port = p; break; }
            catch (SocketException) { if (p > preferredPort + 50) throw; }
        }
    }

    public void Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = listener.AcceptTcpClient(); } catch { if (ct.IsCancellationRequested) break; continue; }
            _ = Task.Run(() => Serve(c));
        }
    }

    public void Stop() { try { listener.Stop(); } catch { } }

    void Serve(TcpClient c)
    {
        using (c)
        {
            try
            {
                c.ReceiveTimeout = 120000;
                var s = c.GetStream();
                var req = ReadRequest(s);
                if (req == null) return;
                Resp resp;
                try { resp = handler(req); }
                catch (Exception e) { AppLog.Error($"{req.Method} {req.Path}: {e.Message}"); resp = Resp.Json(System.Text.Json.JsonSerializer.Serialize(new { error = e.Message }), 500); }
                var head = new StringBuilder();
                head.Append($"HTTP/1.1 {resp.Status} {(resp.Status == 200 ? "OK" : "ERR")}\r\n");
                head.Append($"Content-Type: {resp.ContentType}\r\nContent-Length: {resp.Body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n");
                foreach (var h in resp.Headers) head.Append($"{h.Key}: {h.Value}\r\n");
                head.Append("\r\n");
                var hb = Encoding.UTF8.GetBytes(head.ToString());
                s.Write(hb); s.Write(resp.Body); s.Flush();
            }
            catch { }
        }
    }

    static Req? ReadRequest(NetworkStream s)
    {
        var buf = new List<byte>(4096);
        int b, state = 0;
        while ((b = s.ReadByte()) >= 0)
        {
            buf.Add((byte)b);
            state = (b == '\r' && (state == 0 || state == 2)) ? state + 1 : (b == '\n' && (state == 1 || state == 3)) ? state + 1 : 0;
            if (state == 4) break;
            if (buf.Count > 64 * 1024) return null;
        }
        if (state != 4) return null;
        var lines = Encoding.UTF8.GetString(buf.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var req = new Req { Method = first[0] };
        var url = first[1];
        int q = url.IndexOf('?');
        req.Path = Uri.UnescapeDataString(q >= 0 ? url[..q] : url);
        if (q >= 0)
            foreach (var kv in url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int e = kv.IndexOf('=');
                var k = Uri.UnescapeDataString((e >= 0 ? kv[..e] : kv).Replace('+', ' '));
                var v = e >= 0 ? Uri.UnescapeDataString(kv[(e + 1)..].Replace('+', ' ')) : "";
                req.Query[k] = v;
            }
        foreach (var l in lines.Skip(1))
        {
            int i = l.IndexOf(':');
            if (i > 0) req.Headers[l[..i].Trim()] = l[(i + 1)..].Trim();
        }
        if (req.Headers.TryGetValue("Content-Length", out var cl) && long.TryParse(cl, out var len) && len > 0)
        {
            if (len > 1_500_000_000) return null;
            var body = new byte[len]; int read = 0;
            while (read < len) { int n = s.Read(body, read, (int)Math.Min(len - read, 1 << 20)); if (n <= 0) break; read += n; }
            req.Body = body;
        }
        return req;
    }
}

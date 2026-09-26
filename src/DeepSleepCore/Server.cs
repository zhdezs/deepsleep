using System.Text;

namespace TrollWrangler.CoreHost;

/// <summary>路由：接口协议和桌面客户端一致，同一份 ui\app.js 能直接跑在浏览器里。</summary>
internal static partial class Program
{
    private static void OnPush(string json)
    {
        Res[] clients;
        lock (SseGate) clients = SseClients.ToArray();
        foreach (var c in clients)
        {
            _ = Task.Run(async () =>
            {
                try { await c.SseAsync("data: " + json + "\n\n"); }
                catch { lock (SseGate) SseClients.Remove(c); }
            });
        }
    }

    private static async Task HandleAsync(Req req, Res res)
    {
        res.Headers["Cache-Control"] = "no-store";
        if (!ApplyCors(req, res, req.Header("Origin")))
        {
            await res.SendTextAsync("origin not allowed", "text/plain; charset=utf-8", 403);
            return;
        }
        if (req.Method == "OPTIONS") { await res.SendEmptyAsync(204); return; }

        string path = req.Path.Length > 1 ? req.Path.TrimEnd('/') : req.Path;
        if (path.Length == 0) path = "/";

        if (path == "/api/ping") { await res.SendJsonAsync(PingJson()); return; }
        if (path == "/favicon.ico") { await res.SendEmptyAsync(204); return; }

        bool needToken = path.StartsWith("/api/") || path.StartsWith("/data/");
        if (needToken && !TokenOk(req))
        {
            await res.SendJsonAsync("{\"ok\":false,\"err\":\"配对令牌不对（在 Core 窗口里看令牌）\"}", 401);
            return;
        }

        if (path == "/api/events") { await SseAsync(res); return; }

        if (path == "/api/invoke" && req.Method == "POST")
        {
            string outJson;
            try { outJson = await ShellCommandAsync(req.Text); }
            catch (Exception ex) { outJson = "{\"ok\":false,\"err\":" + System.Text.Json.JsonSerializer.Serialize(ex.Message) + "}"; }
            await res.SendJsonAsync(outJson);
            return;
        }

        if (path == "/api/upload" && req.Method == "POST")
        {
            string name = req.Q("name") ?? "file";
            string dir = Path.Combine(_dataDir, "uploads");
            Directory.CreateDirectory(dir);
            string safe = new string(name.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).ToArray());
            if (string.IsNullOrWhiteSpace(safe)) safe = "file";
            string full = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + safe);
            await File.WriteAllBytesAsync(full, req.Body);
            await res.SendJsonAsync("{\"ok\":true,\"path\":" + System.Text.Json.JsonSerializer.Serialize(full) +
                                    ",\"name\":" + System.Text.Json.JsonSerializer.Serialize(safe) +
                                    ",\"size\":" + req.Body.Length + "}");
            return;
        }

        string? relData = SubPath(req.Path, "/data");
        if (relData != null) { await ServeFileAsync(res, _dataDir, relData); return; }
        string? relWeb = SubPath(req.Path, "/web");
        if (relWeb != null) { await ServeFileAsync(res, Path.Combine(_rootDir, "web"), relWeb); return; }
        string? relUi = SubPath(req.Path, "/ui");
        if (relUi != null) { await ServeFileAsync(res, Path.Combine(_rootDir, "ui"), relUi); return; }
        if (path == "/" || path == "/index.html") { await res.SendTextAsync(HomePage(), "text/html; charset=utf-8"); return; }

        await res.SendTextAsync("not found: " + path, "text/plain; charset=utf-8", 404);
    }

    /// <summary>把 "/web/app/" 这类路径剥成相对路径（结尾斜杠要能对上目录，所以不能用 TrimEnd 过的 path）。</summary>
    private static string? SubPath(string raw, string prefix)
    {
        if (raw == prefix || raw == prefix + "/") return "";
        if (raw.StartsWith(prefix + "/", StringComparison.Ordinal)) return raw.Substring(prefix.Length + 1);
        return null;
    }

    private static string PingJson()
        => "{\"ok\":true,\"name\":\"deepsleep-core\",\"version\":" + System.Text.Json.JsonSerializer.Serialize(Version()) +
           ",\"port\":" + _port + ",\"uptime\":" + (int)(DateTime.Now - Started).TotalSeconds +
           ",\"dataDir\":" + System.Text.Json.JsonSerializer.Serialize(_dataDir) + "}";

    private static async Task SseAsync(Res res)
    {
        await res.StartSseAsync();
        lock (SseGate) SseClients.Add(res);
        try
        {
            await res.SseAsync(": connected\n\n");
            while (true)
            {
                await Task.Delay(15000);
                await res.SseAsync(": ping\n\n");
            }
        }
        catch { }
        finally { lock (SseGate) SseClients.Remove(res); }
    }
}

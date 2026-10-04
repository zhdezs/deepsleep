using System.Text;
using System.Text.Json;

namespace TrollWrangler.CoreHost;

/// <summary>内置远程桌面的 HTTP 接口（帧流 + 输入注入）。</summary>
public static partial class CoreServer
{
    private static int QInt(Req req, string name, int def, int min, int max)
    {
        string? s = req.Q(name);
        return int.TryParse(s, out int v) ? Math.Clamp(v, min, max) : def;
    }

    private static double GetNum(JsonElement r, string name)
        => r.TryGetProperty(name, out var e) && e.TryGetDouble(out double v) ? v : 0;

    private static string RdInfoJson()
        => "{\"ok\":true,\"available\":" + (RemoteDesktop.Available ? "true" : "false") +
           ",\"screenW\":" + RemoteDesktop.ScreenWidth +
           ",\"screenH\":" + RemoteDesktop.ScreenHeight +
           ",\"name\":" + JsonSerializer.Serialize(Environment.MachineName) + "}";

    private static async Task RdFrameAsync(Res res, Req req)
    {
        if (!RemoteDesktop.Available)
        {
            await res.SendJsonAsync("{\"ok\":false,\"err\":\"这台机器的系统暂时不支持远程桌面\"}", 501);
            return;
        }
        int w = QInt(req, "w", 1280, 320, 3840);
        byte[] png = RemoteDesktop.CapturePng(w, out int dw, out int dh);
        if (png.Length == 0)
        {
            await res.SendJsonAsync("{\"ok\":false,\"err\":\"截屏失败（系统错误码 " + RemoteDesktop.LastError + "）\"}", 500);
            return;
        }
        res.Headers["X-Frame-W"] = dw.ToString();
        res.Headers["X-Frame-H"] = dh.ToString();
        res.Headers["Cache-Control"] = "no-store";
        await res.SendAsync(png, "image/png");
    }

    /// <summary>
    /// 帧流：每帧 PNG 转 base64，塞进 SSE 的 data 里发（约定 "img" 字段）。
    /// 原来的 multipart/x-mixed-replace 长连会被部分隧道整体缓存，浏览器就一直黑屏 —— 换成 SSE 后
    /// 每条事件都是独立一行，隧道只会转发不会攒着。默认 30 帧，画面没变就跳过，静止不吃带宽。
    /// </summary>
    private static async Task RdStreamAsync(Res res, Req req)
    {
        if (!RemoteDesktop.Available)
        {
            await res.SendJsonAsync("{\"ok\":false,\"err\":\"这台机器的系统暂时不支持远程桌面\"}", 501);
            return;
        }
        int w = QInt(req, "w", 1280, 320, 3840);
        int fps = QInt(req, "fps", 30, 1, 30);
        await res.StartSseAsync();
        try
        {
            byte[] last = Array.Empty<byte>();
            var lastSent = DateTime.UtcNow;
            while (!res.SseBroken)
            {
                byte[] png = RemoteDesktop.CapturePng(w, out int dw, out int dh);
                if (png.Length == 0) break;
                // 画面没变就不重复发（隧道带宽很小，静止时省下来的全是流畅度）；
                // 但最多 5 秒补一帧，免得长时间没数据被中间设备判成死连接。
                bool changed = !png.AsSpan().SequenceEqual(last);
                if (changed || (DateTime.UtcNow - lastSent).TotalSeconds >= 5)
                {
                    res.PushSse("data: {\"w\":" + dw + ",\"h\":" + dh +
                                ",\"img\":\"" + Convert.ToBase64String(png) + "\"}\n\n");
                    last = png;
                    lastSent = DateTime.UtcNow;
                }
                await Task.Delay(Math.Max(1, 1000 / fps));
            }
        }
        catch { /* 对方关了页面 / 断线 */ }
    }

    /// <summary>
    /// 远控消息：控制端把文字 base64 编码后 POST 过来，这里解码喂给本机内核（等价于本机输入一条消息）。
    /// 用 base64 是为了让消息里出现换行 / emoji / 引号时也不被隧道和 JSON 转义搞坏。
    /// </summary>
    private static async Task RdMsgAsync(Res res, Req req)
    {
        string text = "";
        try
        {
            using var doc = JsonDocument.Parse(req.Text);
            var r = doc.RootElement;
            string b = r.TryGetProperty("b", out var be) ? be.GetString() ?? "" : "";
            if (b.Length > 0) text = Encoding.UTF8.GetString(Convert.FromBase64String(b));
            if (text.Length == 0 && r.TryGetProperty("text", out var te) && te.ValueKind == JsonValueKind.String)
                text = te.GetString() ?? "";
        }
        catch { }
        if (text.Length == 0)
        {
            await res.SendJsonAsync("{\"ok\":false,\"err\":\"空消息\"}", 400);
            return;
        }
        // 不等着 AI 把回答写完（可能几分钟，隧道会先超时）：立刻回 ok，
        // 回答过程由 /api/rd/chat 的 SSE 持续推回控制端。
        string payload = JsonSerializer.Serialize(new { id = 0, cmd = "send", kind = 0, text, target = "rd" });
        _ = Task.Run(async () => { try { await ShellCommandAsync(payload); } catch { } });
        await res.SendJsonAsync("{\"ok\":true}");
    }

    /// <summary>
    /// 远控事件流：把内核输出（AI 流式回答、工具状态…）base64 后推给控制端，
    /// 就是用户说的「被控端发上下文」——控制端据此看到完整的回复过程。
    /// </summary>
    internal static async Task RdChatAsync(Res res)
    {
        await res.StartSseAsync();
        lock (RdChatGate) RdChatClients.Add(res);
        try
        {
            res.PushSse(": connected\n\n");
            while (true)
            {
                await Task.Delay(15000);
                if (res.SseBroken) break;
                res.PushSse(": ping\n\n");
            }
        }
        catch { }
        finally { lock (RdChatGate) RdChatClients.Remove(res); }
    }

    private static readonly List<Res> RdChatClients = new();
    private static readonly object RdChatGate = new();

    /// <summary>内核每推一条事件，除了原有客户端，也 base64 转发给远控端。</summary>
    private static void PushRdChat(string json)
    {
        Res[] list;
        lock (RdChatGate) list = RdChatClients.ToArray();
        if (list.Length == 0) return;
        string line = "data: {\"b\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) + "\"}\n\n";
        foreach (var c in list) c.PushSse(line);
    }

    private static void RemoteInput(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string type = r.TryGetProperty("type", out var te) ? te.GetString() ?? "" : "";
            switch (type)
            {
                case "move":
                    RemoteDesktop.MouseMoveNorm(GetNum(r, "x"), GetNum(r, "y"));
                    break;
                case "down":
                case "up":
                {
                    string btn = r.TryGetProperty("button", out var be) ? be.GetString() ?? "left" : "left";
                    RemoteDesktop.MouseButton(btn, type == "down");
                    break;
                }
                case "wheel":
                {
                    int dy = r.TryGetProperty("dy", out var de) && de.TryGetInt32(out int dv) ? dv : 0;
                    RemoteDesktop.Wheel(dy > 0 ? -120 : 120);
                    break;
                }
                case "key":
                {
                    int vk = r.TryGetProperty("vk", out var ve) && ve.TryGetInt32(out int vv) ? vv : 0;
                    bool down = !r.TryGetProperty("down", out var de2) || de2.ValueKind != JsonValueKind.False;
                    RemoteDesktop.Key(vk, down);
                    break;
                }
            }
        }
        catch { }
    }
}

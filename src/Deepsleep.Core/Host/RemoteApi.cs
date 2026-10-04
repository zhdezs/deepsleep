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

    private static async Task RdStreamAsync(Res res, Req req)
    {
        if (!RemoteDesktop.Available)
        {
            await res.SendJsonAsync("{\"ok\":false,\"err\":\"这台机器的系统暂时不支持远程桌面\"}", 501);
            return;
        }
        int w = QInt(req, "w", 1280, 320, 3840);
        int fps = QInt(req, "fps", 8, 1, 30);
        await res.StartStreamAsync("multipart/x-mixed-replace; boundary=dsframe");
        try
        {
            byte[] last = Array.Empty<byte>();
            var lastSent = DateTime.UtcNow;
            while (true)
            {
                byte[] png = RemoteDesktop.CapturePng(w, out _, out _);
                if (png.Length == 0) break;
                // 画面没变就不重复发（隧道带宽很小，静止时省下来的全是响应速度）；
                // 但最多 10 秒补发一次，免得长时间没数据被中间设备判成死连接。
                bool changed = !png.AsSpan().SequenceEqual(last);
                if (changed || (DateTime.UtcNow - lastSent).TotalSeconds >= 10)
                {
                    await res.ChunkAsync(Encoding.ASCII.GetBytes(
                        "\r\n--dsframe\r\nContent-Type: image/png\r\nContent-Length: " + png.Length + "\r\n\r\n"));
                    await res.ChunkAsync(png);
                    last = png;
                    lastSent = DateTime.UtcNow;
                }
                await Task.Delay(1000 / fps);
            }
        }
        catch { /* 对方关了页面 / 断线 */ }
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

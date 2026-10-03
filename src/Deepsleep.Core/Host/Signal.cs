using System.Text;
using System.Text.Json;

namespace TrollWrangler.CoreHost;

/// <summary>
/// 超级连接的公共信令通道（ntfy.sh —— 免费、免注册、纯 HTTP）。
/// 只用来交换「配对码 → 地址」这类几 KB 的握手消息；连上之后数据走直连或隧道，不经过第三方。
/// 实测：订阅（/json NDJSON 长连接）和发布（POST）在国内可直连。
/// </summary>
public static class Signal
{
    /// <summary>信令服务器地址；想自建 ntfy（或走代理）就设环境变量 DEEPSLEEP_SIGNAL_BASE。</summary>
    private static readonly string Base =
        Environment.GetEnvironmentVariable("DEEPSLEEP_SIGNAL_BASE") is string env && env.Trim().Length > 0
            ? env.Trim().TrimEnd('/')
            : "https://ntfy.sh";

    public static string Topic(string code) => "dslink-v2-" + code;

    public static async Task<bool> PublishAsync(string topic, string json, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            using var resp = await http.PostAsync(Base + "/" + topic,
                new StringContent(json, Encoding.UTF8), ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>订阅一个 topic：每条消息回调一次，直到 ct 取消。</summary>
    public static async Task SubscribeAsync(string topic, Action<string> onMessage, CancellationToken ct,
                                          string since = "5m")
    {
        // 注意：ntfy 的 since 只认 all / 时长（5m、1h）/ 时间戳 / 消息 id，
        // 写 "now" 会被判 400（invalid since parameter）——踩过一次。
        await SubscribeOnceAsync(topic, onMessage, ct, since);
    }

    private static async Task SubscribeOnceAsync(string topic, Action<string> onMessage, CancellationToken ct,
                                                 string since)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var resp = await http.GetAsync(Base + "/" + topic + "/json?since=" + since,
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)          // since 不合法之类的，退回全量再试一次
            resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct); }
            catch { break; }
            if (line == null) break;
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("event", out var ev) &&
                    ev.GetString() == "message" &&
                    doc.RootElement.TryGetProperty("message", out var msg))
                {
                    onMessage(msg.GetString() ?? "");
                }
            }
            catch { }
        }
    }
}

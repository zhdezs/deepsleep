using System.Net.Sockets;
using System.Text;

namespace TrollWrangler.CoreHost;

/// <summary>极简 HTTP：不依赖 http.sys（有些机器 / 环境上 HttpListener 会起不来），只服务本机网页。</summary>
internal sealed class Req
{
    public string Method = "";
    public string Path = "/";
    public string Query = "";
    public readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body = Array.Empty<byte>();
    public string Text => Encoding.UTF8.GetString(Body);

    public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : "";
    public bool Has(string name, string value) => string.Equals(Header(name), value, StringComparison.OrdinalIgnoreCase);

    public string? Q(string name)
    {
        foreach (var kv in Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = kv.IndexOf('=');
            string k = i < 0 ? kv : kv[..i];
            if (string.Equals(Uri.UnescapeDataString(k), name, StringComparison.OrdinalIgnoreCase))
                return i < 0 ? "" : Uri.UnescapeDataString(kv[(i + 1)..].Replace('+', ' '));
        }
        return null;
    }
}

internal sealed class Res
{
    private readonly NetworkStream _s;
    public int Status = 200;
    public readonly Dictionary<string, string> Headers = new();
    private bool _sent;

    public Res(NetworkStream s) { _s = s; }

    private static string Reason(int code) => code switch
    {
        200 => "OK", 204 => "No Content", 401 => "Unauthorized", 403 => "Forbidden",
        404 => "Not Found", 500 => "Internal Server Error", _ => "OK",
    };

    private async Task HeadAsync(int status, string type, long? len, bool chunked)
    {
        Status = status;
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
        if (type.Length > 0) sb.Append("Content-Type: ").Append(type).Append("\r\n");
        foreach (var kv in Headers) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
        if (chunked) sb.Append("Transfer-Encoding: chunked\r\n");
        else if (len.HasValue) sb.Append("Content-Length: ").Append(len.Value).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        _sent = true;
        await _s.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    public async Task SendAsync(byte[] data, string type, int status = 200)
    {
        await HeadAsync(status, type, data.Length, false);
        if (data.Length > 0) await _s.WriteAsync(data);
        await _s.FlushAsync();
    }

    public Task SendTextAsync(string text, string type = "text/plain; charset=utf-8", int status = 200)
        => SendAsync(Encoding.UTF8.GetBytes(text), type, status);

    public Task SendJsonAsync(string json, int status = 200)
        => SendAsync(Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8", status);

    public Task SendEmptyAsync(int status) => HeadAsync(status, "", 0, false);

    public async Task StartSseAsync()
    {
        await HeadAsync(200, "text/event-stream; charset=utf-8", null, true);
    }

    /// <summary>开始一段分块流（SSE / MJPEG 都用它）。</summary>
    public async Task StartStreamAsync(string type) => await HeadAsync(200, type, null, true);

    /// <summary>往分块流里写一块。</summary>
    public async Task ChunkAsync(byte[] b)
    {
        if (b.Length == 0) return;
        await _s.WriteAsync(Encoding.UTF8.GetBytes(b.Length.ToString("x") + "\r\n"));
        await _s.WriteAsync(b);
        await _s.WriteAsync(Encoding.UTF8.GetBytes("\r\n"));
        await _s.FlushAsync();
    }

    public async Task SseAsync(string payload) => await ChunkAsync(Encoding.UTF8.GetBytes(payload));

    // --- SSE 有序发送队列 ---------------------------------------------------
    // 以前每个事件都 Task.Run 直接往同一个 socket 写，多个事件并发写会互相踩坏帧、
    // 甚至抛异常把连接踢掉（网页版「事件丢一半」的真凶）。改成一条队列串行写。
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _sseQ = new();
    private int _sseBusy;
    /// <summary>连接已坏（写失败），由 SSE 保活循环负责把客户端摘掉。</summary>
    public volatile bool SseBroken;

    public void PushSse(string payload)
    {
        _sseQ.Enqueue(payload);
        if (Interlocked.CompareExchange(ref _sseBusy, 1, 0) == 0) _ = Task.Run(SseDrain);
    }

    private void SseDrain()
    {
        try
        {
            while (_sseQ.TryDequeue(out var t)) SseAsync(t).GetAwaiter().GetResult();
        }
        catch
        {
            SseBroken = true;
            while (_sseQ.TryDequeue(out _)) { }
        }
        Interlocked.Exchange(ref _sseBusy, 0);
        if (!_sseQ.IsEmpty && Interlocked.CompareExchange(ref _sseBusy, 1, 0) == 0) _ = Task.Run(SseDrain);
    }
}

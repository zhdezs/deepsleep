using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler;

/// <summary>
/// 应用内回调接收器（OAuth 一键绑定的**收结果**那一半）。
/// <para>
/// 外壳把授权页开在应用内窗口里，用户点「同意」后平台会让窗口跳到本机回调地址。
/// 我们不真的让那个 127.0.0.1 服务来收，而是：
/// ① 先在网络层拦下这条导航（<see cref="InternalBrowser.ShouldIntercept"/>）；
/// ② 调用这里，把查询串塞进队列；
/// ③ <see cref="OAuthFlow"/> 那边像收到真实 HTTP 请求一样取走。
/// </para>
/// <para>
/// 这样做的好处：**不占端口、不弹防火墙**，也不依赖「浏览器肯不肯访问 127.0.0.1」。
/// 走系统浏览器兜底时仍用真实回环服务，两条路共用同一套解析代码。
/// </para>
/// </summary>
public static class OAuthCallback
{
    // 平台会把 ?code=…&state=… 打在回调地址上，这里最多到几十字节。
    private const string OkHtml =
        "<!doctype html><meta charset=\"utf-8\"><title>deepsleep</title>" +
        "<body style=\"font-family:system-ui,'Segoe UI',sans-serif;text-align:center;padding:80px 24px;color:#222\">" +
        "<h2>绑定成功</h2><p style=\"color:#666\">已经拿到 API Key，这个窗口会自动关闭。</p></body>";

    private static readonly ConcurrentQueue<string> Incoming = new();

    /// <summary>开始新一次授权前清空 —— 上一次残留的回调会让新流程「秒成功」但拿到的是旧码。</summary>
    public static void Clear()
    {
        while (Incoming.TryDequeue(out _)) { }
    }

    /// <summary>把回调地址里的查询串交出去（在 UI 线程调用，内部只做入队，不阻塞）。</summary>
    public static void Handle(string url)
    {
        string query = "";
        try
        {
            int q = url.IndexOf('?', StringComparison.Ordinal);
            query = q >= 0 ? url[(q + 1)..] : "";
        }
        catch { }
        Incoming.Enqueue(query);
    }

    /// <summary>取一条回调。超时 / 被取消会抛，交给上层翻译成人话。</summary>
    public static async Task<CallbackPayload> NextAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Incoming.TryDequeue(out var query))
                return Parse(query);
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("回调超时");
            await Task.Delay(120, ct).ConfigureAwait(false);
        }
    }

    private static CallbackPayload Parse(string query)
    {
        var result = new CallbackPayload();
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            string k = Uri.UnescapeDataString(pair[..eq]);
            string v = Uri.UnescapeDataString(pair[(eq + 1)..]);
            switch (k)
            {
                case "code": result.Code = v; break;
                case "state": result.State = v; break;
                case "error": result.Error = v; break;
                case "error_description": result.ErrorDescription = v; break;
            }
        }
        return result;
    }

    /// <summary>回调成功页（应用内窗口自己显示的「可以关了」页面）。</summary>
    public static string SuccessHtml => OkHtml;

    public sealed class CallbackPayload
    {
        public string Code { get; set; } = "";
        public string State { get; set; } = "";
        public string Error { get; set; } = "";
        public string ErrorDescription { get; set; } = "";
    }
}

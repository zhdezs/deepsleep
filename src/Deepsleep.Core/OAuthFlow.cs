using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler;

/// <summary>
/// OAuth PKCE 一键绑定：不手工创建 API Key，授权登录一次就自动填好。
/// <para>
/// 流程（Codex / Claude Code / Cline 走的是同一套标准做法）：
/// ① 生成 PKCE 的 code_verifier / code_challenge（S256）与防重放的 state；
/// ② 把用户送到平台授权页，授权页里的 callback_url 指向本机 http://127.0.0.1:&lt;随机端口&gt;/…；
/// ③ 用户登录并点「同意」；
/// ④ 平台带 ?code= 跳回那个本机地址，**两条接收路径同时开着，谁先拿到算谁**：
///    · 应用内窗口：导航到回调地址时在网络层拦下，直接取走查询串；
///    · 本机回环服务：真的起一个只监听 127.0.0.1 的 HttpListener。
/// ⑤ 拿 code + code_verifier 换回 API Key。
/// </para>
/// <para>
/// ⚠ **第二条路径必须一直开着**。这不是「二选一」而是「双保险」：授权中途被带去外部浏览器是
/// 常态（GitHub / Google 这类第三方登录尤其容易），只靠应用内拦截的话，流程走到一半就断了 ——
/// 用户会卡在 GitHub 页面上，而应用这边永远等不到授权码。真实踩过。
/// </para>
/// <para>
/// ⚠ 只有**开放第三方应用 OAuth** 的平台能用。硅基流动、智谱这些只能在控制台手工建 Key；
/// 魔搭虽有 OIDC，但换回的是身份 access_token（不是推理 Key），调不了推理接口 ——
/// 这几家走「按引导绑定」，界面上会明确区分，不会让用户点了按钮撞一头灰。
/// </para>
/// 零第三方依赖：HttpListener + HttpClient 都是 .NET 自带。
/// </summary>
public static class OAuthFlow
{
    /// <summary>一个支持一键绑定的平台。</summary>
    public sealed class Provider
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        /// <summary>授权页（用户在这里登录 / 同意）。</summary>
        public string AuthUrl { get; init; } = "";
        /// <summary>拿授权码换 API Key 的接口。</summary>
        public string ExchangeUrl { get; init; } = "";
        /// <summary>换回来的 Key 能用的对话地址。</summary>
        public string ApiUrl { get; init; } = "";
        public string Format { get; init; } = "openai";
        public string KeyUrl { get; init; } = "";
        public string Quota { get; init; } = "";
        /// <summary>平台是否要求 PKCE。有些平台收到 code_challenge 会直接报错，所以做成开关。</summary>
        public bool SendPkce { get; init; } = true;
    }

    /// <summary>
    /// 支持一键绑定的平台。加新平台只要这里加一条 —— 但**必须先确认它真的开放第三方 OAuth**，
    /// 否则用户点下去只会看到一个「未授权应用 / invalid client」的错误页。
    /// </summary>
    public static readonly Provider[] Providers =
    {
        new()
        {
            Id = "openrouter",
            Name = "OpenRouter（25+ 款 :free 永久 0 元）",
            AuthUrl = "https://openrouter.ai/auth",
            ExchangeUrl = "https://openrouter.ai/api/v1/auth/keys",
            ApiUrl = "https://openrouter.ai/api/v1/chat/completions",
            KeyUrl = "https://openrouter.ai/settings/keys",
            Quota = "授权后拿到一把专属 API Key（随时可在官网吊销，不用手动创建 / 复制）。" +
                    "25+ 款模型带 :free 后缀永久 0 元，默认 50 次/天、20 次/分；充过 $10 后升到 1000 次/天。",
            SendPkce = true,
        },
        new()
        {
            Id = "nanogpt",
            Name = "NanoGPT（聚合多渠道，无地区限制）",
            AuthUrl = "https://nano-gpt.com/auth",
            ExchangeUrl = "https://nano-gpt.com/api/v1/auth/keys",
            ApiUrl = "https://nano-gpt.com/api/v1/chat/completions",
            KeyUrl = "https://nano-gpt.com/account",
            Quota = "授权后拿到专属 sk-nano-… Key，聚合多家模型。按量付费、没有免费档 —— " +
                    "适合当作「不用梯子也能到主流模型」的通道。",
            SendPkce = true,
        },
    };

    public static Provider? Find(string id)
    {
        foreach (var p in Providers)
            if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>
    /// 跑完整条一键绑定流程，返回换到的 API Key。失败抛异常，调用方把 Message 直接显示给用户。
    /// </summary>
    public static async Task<string> RunAsync(Provider p, int timeoutSeconds, CancellationToken ct = default)
    {
        int port = FreePort();
        string redirect = $"http://127.0.0.1:{port}/callback/";

        // ---- PKCE + state ----
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var q = new List<string> { "callback_url=" + Uri.EscapeDataString(redirect) };
        if (p.SendPkce)
        {
            q.Add("code_challenge=" + Uri.EscapeDataString(challenge));
            q.Add("code_challenge_method=S256");
        }
        q.Add("state=" + Uri.EscapeDataString(state));
        string authUrl = p.AuthUrl + "?" + string.Join("&", q);

        // 回环服务**先起**，再开授权页 —— 中间任何一个瞬间的跳转都不能漏掉。
        OAuthCallback.Clear();   // 清掉上一次残留的回调，避免新流程拿到旧授权码
        var receivers = new List<Func<CancellationToken, Task<OAuthCallback.CallbackPayload>>>();
        LoopbackReceiver? loopback = null;
        if (HttpListener.IsSupported)
        {
            try
            {
                loopback = new LoopbackReceiver(redirect);
                receivers.Add(loopback.NextAsync);
            }
            catch { loopback = null; }   // 端口被占等：还能走应用内拦截
        }

        // 应用内接收器**无条件**挂上，不跟「窗口有没有建成」绑定：
        // 窗口没建成时它只是个空转的队列轮询（成本为零），但窗口建成后又被中途关掉、
        // 或者外壳在别处把回调交回队列时，这条路径就是唯一的救生索。
        receivers.Add(t => OAuthCallback.NextAsync(TimeSpan.FromSeconds(timeoutSeconds), t));

        bool inApp = InternalBrowser.TryOpen(authUrl, redirect);
        if (!inApp && loopback != null)
        {
            // 应用内窗口没建成 → 退回系统浏览器（结果由上面两条路径之一收下）
            try { Platform.OpenWithShell(authUrl, false); } catch { }
        }

        InternalBrowser.Log($"OAuth 开始 · {p.Id} · 应用内窗口={inApp} · 回环监听={loopback != null}");

        try
        {
            var cb = await FirstResultAsync(receivers, TimeSpan.FromSeconds(timeoutSeconds), ct)
                              .ConfigureAwait(false);
            InternalBrowser.Log($"OAuth 收到回调 · code 长度={cb.Code.Length} · state 匹配={cb.State == state}");
            Validate(cb, state);
            return await ExchangeAsync(p, cb.Code, verifier, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException(TimeoutMessage(timeoutSeconds));
        }
        finally
        {
            loopback?.Dispose();
            if (inApp) InternalBrowser.Release();
        }
    }

    /// <summary>
    /// 多条接收路径赛跑，谁先拿到有效结果算谁。**单条路径失败不影响另一条** ——
    /// 所以这里把异常都吃掉，只认「真的拿到了回调」。
    /// </summary>
    private static async Task<OAuthCallback.CallbackPayload> FirstResultAsync(
        IReadOnlyList<Func<CancellationToken, Task<OAuthCallback.CallbackPayload>>> receivers,
        TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var tcs = new TaskCompletionSource<OAuthCallback.CallbackPayload>(TaskCreationOptions.RunContinuationsAsynchronously);

        foreach (var receive in receivers)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var payload = await receive(cts.Token).ConfigureAwait(false);
                    if (payload != null) tcs.TrySetResult(payload);
                }
                catch (OperationCanceledException) { /* 到点了 / 别的路径先拿到，正常收工 */ }
                catch (Exception ex) { InternalBrowser.Log("OAuth 接收路径异常：" + ex.Message); }
            });
        }

        using (cts.Token.Register(() => tcs.TrySetCanceled()))
            return await tcs.Task.ConfigureAwait(false);
    }

    private static void Validate(OAuthCallback.CallbackPayload cb, string expectState)
    {
        if (cb.Error.Length > 0)
            throw new InvalidOperationException($"平台拒绝了这次授权：{cb.Error} {cb.ErrorDescription}".Trim());
        if (cb.Code.Length == 0)
            throw new InvalidOperationException("回调里没有带回授权码，请重试。");
        // state 必须一致：防止别的页面 / 请求把 code 顶掉（CSRF 面）
        if (cb.State.Length > 0 && cb.State != expectState)
            throw new InvalidOperationException("回调校验不一致（state 对不上），已中止。请重试。");
    }

    private static string TimeoutMessage(int seconds) =>
        $"等了 {seconds} 秒没等到授权结果。常见原因是卡在第三方登录页（GitHub / Google）没走完 —— " +
        "可以点授权窗口右上角的「在系统浏览器完成授权」，那条路更稳。";

    private static async Task<string> ExchangeAsync(Provider p, string code, string verifier, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var payload = new Dictionary<string, object?> { ["code"] = code };
        if (p.SendPkce)
        {
            payload["code_verifier"] = verifier;
            payload["code_challenge_method"] = "S256";
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, p.ExchangeUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string snippet = body.Length > 200 ? body[..200] : body;
            throw new InvalidOperationException($"换取 API Key 失败：HTTP {(int)resp.StatusCode} {snippet}");
        }

        using var doc = JsonDocument.Parse(body);
        foreach (var name in new[] { "key", "api_key", "apiKey", "token", "access_token" })
        {
            if (doc.RootElement.TryGetProperty(name, out var v) &&
                v.ValueKind == JsonValueKind.String &&
                v.GetString() is { Length: > 0 } key)
                return key;
        }
        throw new InvalidOperationException("平台返回里没有找到 API Key 字段。");
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Base64Url(byte[] raw)
        => Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>只监听回环地址的临时回调服务。平台把 ?code= 打过来就收下，然后回一个「可以关了」的页面。</summary>
    private sealed class LoopbackReceiver : IDisposable
    {
        private readonly HttpListener _listener;

        public LoopbackReceiver(string prefix)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            _listener.Start();
        }

        public async Task<OAuthCallback.CallbackPayload> NextAsync(CancellationToken ct)
        {
            var ctx = await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            var qs = ctx.Request.QueryString;
            var payload = new OAuthCallback.CallbackPayload
            {
                Code = qs["code"] ?? "",
                State = qs["state"] ?? "",
                Error = qs["error"] ?? "",
                ErrorDescription = qs["error_description"] ?? "",
            };
            await ReplyHtmlAsync(ctx).ConfigureAwait(false);
            return payload;
        }

        private static async Task ReplyHtmlAsync(HttpListenerContext ctx)
        {
            byte[] buf = Encoding.UTF8.GetBytes(OAuthCallback.SuccessHtml);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            try
            {
                ctx.Response.ContentLength64 = buf.Length;
                await ctx.Response.OutputStream.WriteAsync(buf).ConfigureAwait(false);
            }
            catch { }
            finally { try { ctx.Response.Close(); } catch { } }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}

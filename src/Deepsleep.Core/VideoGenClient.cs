using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler;

/// <summary>
/// 视频生成客户端（**自定义 API**）。
///
/// 视频生成没有统一标准，各家的差别主要在「怎么拿到结果」。这里按下面三种都认：
///   ① 提交接口直接给视频地址 / base64（同步型）
///   ② 提交后返回任务 id，再轮询查询接口（智谱 / 多数国内厂商）
///   ③ 自己指定查询地址模板（含 {id} 占位，例如 https://api.x.com/v1/videos/{id}）
/// 查询地址留空时按「把提交地址最后一段换成 async-result/{id}」推导（智谱协议）。
/// 结果里 url / video_result[0].url / data[0].url / b64_json 都能识别。
/// </summary>
public sealed class VideoGenClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>提交任务地址（必填）。任何遵守「POST 提交 → 拿结果」的接口都能填。</summary>
    public string Url { get; set; } = "https://open.bigmodel.cn/api/paas/v4/videos/generations";
    /// <summary>查询地址模板（可选，含 {id}）。留空则按智谱协议推导。</summary>
    public string PollUrlTemplate { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "cogvideox-flash";
    public string SaveDir { get; set; } = "";

    /// <summary>最近一次失败的原因（给工具返回和日志看，别让用户只看到「失败」两个字）。</summary>
    public string LastError { get; private set; } = "";

    public bool Configured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>生成视频并保存到本地，返回本地文件路径；失败返回 null（原因见 LastError）。</summary>
    public async Task<string?> GenerateAsync(string prompt, string? size = null,
                                             CancellationToken ct = default)
    {
        LastError = "";
        if (!Configured) { LastError = "没有配置视频 API Key"; return null; }
        if (string.IsNullOrWhiteSpace(prompt)) { LastError = "提示词为空"; return null; }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(10));

            // ---- 1) 提交任务
            var payload = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["model"] = Model,
                ["prompt"] = prompt,
            };
            if (!string.IsNullOrWhiteSpace(size)) payload["size"] = size;

            using var req = new HttpRequestMessage(HttpMethod.Post, Url);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey.Trim());

            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"提交失败 HTTP {(int)resp.StatusCode}：{Short(body)}";
                return null;
            }
            // 智谱这类接口出错也回 200，正文里带 {"error":{"code":1305,"message":"…"}}
            string? apiErr = ErrorMessageOf(body);
            if (apiErr != null) { LastError = apiErr; return null; }

            string taskId = "";
            using (var doc = JsonDocument.Parse(body))
            {
                var root = doc.RootElement;
                // 各家叫法不一：id / task_id / data.id 都认
                taskId = Str(root, "id") ?? Str(root, "task_id") ?? "";
                if (taskId.Length == 0 && root.TryGetProperty("data", out var d0) &&
                    d0.ValueKind == JsonValueKind.Object)
                    taskId = Str(d0, "id") ?? Str(d0, "task_id") ?? "";
                // 少数网关直接同步返回结果
                string? direct = VideoUrlOf(root);
                if (direct != null) return await DownloadAsync(direct, cts.Token).ConfigureAwait(false);
            }
            if (taskId.Length == 0) { LastError = "接口没返回任务 id：" + Short(body); return null; }

            // ---- 2) 轮询结果
            string pollUrl = PollUrl(taskId);
            string lastStatus = "";
            for (int i = 0; i < 120 && !cts.Token.IsCancellationRequested; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cts.Token).ConfigureAwait(false);
                using var pollReq = new HttpRequestMessage(HttpMethod.Get, pollUrl);
                pollReq.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey.Trim());
                using var pollResp = await _http.SendAsync(pollReq, cts.Token).ConfigureAwait(false);
                string pollBody = await pollResp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                if (!pollResp.IsSuccessStatusCode) { LastError = $"查询失败 HTTP {(int)pollResp.StatusCode}：{Short(pollBody)}"; continue; }
                string? perr = ErrorMessageOf(pollBody);
                if (perr != null) { LastError = perr; return null; }

                using var doc = JsonDocument.Parse(pollBody);
                var root = doc.RootElement;
                string status = (Str(root, "task_status") ?? Str(root, "status") ?? "").ToUpperInvariant();
                lastStatus = status;
                if (status is "SUCCESS" or "SUCCEEDED" or "DONE" or "COMPLETED" or "")
                {
                    string? url = VideoUrlOf(root);
                    if (url != null) return await DownloadAsync(url, cts.Token).ConfigureAwait(false);
                    if (status.Length > 0) { LastError = "任务完成但没有视频地址：" + Short(pollBody); return null; }
                }
                if (status is "FAIL" or "FAILED" or "ERROR" or "CANCELED" or "CANCELLED")
                {
                    LastError = "生成失败：" + Short(pollBody);
                    return null;
                }
                // PROCESSING / QUEUED / 其它 → 继续等
            }
            LastError = "等待超时（10 分钟）" + (lastStatus.Length > 0 ? "，最后状态 " + lastStatus : "") + "，稍后重试";
            return null;
        }
        catch (OperationCanceledException) { LastError = ct.IsCancellationRequested ? "已取消" : "等待超时"; return null; }
        catch (Exception ex) { LastError = ex.Message; return null; }
    }

    /// <summary>查询地址：优先用用户给的模板（含 {id}），否则按智谱协议推导。</summary>
    private string PollUrl(string taskId)
    {
        string tpl = (PollUrlTemplate ?? "").Trim();
        if (tpl.Length > 0)
            return tpl.Contains("{id}") ? tpl.Replace("{id}", Uri.EscapeDataString(taskId)) : tpl.TrimEnd('/') + "/" + taskId;
        string s = (Url ?? "").Trim();
        int q = s.IndexOf('?');
        if (q >= 0) s = s[..q];
        try
        {
            var u = new Uri(s);
            string path = u.AbsolutePath;
            int slash = path.LastIndexOf('/');
            if (slash > 0) path = path[..slash];                 // 去掉 /videos/generations
            return u.GetLeftPart(UriPartial.Authority) + path + "/async-result/" + taskId;
        }
        catch { return "https://open.bigmodel.cn/api/paas/v4/async-result/" + taskId; }
    }

    /// <summary>从结果里抠视频地址：video_result[0].url / data[0].url / 顶层 url 都认。</summary>
    private static string? VideoUrlOf(JsonElement root)
    {
        if (root.TryGetProperty("video_result", out var vr) && vr.ValueKind == JsonValueKind.Array && vr.GetArrayLength() > 0)
        {
            var first = vr[0];
            if (first.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String) return u.GetString();
        }
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
        {
            var first = data[0];
            if (first.ValueKind == JsonValueKind.String) return first.GetString();
            if (first.ValueKind == JsonValueKind.Object &&
                first.TryGetProperty("url", out var u2) && u2.ValueKind == JsonValueKind.String) return u2.GetString();
        }
        if (root.TryGetProperty("url", out var u3) && u3.ValueKind == JsonValueKind.String) return u3.GetString();
        return null;
    }

    private async Task<string?> DownloadAsync(string url, CancellationToken ct)
    {
        using var img = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!img.IsSuccessStatusCode) { LastError = $"下载视频失败 HTTP {(int)img.StatusCode}"; return null; }
        byte[] bytes = await img.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length == 0) { LastError = "下载到的视频是空的"; return null; }

        string dir = string.IsNullOrWhiteSpace(SaveDir) ? Path.Combine(Path.GetTempPath(), "agent_videos") : SaveDir;
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"vid_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}.mp4");
        File.WriteAllBytes(file, bytes);
        return file;
    }

    /// <summary>正文里是不是一个错误对象（智谱出错也回 200，要自己看）。</summary>
    private static string? ErrorMessageOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var err)) return null;
            if (err.ValueKind == JsonValueKind.Object)
            {
                string msg = Str(err, "message") ?? "";
                string code = Str(err, "code") ?? "";
                if (msg.Length == 0 && code.Length == 0) return null;
                return "接口报错" + (code.Length > 0 ? "（" + code + "）" : "") + "：" + (msg.Length > 0 ? msg : "未知错误");
            }
            if (err.ValueKind == JsonValueKind.String) return "接口报错：" + err.GetString();
            return null;
        }
        catch { return null; }
    }

    private static string? Str(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Short(string s) => (s ?? "").Length <= 300 ? (s ?? "") : s![..300] + "…";

    public void Dispose() => _http.Dispose();
}

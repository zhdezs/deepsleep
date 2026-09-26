using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler;

/// <summary>
/// 外部 API 客户端（OpenAI 兼容 chat/completions 接口，如 DeepSeek）。
/// 零第三方依赖，仅用 System.Net.Http + System.Text.Json。
/// </summary>
public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http = new();
    public string Url { get; set; } = "https://api.deepseek.com/chat/completions";
    public string Model { get; set; } = "deepseek-v4-flash";
    public string ApiKey { get; set; } = "";
    /// <summary>API 协议格式：openai（兼容）/ anthropic（Claude）/ gemini（Google）。</summary>
    public string Format { get; set; } = "openai";
    /// <summary>启用深度思考（DeepSeek v4 的 thinking 模式）。</summary>
    public bool Thinking { get; set; } = false;
    /// <summary>思考强度：low / medium / high（配合 Thinking 使用）。</summary>
    public string ReasoningEffort { get; set; } = "high";

    public bool Configured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>最近一次调用失败的原因（用于界面诊断）。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 最近一次回复是不是被输出长度上限截断了（finish_reason=length）。
    /// 上层据此自动续写，用户不用自己敲「继续」。
    /// </summary>
    public bool LastTruncated { get; internal set; }

    /// <summary>
    /// 规范化后的请求地址。用户容易只填主机名（例如 https://api.deepseek.com/），
    /// 直接 POST 根地址会 404，所以这里按协议自动补全路径。
    /// </summary>
    public string Endpoint => NormalizeUrl(Url, EffectiveFormat);

    /// <summary>
    /// 实际使用的协议：按地址自动纠正明显不匹配的格式。
    /// 例如格式还留在「OpenAI 兼容」但地址已经写了 /v1/responses（OpenAI Responses API），
    /// 或者地址写了 /v1/messages（Claude）、:generateContent（Gemini）。
    /// </summary>
    public string EffectiveFormat
    {
        get
        {
            string lower = (Url ?? "").ToLowerInvariant();
            if (lower.Contains("/responses")) return "responses";
            if (lower.Contains("/messages")) return "anthropic";
            if (lower.Contains(":generatecontent")) return "gemini";
            return Format;
        }
    }

    /// <summary>补全 API 路径：https://api.deepseek.com/ → .../chat/completions。</summary>
    public static string NormalizeUrl(string url, string format)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        string u = url.Trim();
        string lower = u.ToLowerInvariant();
        if (format == "gemini") return u;
        if (format == "responses")
        {
            // OpenAI 新版 Responses API：POST /v1/responses
            if (lower.Contains("/responses")) return u;
            int cc = u.IndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase);
            if (cc >= 0) return u[..cc] + "/responses";
            if (lower.EndsWith("/v1")) return u + "/responses";
            // 只写了主机名 → 补标准路径 /v1/responses；已经带了别的路径前缀就只补 /responses
            string hostPart = u;
            int scheme = hostPart.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) hostPart = hostPart[(scheme + 3)..];
            int firstSlash = hostPart.IndexOf('/');
            bool noPath = firstSlash < 0 || hostPart[(firstSlash + 1)..].Trim('/').Length == 0;
            return u.TrimEnd('/') + (noPath ? "/v1/responses" : "/responses");
        }
        if (format == "anthropic")
            return lower.Contains("/messages") ? u : u.TrimEnd('/') + "/v1/messages";
        if (lower.Contains("/chat/completions") || lower.Contains("/completions")) return u;
        // 已经写了别的合法端点（responses / messages）就别乱拼路径
        if (lower.Contains("/responses") || lower.Contains("/messages")) return u;
        return u.TrimEnd('/') + "/chat/completions";
    }

    /// <summary>HTTP 错误信息：带上状态码含义提示，方便在设置里对症改配置。</summary>
    private string HttpError(HttpResponseMessage resp)
    {
        string hint = (int)resp.StatusCode switch
        {
            401 or 403 => "（API Key 无效或没有权限）",
            404 => "（地址或模型名不对：OpenAI 兼容地址一般要写到 /chat/completions）",
            429 => "（触发限流，稍后重试或换模型）",
            >= 500 => "（服务端错误，稍后重试）",
            _ => "",
        };
        return $"HTTP {(int)resp.StatusCode}{hint}";
    }

    /// <summary>
    /// 调用外部 API 生成回怼。失败（无 key / 网络 / 超时 / 解析错误）返回 null。
    /// </summary>
    public async Task<string?> ChatAsync(string system, string user,
        int timeoutSeconds = 300, CancellationToken ct = default)
        => await ChatAsync(system, new[] { (Role: "user", Content: user) }, timeoutSeconds, ct);

    /// <summary>多轮消息版本（供 AI 助手使用）。消息角色：user / assistant / tool。</summary>
    public async Task<string?> ChatAsync(string system, IReadOnlyList<(string Role, string Content)> messages,
        int timeoutSeconds = 300, CancellationToken ct = default)
    {
        if (!Configured)
        {
            LastError = "未配置 API Key";
            return null;
        }
        try
        {
        LastTruncated = false;
        LastTruncated = false;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            if (EffectiveFormat == "anthropic")
                return await ChatAnthropicAsync(system, messages, cts.Token, stream: false, onDelta: null);
            if (EffectiveFormat == "gemini")
                return await ChatGeminiAsync(system, messages, cts.Token, stream: false, onDelta: null);
            if (EffectiveFormat == "responses")
                return await ChatResponsesAsync(system, messages, cts.Token, stream: false, onDelta: null);

            var msgs = new List<object> { new { role = "system", content = system } };
            foreach (var (role, content) in messages)
                msgs.Add(new { role = MapRole(role), content = BuildContent(content) });

            var payload = new Dictionary<string, object?>
            {
                ["model"] = Model,
                ["messages"] = msgs,
                ["temperature"] = 0.9,
                ["max_tokens"] = 8192,
                ["stream"] = false,
            };
            if (Thinking)
            {
                payload["thinking"] = new { type = "enabled" };
                if (!string.IsNullOrWhiteSpace(ReasoningEffort))
                    payload["reasoning_effort"] = ReasoningEffort;
            }
            string json = JsonSerializer.Serialize(payload);
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey.Trim());

            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = HttpError(resp);
                try
                {
                    string errBody = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    if (errBody.Length > 200) errBody = errBody[..200];
                    LastError += " " + errBody;
                }
                catch { /* 读取错误体失败则只保留状态码 */ }
                return null;
            }
            string body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0)
            {
                LastError = "响应中没有 choices";
                return null;
            }
            string? text = choices[0].GetProperty("message").GetProperty("content").GetString();
            if (choices[0].TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                LastTruncated = fr.GetString() == "length";
            LastError = null;
            return text;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    private static string MapRole(string role) => role switch
    {
        "assistant" => "assistant",
        "tool" => "user",
        _ => "user",
    };

    /// <summary>多模态支持：IMG|路径|文本 格式的消息内联为 image_url（base64），其余保持纯文本。</summary>
    private static object BuildContent(string content)
    {
        if (!content.StartsWith("IMG|", StringComparison.Ordinal)) return content;
        int p1 = content.IndexOf('|', 3);
        if (p1 <= 3) return content;
        string path = content[4..p1];
        string text = content[(p1 + 1)..];
        if (!File.Exists(path)) return text;
        var fi = new FileInfo(path);
        if (fi.Length <= 6L * 1024 * 1024)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string mime = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "image/png",
            };
            string b64 = Convert.ToBase64String(File.ReadAllBytes(path));
            return new object[]
            {
                new { type = "text", text },
                new { type = "image_url", image_url = new { url = "data:" + mime + ";base64," + b64 } },
            };
        }
        return text;
    }

    /// <summary>流式版本：逐段回调增量内容，返回完整文本（供真·流式输出）。</summary>
    public async Task<string?> ChatStreamAsync(string system,
        IReadOnlyList<(string Role, string Content)> messages,
        Action<string>? onDelta = null, int timeoutSeconds = 600, CancellationToken ct = default)
    {
        if (!Configured)
        {
            LastError = "未配置 API Key";
            return null;
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            if (EffectiveFormat == "anthropic")
                return await ChatAnthropicAsync(system, messages, cts.Token, stream: false, onDelta: null);
            if (EffectiveFormat == "gemini")
                return await ChatGeminiAsync(system, messages, cts.Token, stream: false, onDelta: null);
            if (EffectiveFormat == "responses")
                return await ChatResponsesAsync(system, messages, cts.Token, stream: true, onDelta: onDelta);

            var msgs = new List<object> { new { role = "system", content = system } };
            foreach (var (role, content) in messages)
                msgs.Add(new { role = MapRole(role), content = BuildContent(content) });
            var payload = new Dictionary<string, object?>
            {
                ["model"] = Model,
                ["messages"] = msgs,
                ["temperature"] = 0.9,
                ["max_tokens"] = 8192,
                ["stream"] = true,
            };
            if (Thinking)
            {
                payload["thinking"] = new { type = "enabled" };
                if (!string.IsNullOrWhiteSpace(ReasoningEffort))
                    payload["reasoning_effort"] = ReasoningEffort;
            }
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey.Trim());

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                                       .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = HttpError(resp);
                try
                {
                    string errBody = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    if (errBody.Length > 200) errBody = errBody[..200];
                    LastError += " " + errBody;
                }
                catch { /* 只保留状态码 */ }
                return null;
            }

            var sb = new StringBuilder();
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (true)
            {
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                    continue;
                string data = line["data:".Length..].Trim();
                if (data == "[DONE]") break;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() == 0) continue;
                    if (choices[0].TryGetProperty("finish_reason", out var fr) &&
                        fr.ValueKind == JsonValueKind.String && fr.GetString() == "length")
                        LastTruncated = true;
                    var delta = choices[0].GetProperty("delta");
                    if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        string chunk = c.GetString() ?? "";
                        if (chunk.Length > 0)
                        {
                            sb.Append(chunk);
                            onDelta?.Invoke(chunk);
                        }
                    }
                }
                catch { /* 跳过无法解析的 SSE 行 */ }
            }
            LastError = null;
            return sb.ToString();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    // ------------------------------------------------------------------
    // OpenAI Responses API（POST /v1/responses）
    // 与 chat/completions 的差异：系统提示走 instructions、对话走 input、
    // 结果在 output[].content[].text，流式事件为 response.output_text.delta。
    // ------------------------------------------------------------------
    private async Task<string?> ChatResponsesAsync(string system,
        IReadOnlyList<(string Role, string Content)> messages, CancellationToken ct,
        bool stream, Action<string>? onDelta, int maxOut = 8192)
    {
        var input = new List<object>();
        foreach (var (role, content) in messages)
        {
            if (string.IsNullOrEmpty(content)) continue;
            if (role == "tool")
            {
                // Responses API 没有 tool 角色，工具结果用 user 交回
                input.Add(new { role = "user", content = "[工具执行结果]\n" + content });
                continue;
            }
            input.Add(new
            {
                role = role == "assistant" ? "assistant" : "user",
                content = BuildResponsesContent(content),
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = Model,
            ["input"] = input,
            ["max_output_tokens"] = maxOut,
            ["stream"] = stream,
        };
        if (!string.IsNullOrWhiteSpace(system)) payload["instructions"] = system;
        // Responses 端点对参数更严格：只带必要字段，temperature 之类留给服务端默认，
        // 只有显式开了深度思考才带 reasoning.effort。
        if (Thinking)
            payload["reasoning"] = new { effort = string.IsNullOrWhiteSpace(ReasoningEffort) ? "medium" : ReasoningEffort };

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey.Trim());

        using var resp = await _http.SendAsync(req,
            stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            LastError = HttpError(resp);
            try
            {
                string errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (errBody.Length > 200) errBody = errBody[..200];
                LastError += " " + errBody;
            }
            catch { /* 只保留状态码 */ }
            return null;
        }

        if (stream)
        {
            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            string? text = await ReadSseAsync(s, ct, onDelta, ev =>
            {
                if (!ev.TryGetProperty("type", out var ty) || ty.ValueKind != JsonValueKind.String) return null;
                string type = ty.GetString() ?? "";
                if (type == "response.output_text.delta")
                    return ev.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString() : null;
                if (type is "response.failed" or "response.incomplete" or "error")
                    LastError = "Responses 流式事件：" + type;
                return null;
            }).ConfigureAwait(false);
            // 流式没拿到内容（部分兼容端点不支持）→ 退回非流式，保证能出话
            if (string.IsNullOrEmpty(text)) return await ChatResponsesAsync(system, messages, ct, false, null, maxOut);
            LastError = null;
            return text;
        }

        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string got = ExtractResponsesText(root, out string status, out string reason);
        if (got.Length > 0)
        {
            // 即使被判定为 incomplete（例如刚好卡在 token 上限），也先把已经拿到的部分交给用户，别整段丢掉
            LastError = null;
            return got;
        }

        // 完全没拿到文本：如果是 token 用尽，放大上限重试一次
        if (status == "incomplete" && reason.Contains("max_output_tokens", StringComparison.OrdinalIgnoreCase) &&
            maxOut < 32768)
        {
            return await ChatResponsesAsync(system, messages, ct, false, null, Math.Min(32768, maxOut * 2))
                .ConfigureAwait(false);
        }

        LastError = string.IsNullOrEmpty(status)
            ? "响应里没有可用的文本（检查模型名是否支持 Responses API）"
            : $"响应里没有可用的文本（status={status}" + (string.IsNullOrEmpty(reason) ? "" : " · " + reason) + "）";
        return null;
    }

    /// <summary>
    /// 宽容解析 Responses API 的返回：output_text / output[].content[].text / output[].text 都认，
    /// 不再要求 content 的 type 一定等于 output_text（各家实现不一致），
    /// 并顺带带回 status 与 incomplete_details.reason，方便把「为什么没有文本」说清楚。
    /// </summary>
    private static string ExtractResponsesText(JsonElement root, out string status, out string reason)
    {
        status = root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString() ?? "" : "";
        reason = "";
        if (root.TryGetProperty("incomplete_details", out var inc) && inc.ValueKind == JsonValueKind.Object &&
            inc.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String)
            reason = rs.GetString() ?? "";

        if (root.TryGetProperty("output_text", out var ot) && ot.ValueKind == JsonValueKind.String)
        {
            string s = ot.GetString() ?? "";
            if (s.Length > 0) return s;
        }

        var sb = new StringBuilder();
        if (root.TryGetProperty("output", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.TryGetProperty("content", out var cs) && cs.ValueKind == JsonValueKind.Array)
                    foreach (var c in cs.EnumerateArray())
                        if (c.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                            sb.Append(tx.GetString());
                if (item.TryGetProperty("text", out var itx) && itx.ValueKind == JsonValueKind.String)
                    sb.Append(itx.GetString());
            }
        }
        return sb.ToString();
    }

    /// <summary>Responses API 的多模态内容格式：input_text / input_image。</summary>
    private static object BuildResponsesContent(string content)
    {
        if (!content.StartsWith("IMG|", StringComparison.Ordinal)) return content;
        int p1 = content.IndexOf('|', 3);
        if (p1 <= 3) return content;
        string path = content[4..p1];
        string text = content[(p1 + 1)..];
        if (!File.Exists(path)) return text;
        if (new FileInfo(path).Length > 6L * 1024 * 1024) return text;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string mime = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/png",
        };
        string b64 = Convert.ToBase64String(File.ReadAllBytes(path));
        return new object[]
        {
            new { type = "input_text", text },
            new { type = "input_image", image_url = "data:" + mime + ";base64," + b64 },
        };
    }

    // ------------------------------------------------------------------
    // Anthropic Claude（POST /v1/messages）
    // ------------------------------------------------------------------
    private async Task<string?> ChatAnthropicAsync(string system,
        IReadOnlyList<(string Role, string Content)> messages, CancellationToken ct,
        bool stream, Action<string>? onDelta, int maxOut = 8192)
    {
        var msgs = new List<object>();
        foreach (var (role, content) in messages)
        {
            string r = role == "assistant" ? "assistant" : "user";
            msgs.Add(new { role = r, content = PlainText(content) });
        }
        var payload = new Dictionary<string, object?>
        {
            ["model"] = Model,
            ["max_tokens"] = 8192,
            ["system"] = system,
            ["messages"] = msgs,
            ["stream"] = stream,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("x-api-key", ApiKey.Trim());
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            LastError = HttpError(resp);
            try
            {
                string errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (errBody.Length > 200) errBody = errBody[..200];
                LastError += " " + errBody;
            }
            catch { }
            return null;
        }
        if (!stream)
        {
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement.GetProperty("content");
            if (content.GetArrayLength() == 0) { LastError = "响应中没有 content"; return null; }
            LastError = null;
            return content[0].GetProperty("text").GetString() ?? "";
        }
        LastError = null;
        return await ReadSseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            ct, onDelta, ev =>
            {
                if (ev.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    return t.GetString() ?? "";
                return null;
            });
    }

    // ------------------------------------------------------------------
    // Google Gemini（generateContent / streamGenerateContent）
    // ------------------------------------------------------------------
    private async Task<string?> ChatGeminiAsync(string system,
        IReadOnlyList<(string Role, string Content)> messages, CancellationToken ct,
        bool stream, Action<string>? onDelta, int maxOut = 8192)
    {
        string url = Endpoint.Replace("{model}", Model);
        if (!url.Contains(":generateContent"))
            url = url.TrimEnd('/') + "/v1beta/models/" + Model + ":generateContent";
        if (stream)
            url = url.Replace(":generateContent", ":streamGenerateContent?alt=sse");
        var contents = new List<object>();
        foreach (var (role, content) in messages)
        {
            if (role == "tool") continue;
            string r = role == "assistant" ? "model" : "user";
            contents.Add(new { role = r, parts = new object[] { new { text = PlainText(content) } } });
        }
        var payload = new Dictionary<string, object?>
        {
            ["contents"] = contents,
            ["systemInstruction"] = new { parts = new object[] { new { text = system } } },
            ["generationConfig"] = new { maxOutputTokens = 8192 },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("x-goog-api-key", ApiKey.Trim());
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            LastError = HttpError(resp);
            try
            {
                string errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (errBody.Length > 200) errBody = errBody[..200];
                LastError += " " + errBody;
            }
            catch { }
            return null;
        }
        if (!stream)
        {
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("candidates", out var cands) || cands.GetArrayLength() == 0)
            {
                LastError = "响应中没有 candidates";
                return null;
            }
            LastError = null;
            var parts = cands[0].GetProperty("content").GetProperty("parts");
            var sb = new StringBuilder();
            foreach (var p in parts.EnumerateArray())
                if (p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    sb.Append(t.GetString());
            return sb.ToString();
        }
        LastError = null;
        return await ReadSseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            ct, onDelta, ev =>
            {
                if (ev.TryGetProperty("candidates", out var c) && c.GetArrayLength() > 0)
                {
                    var parts = c[0].GetProperty("content").GetProperty("parts");
                    foreach (var p in parts.EnumerateArray())
                        if (p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                            return t.GetString() ?? "";
                }
                return null;
            });
    }

    /// <summary>通用 SSE 读取：逐行解析 data: JSON，用 extract 提取增量文本。</summary>
    private static async Task<string?> ReadSseAsync(Stream stream, CancellationToken ct,
        Action<string>? onDelta, Func<JsonElement, string?> extract)
    {
        var sb = new StringBuilder();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
            string data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;
            try
            {
                using var doc = JsonDocument.Parse(data);
                string? chunk = extract(doc.RootElement);
                if (!string.IsNullOrEmpty(chunk))
                {
                    sb.Append(chunk);
                    onDelta?.Invoke(chunk);
                }
            }
            catch { /* 跳过无法解析的 SSE 行 */ }
        }
        return sb.ToString();
    }

    /// <summary>把多模态 IMG|… 标记降级为纯文本（Anthropic/Gemini 文本通道）。</summary>
    private static string PlainText(string content)
    {
        if (!content.StartsWith("IMG|", StringComparison.Ordinal)) return content;
        int p1 = content.IndexOf('|', 3);
        return p1 > 3 ? content[(p1 + 1)..] : content;
    }
}

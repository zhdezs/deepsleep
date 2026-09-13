using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler;

/// <summary>
/// 本地 LLM 客户端：通过 HTTP 调用 Ollama（默认 qwen2.5:1.5b）。
/// 零第三方依赖，仅用 System.Net.Http + System.Text.Json。
/// </summary>
public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _http = new();
    private string _baseUrl = "http://127.0.0.1:11434";
    private string _model = "qwen2.5:1.5b";
    private bool _available;
    private DateTime _lastCheck = DateTime.MinValue;

    public string Model
    {
        get => _model;
        set => _model = value;
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set => _baseUrl = value.TrimEnd('/');
    }

    /// <summary>最近一次调用失败的原因（用于界面诊断）。</summary>
    public string? LastError { get; private set; }

    /// <summary>最近一次探测是否在线（缓存 5 秒，避免每轮都打 API）。</summary>
    public bool Available
    {
        get
        {
            if ((DateTime.Now - _lastCheck).TotalSeconds > 5)
            {
                _available = false;
            }
            return _available;
        }
    }

    /// <summary>异步探测 Ollama 是否在线（返回模型名是否已存在）。</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/api/tags");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { _available = false; _lastCheck = DateTime.Now; return false; }
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("models", out var models))
            {
                foreach (var m in models.EnumerateArray())
                {
                    string? name = m.GetProperty("name").GetString();
                    if (name != null && (name == _model || name.StartsWith(_model + ":")))
                    {
                        _available = true;
                        _lastCheck = DateTime.Now;
                        return true;
                    }
                }
            }
            _available = false;
            _lastCheck = DateTime.Now;
            return false;
        }
        catch
        {
            _available = false;
            _lastCheck = DateTime.Now;
            return false;
        }
    }

    /// <summary>
    /// 让本地 LLM 生成一段回怼。
    /// 可指定模型名与 token 预算（思考型模型如 qwen3 需要更大 numPredict）。
    /// 失败（Ollama 未启动 / 超时 / 网络错误）返回 null，调用方回退本地引擎。
    /// </summary>
    public async Task<string?> ChatAsync(string system, string user,
        string? model = null, int numPredict = 1024, int timeoutSeconds = 120,
        CancellationToken ct = default)
        => await ChatAsync(system, new[] { (Role: "user", Content: user) }, model, numPredict,
            timeoutSeconds, ct);

    /// <summary>多轮消息版本（供 AI 助手使用）。消息角色：user / assistant / tool。</summary>
    public async Task<string?> ChatAsync(string system, IReadOnlyList<(string Role, string Content)> messages,
        string? model = null, int numPredict = 2048, int timeoutSeconds = 180,
        CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            // think:false 尝试关闭思考链（对 qwen2.5 等非思考模型无害；
            // 对升级 Ollama 后的 qwen3 能正确关闭思考，直接输出正文）。
            // 旧版 Ollama 下若思考链漏进 content，由 Engine.LooksLikeThinking 兜底回退。
            var msgs = new List<object> { new { role = "system", content = system } };
            foreach (var (role, content) in messages)
                msgs.Add(new { role = MapRole(role), content });

            var payload = new
            {
                model = model ?? _model,
                messages = msgs,
                stream = false,
                think = false,
                options = new { temperature = 0.7, num_predict = numPredict },
            };
            string json = JsonSerializer.Serialize(payload);
            using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

            using var resp = await _http.PostAsync(_baseUrl + "/api/chat", httpContent, cts.Token)
                                       .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)resp.StatusCode}";
                try
                {
                    string errBody = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    if (errBody.Length > 200) errBody = errBody[..200];
                    LastError += " " + errBody;
                }
                catch { /* 只保留状态码 */ }
                return null;
            }
            string body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            string? text = doc.RootElement.GetProperty("message").GetProperty("content").GetString();
            _available = true;
            _lastCheck = DateTime.Now;
            LastError = null;
            return text;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _available = false;
            _lastCheck = DateTime.Now;
            return null;
        }
    }

    private static string MapRole(string role) => role switch
    {
        "assistant" => "assistant",
        "tool" => "user",
        _ => "user",
    };

    /// <summary>流式版本：逐段回调增量内容，返回完整文本（供真·流式输出）。</summary>
    public async Task<string?> ChatStreamAsync(string system,
        IReadOnlyList<(string Role, string Content)> messages,
        Action<string>? onDelta = null, string? model = null, int numPredict = 2048,
        int timeoutSeconds = 180, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var msgs = new List<object> { new { role = "system", content = system } };
            foreach (var (role, content) in messages)
                msgs.Add(new { role = MapRole(role), content });
            var payload = new
            {
                model = model ?? _model,
                messages = msgs,
                stream = true,
                think = false,
                options = new { temperature = 0.7, num_predict = numPredict },
            };
            string json = JsonSerializer.Serialize(payload);
            using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

            using var resp = await _http.PostAsync(_baseUrl + "/api/chat", httpContent, cts.Token)
                                       .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)resp.StatusCode}";
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
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        string chunk = c.GetString() ?? "";
                        if (chunk.Length > 0)
                        {
                            sb.Append(chunk);
                            onDelta?.Invoke(chunk);
                        }
                    }
                }
                catch { /* 跳过无法解析的行 */ }
            }
            _available = true;
            _lastCheck = DateTime.Now;
            LastError = null;
            return sb.ToString();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _available = false;
            _lastCheck = DateTime.Now;
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler;

/// <summary>发给模型的原生工具定义（function calling）。 Name 用 ASCII 短名：GPT / Gemini 的函数名只认 ASCII 标识符，中文名会被拒或叫不动。</summary>
public sealed class ApiTool
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>JSON Schema 字符串（parameters / input_schema）。</summary>
    public string ParametersJson { get; set; } = "{\"type\":\"object\",\"properties\":{}}";
}

/// <summary>模型发起的一次原生工具调用（一轮可以有多条，对应 OpenAI 的 tool_calls 数组）。</summary>
public sealed class ApiToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>参数的原始 JSON 字符串（流式接口是一段一段拼起来的）。</summary>
    public string Arguments { get; set; } = "";
}

/// <summary>发给接口的一条消息。 比 (role, content) 多了「这一轮我调了哪些工具」和「这条结果对应哪次调用」—— 原生 function calling 的多轮上下文靠这两样才接得上。</summary>
public sealed class ApiMsg
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    /// <summary>role=assistant 时：这一轮发起的原生工具调用。</summary>
    public List<ApiToolCall>? ToolCalls { get; set; }
    /// <summary>role=tool 时：对应哪次调用（OpenAI / Responses / Anthropic 用它配对）。</summary>
    public string? ToolCallId { get; set; }
    /// <summary>role=tool 时：工具名（Gemini 的 functionResponse 只认名字，不认 id）。</summary>
    public string? ToolName { get; set; }

    public ApiMsg() { }
    public ApiMsg(string role, string content) { Role = role; Content = content; }
}

/// <summary>外部 API 客户端（OpenAI 兼容 chat/completions 接口，如 DeepSeek）。</summary>
public sealed class ApiClient : IDisposable
{
    // 超时一律交给 CancellationToken：HttpClient 默认 100 秒会硬砍请求，
    // 长对话在网关上经常要 100 秒以上，被砍之后又只表现为「返回空」，看起来像模型不给正文。
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    public string Url { get; set; } = "https://api.deepseek.com/chat/completions";
    public string Model { get; set; } = "deepseek-v4-flash";
    public string ApiKey { get; set; } = "";
    /// <summary>API 协议格式：openai（兼容）/ anthropic（Claude）/ gemini（Google）。</summary>
    public string Format { get; set; } = "openai";
    /// <summary>启用深度思考（DeepSeek v4 的 thinking 模式）。</summary>
    public bool Thinking { get; set; } = false;
    /// <summary>思考强度：low / medium / high（配合 Thinking 使用）。</summary>
    public string ReasoningEffort { get; set; } = "high";

    /// <summary>界面档位换成接口认的值：低=low、中=high、高=max。界面保持四档，发出去的是三档。</summary>
    private string EffortParam => (ReasoningEffort ?? "").Trim().ToLowerInvariant() switch
    {
        "low" => "low",
        "medium" => "high",
        "high" => "max",
        var v => v,
    };

    /// <summary>关思考参数被哪个「地址|模型」拒过。同一条线不再重复试，换模型/换线路自然恢复。</summary>
    private string? _thinkingRejectedFor;

    /// <summary>设置里关着思考时要不要明说 thinking:{type:disabled}。</summary>
    private bool ForceThinkingOff => _thinkingRejectedFor != (Url + "|" + Model);

    /// <summary>这一轮会不会真的发「关思考」参数（日志用）。</summary>
    public bool SendingThinkingOff => !Thinking && ForceThinkingOff;

    /// <summary>最近一次调用里模型吐出的思考 / 推理内容（reasoning_content / reasoning / Responses 的 reasoning 条目）。</summary>
    public string Reasoning { get; private set; } = "";

    private readonly StringBuilder _reasonLog = new();

    private void ResetReasoning()
    {
        _reasonLog.Clear();
        Reasoning = "";
    }

    private void AddReasoning(string? chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        _reasonLog.Append(chunk);
        Reasoning = _reasonLog.ToString();
    }

    public bool Configured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>最近一次调用失败的原因（用于界面诊断）。</summary>
    public string? LastError { get; private set; }

    /// <summary>最近一次调用里模型发起的原生工具调用（可能一次好几个）。 上层拿它去执行，比让它自己吐 JSON 稳得多。</summary>
    public List<ApiToolCall> ToolCalls { get; } = new();

    /// <summary>这一轮原生工具走通了吗（发了 tools、接口也认了）。 false 且 ToolCalls 为空 = 走了文本 JSON 协议。</summary>
    public bool NativeToolsUsed { get; private set; }

    /// <summary>哪些「地址|模型」拒过 tools 参数：同一条线不再重复试，直接走文本 JSON 协议。</summary>
    private readonly HashSet<string> _toolsRejected = new();

    private string ToolKey => Url + "|" + Model;

    /// <summary>这条线路还值得发 tools 吗（拒过一次就不再发，省一次必然失败的往返）。</summary>
    public bool NativeToolsAllowed => !_toolsRejected.Contains(ToolKey);

    /// <summary>线路为什么不用原生工具（诊断用；正常返回空）。</summary>
    public string NativeToolsWhy
    {
        get
        {
            if (!NativeToolsAllowed) return "接口拒过 tools 参数（已自动退回文本 JSON 协议）";
            if (EffectiveFormat is not ("openai" or "anthropic" or "gemini" or "responses"))
                return "协议 " + EffectiveFormat + " 不支持原生工具";
            return "";
        }
    }

    /// <summary>每轮开始清空上一轮的工具调用，避免「这一轮明明没调工具却执行了上一轮的」。</summary>
    private void ResetToolCalls()
    {
        ToolCalls.Clear();
        NativeToolsUsed = false;
    }

    /// <summary>把 Schema 字符串解析成可复用的 JsonElement（克隆后脱离 JsonDocument 生命周期）。</summary>
    private static JsonElement SchemaElement(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json)
                ? "{\"type\":\"object\",\"properties\":{}}" : json);
            return d.RootElement.Clone();
        }
        catch
        {
            using var d = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
            return d.RootElement.Clone();
        }
    }

    /// <summary>把模型可能返回的 arguments（字符串或对象）统一成 JsonElement。</summary>
    private static JsonElement ArgsElement(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
        try
        {
            using var d = JsonDocument.Parse(raw);
            return d.RootElement.Clone();
        }
        catch
        {
            // 半截 JSON（流被截断）：交空参数，让工具回一句「参数错误」让模型重试
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }

    /// <summary>这一轮是不是本机等超时了（不是用户点停止，也不是模型不给正文）。</summary>
    public bool TimeoutHit { get; private set; }

    /// <summary>最近一次回复是不是被输出长度上限截断了（finish_reason=length）。</summary>
    public bool LastTruncated { get; internal set; }

    /// <summary>最近一次返回的 finish_reason：stop / length / tool_calls。为空表示这轮没拿到。</summary>
    public string? LastFinish { get; private set; }

    /// <summary>最近一轮思考用了多少 token（网关给了 usage 才知道，否则 -1）。</summary>
    public int LastReasoningTokens { get; private set; } = -1;

    /// <summary>接口通了、状态码也正常，但正文一个字都没给（换模型后最容易撞上）。</summary>
    public bool EmptyReply { get; set; }

    /// <summary>规范化后的请求地址。用户容易只填主机名（例如 https://api.deepseek.com/）， 直接 POST 根地址会 404，所以这里按协议自动补全路径。</summary>
    public string Endpoint => NormalizeUrl(Url, EffectiveFormat);

    /// <summary>实际使用的协议：按地址自动纠正明显不匹配的格式。</summary>
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
            502 or 503 or 504 => "（网关等不到上游就返回了，稍后重试或换线路）",
            524 => "（网关等待上游超时：这轮请求太大或上游排队太久）",
            >= 500 => "（服务端错误，稍后重试）",
            _ => "",
        };
        return $"HTTP {(int)resp.StatusCode}{hint}";
    }

    /// <summary>调用外部 API 生成回怼。失败（无 key / 网络 / 超时 / 解析错误）返回 null。</summary>
    public async Task<string?> ChatAsync(string system, string user,
        int timeoutSeconds = 300, CancellationToken ct = default)
        => await ChatAsync(system, new List<ApiMsg> { new("user", user) }, timeoutSeconds, ct);

    /// <summary>多轮消息版本（旧签名，保持兼容）。消息角色：user / assistant / tool。</summary>
    public Task<string?> ChatAsync(string system, IReadOnlyList<(string Role, string Content)> messages,
        int timeoutSeconds = 300, CancellationToken ct = default,
        int maxOut = 8192, bool noThinking = false, IReadOnlyList<ApiTool>? tools = null)
        => ChatAsync(system, ToApiMsgs(messages), timeoutSeconds, ct, maxOut, noThinking, tools);

    /// <summary>多轮消息版本（带原生工具上下文）。</summary>
    public async Task<string?> ChatAsync(string system, IReadOnlyList<ApiMsg> messages,
        int timeoutSeconds = 300, CancellationToken ct = default,
        int maxOut = 8192, bool noThinking = false, IReadOnlyList<ApiTool>? tools = null)
    {
        if (!Configured)
        {
            LastError = "未配置 API Key";
            return null;
        }
        // 一律走流式。非流式要等服务端生成完才回第一个字节，长回答几分钟不动，看起来像卡死。
        return await ChatStreamAsync(system, messages, onDelta: null,
            timeoutSeconds: timeoutSeconds, ct: ct,
            maxOut: maxOut, noThinking: noThinking, tools: tools).ConfigureAwait(false);
    }

    // ── 图片输入（base64 直接内联进请求体）──────────────────────────────────

    /// <summary>已经确认不吃图的「地址|模型」：记住，别每次看图都白撞一次 400。</summary>
    private static readonly ConcurrentDictionary<string, byte> ImageRejected = new();

    /// <summary>当前配置的模型是不是已经被判定为「不支持图片输入」。</summary>
    public bool ImageRejectedHere => ImageRejected.ContainsKey(Endpoint + "|" + Model);

    private const string ImageSystemPrompt =
        "你是看图助手。用户会给你一张图片和一个问题，请直接看图回答，用简体中文，" +
        "不要客套、不要重复问题。看不清就说看不清，不要猜。";

    /// <summary>
    /// 把本地图片读成 base64，直接放进一次普通 API 调用的消息里（多模态），返回模型对图片的回答。
    /// 用当前配置的主模型，不再需要单独配「图像理解」的第二个 Key。
    /// 失败（未配置 / 文件不存在 / 超过上限 / 模型不吃图）返回 null，原因写进 LastError。
    /// </summary>
    public async Task<string?> AskImageAsync(string imagePath, string question,
        CancellationToken ct = default, int timeoutSeconds = 180, int maxOut = 2048)
    {
        if (!Configured)
        {
            LastError = "未配置 API Key";
            return null;
        }
        if (ImageRejectedHere)
        {
            LastError = "当前模型不支持图片输入（之前发图被拒过）";
            return null;
        }
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            LastError = "图片不存在：" + imagePath;
            return null;
        }
        long len;
        try { len = new FileInfo(imagePath).Length; }
        catch (Exception ex) { LastError = ex.Message; return null; }
        if (len > MaxImageBytes)
        {
            LastError = $"图片 {len / 1024.0 / 1024.0:0.0} MB，超过 {MaxImageBytes / 1024 / 1024} MB 上限";
            return null;
        }

        var msgs = new List<ApiMsg>
        {
            new("user", "IMG|" + imagePath + "|" + (string.IsNullOrWhiteSpace(question)
                ? "请描述这张图片的内容；有文字就逐字读出来，有界面/图表就说明结构和关键信息。"
                : question)),
        };
        string? reply = await ChatAsync(ImageSystemPrompt, msgs, timeoutSeconds, ct, maxOut)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(reply)) return reply;

        if (LooksLikeImageRejected(LastError))
            ImageRejected[Endpoint + "|" + Model] = 1;
        return null;
    }

    /// <summary>错误里同时出现「图片」和「不认/非法」才判成模型不吃图，别的 400 不能算。</summary>
    private static bool LooksLikeImageRejected(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        string e = error.ToLowerInvariant();
        if (!(e.Contains("image") || e.Contains("vision") || e.Contains("图片") || e.Contains("multimodal")))
            return false;
        return e.Contains("unsupported") || e.Contains("not support") || e.Contains("does not support") ||
               e.Contains("invalid") || e.Contains("unknown") || e.Contains("not allowed") ||
               e.Contains("http 400") || e.Contains("http 422");
    }

    /// <summary>把旧的 (role, content) 列表转成 ApiMsg（无原生工具上下文）。</summary>
    public static List<ApiMsg> ToApiMsgs(IReadOnlyList<(string Role, string Content)> messages)    {
        var list = new List<ApiMsg>(messages.Count);
        foreach (var (role, content) in messages) list.Add(new ApiMsg(role, content));
        return list;
    }

    private static string MapRole(string role) => role switch
    {
        "assistant" => "assistant",
        "tool" => "user",
        _ => "user",
    };

    /// <summary>单张图片的大小上限：Anthropic 单图 5MB 是最紧的一家，取它当通用线。</summary>
    public const long MaxImageBytes = 5L * 1024 * 1024;

    /// <summary>IMG|路径|文本 协议拆出来的东西。</summary>
    private readonly record struct ImagePart(string Mime, string Base64, string Text);

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/png",
    };

    /// <summary>
    /// 把 IMG|路径|文本 拆成图片 + 文字。读不到文件、格式不是图片、超过上限都返回 null（调用方退化成纯文本）。
    /// 图片直接读成 base64 塞进请求体 —— 不再依赖外部图床或第二个「视觉模型」。
    /// </summary>
    private static ImagePart? ReadImage(string content, long maxBytes = MaxImageBytes)
    {
        if (!content.StartsWith("IMG|", StringComparison.Ordinal)) return null;
        // "IMG|" 正好 4 个字符：必须从这里往后找第二个竖线。
        // 以前从索引 3 找，一找就命中前缀自己的 '|'，于是永远判成「格式不对」退回纯文本 ——
        // 多模态图片其实从来没真正发出去过。
        const int prefix = 4;
        int p1 = content.IndexOf('|', prefix);
        if (p1 <= prefix) return null;
        string path = content[prefix..p1];
        string text = content[(p1 + 1)..];
        if (!File.Exists(path)) return null;
        var fi = new FileInfo(path);
        if (fi.Length <= 0 || fi.Length > maxBytes) return null;
        try
        {
            return new ImagePart(MimeOf(path), Convert.ToBase64String(File.ReadAllBytes(path)), text);
        }
        catch { return null; }   // 文件被占用 / 无权限：当纯文本处理，别把整轮请求搞挂
    }

    /// <summary>OpenAI 兼容：content 数组里 image_url 接 data URL。</summary>
    private static object BuildContent(string content)
    {
        var img = ReadImage(content);
        if (img == null) return content.StartsWith("IMG|", StringComparison.Ordinal) ? PlainText(content) : content;
        return new object[]
        {
            new { type = "text", text = img.Value.Text },
            new { type = "image_url", image_url = new { url = "data:" + img.Value.Mime + ";base64," + img.Value.Base64 } },
        };
    }

    /// <summary>IMG|路径|文本 里取出给人看的文字部分（不走图片时用）。</summary>
    private static string ImgText(string content)
    {
        if (!content.StartsWith("IMG|", StringComparison.Ordinal)) return content;
        int p1 = content.IndexOf('|', 4);
        return p1 <= 4 ? content : content[(p1 + 1)..];
    }

    /// <summary>一次流式请求的结局：正常结束 / 长时间无响应被掐 / 用户取消 / 出错。</summary>
    private enum StreamOutcome { Done, Idle, Cancelled, Failed }

    /// <summary>最近一次流式调用是否被「长时间没有任何增量」的看门狗掐断过。 上层据此判断还要不要再补一次普通请求（已经自动重试过的就别再等了）。</summary>
    public bool IdleAborted { get; private set; }

    /// <summary>流式版本（旧签名，保持兼容）。</summary>
    public Task<string?> ChatStreamAsync(string system,
        IReadOnlyList<(string Role, string Content)> messages,
        Action<string>? onDelta = null, int timeoutSeconds = 600, CancellationToken ct = default,
        Action<string>? onReasoning = null,
        int idleSeconds = 30, int maxResumes = 3, Action<string>? onStatus = null,
        int maxOut = 8192, bool noThinking = false, IReadOnlyList<ApiTool>? tools = null)
        => ChatStreamAsync(system, ToApiMsgs(messages), onDelta, timeoutSeconds, ct, onReasoning,
            idleSeconds, maxResumes, onStatus, maxOut, noThinking, tools);

    /// <summary>流式版本：逐段回调增量内容，返回完整文本（供真·流式输出）。 收的是累积到当前的思考全文（不是增量）， 界面直接整段替换即可 —— 这样思考内容和正文一样是边生成边显示的。</summary>
    /// <param name="tools">原生工具定义；给了就优先走 function calling（模型直接发起工具调用，不用吐 JSON）。</param>
    public async Task<string?> ChatStreamAsync(string system,
        IReadOnlyList<ApiMsg> messages,
        Action<string>? onDelta = null, int timeoutSeconds = 600, CancellationToken ct = default,
        Action<string>? onReasoning = null,
        int idleSeconds = 30, int maxResumes = 3, Action<string>? onStatus = null,
        int maxOut = 8192, bool noThinking = false, IReadOnlyList<ApiTool>? tools = null)
    {
        if (!Configured)
        {
            LastError = "未配置 API Key";
            return null;
        }
        ResetReasoning();
        ResetToolCalls();
        IdleAborted = false;
        TimeoutHit = false;
        LastFinish = null;
        LastTruncated = false;
        LastReasoningTokens = -1;
        // 线路拒过 tools 就别再发了：每轮都撞一次 400 只会让用户多等一轮
        var useTools = (tools != null && tools.Count > 0 && NativeToolsAllowed) ? tools : null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            if (EffectiveFormat == "anthropic")
                return await ChatAnthropicAsync(system, messages, cts.Token, stream: true, onDelta: onDelta,
                    maxOut: maxOut, idleSeconds: Math.Max(idleSeconds, 90), tools: useTools);   // 首字节可能排队，比 OpenAI 线路给得宽
            if (EffectiveFormat == "gemini")
                return await ChatGeminiAsync(system, messages, cts.Token, stream: true, onDelta: onDelta, tools: useTools);
            if (EffectiveFormat == "responses")
                return await ChatResponsesAsync(system, messages, cts.Token, stream: true, onDelta: onDelta, tools: useTools);

            var sb = new StringBuilder();
            int resumes = 0;
            while (true)
            {
                // 续写轮：把已经吐出来的内容当成 assistant 前文补进去，模型才知道从哪儿接。
                // 一个字都没来的话（压根没开始），原样重发同一份请求即可。
                string already = sb.ToString();
                var round = resumes == 0 ? messages : WithContinuation(messages, already);

                // 续写轮先攒一小段再往外发：不是所有模型都老实「接着写」，有的会把整段从头背一遍。
                var pending = new StringBuilder();
                bool flushed = false;
                void Sink(string chunk)
                {
                    if (flushed) { sb.Append(chunk); onDelta?.Invoke(chunk); return; }
                    if (already.Length == 0) { flushed = true; sb.Append(chunk); onDelta?.Invoke(chunk); return; }
                    pending.Append(chunk);
                    if (pending.Length < 64) return;
                    flushed = true;
                    string add = TrimReplay(already, pending.ToString());
                    if (add.Length > 0) { sb.Append(add); onDelta?.Invoke(add); }
                }

                var outcome = await StreamOnceAsync(system, round, Sink,
                    ct, onReasoning, idleSeconds, timeoutSeconds, maxOut, noThinking, tools: useTools).ConfigureAwait(false);

                if (!flushed && pending.Length > 0)      // 这一轮很短，收尾时再判一次
                {
                    string add = TrimReplay(already, pending.ToString());
                    if (add.Length > 0) { sb.Append(add); onDelta?.Invoke(add); }
                }

                if (outcome == StreamOutcome.Done)
                {
                    LastError = null;
                    return sb.ToString();
                }
                if (outcome != StreamOutcome.Idle)
                    return sb.Length > 0 ? sb.ToString() : null;   // 取消 / 出错：有半截就交半截

                IdleAborted = true;
                if (++resumes > maxResumes)
                {
                    LastError = $"模型连续 {resumes} 次在 {idleSeconds} 秒内没有响应，已停止等待";
                    return sb.Length > 0 ? sb.ToString() : null;
                }
                onStatus?.Invoke($"⏸ 模型 {idleSeconds} 秒无响应，已暂停并自动恢复（{resumes}/{maxResumes}）");
                // 稍等一下再发：立刻重发多半会撞同一个坑（网关排队 / 连接被吞）
                await Task.Delay(600, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 用户没点停止却抛了取消：是这条链路自己等超时了（总时长到点 / 看门狗掐断）。
            // 以前这里写 null，上层只能判成「接口通了但没正文」，把责任推给模型，误导排查方向。
            LastError = ct.IsCancellationRequested
                ? "已取消"
                : "等了太久没拿到回复（网关排队或上游太慢）";
            TimeoutHit = !ct.IsCancellationRequested;
            return null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    /// <summary>续写去重：模型如果没听招呼、把已经写过的开头又背了一遍，就把重复的那截剪掉。 只认「整段复述」和「≥4 字的重合」，免得把正常要写的短句（叠词、重复标点）也剪了。</summary>
    private static string TrimReplay(string already, string more)
    {
        if (already.Length == 0 || more.Length == 0) return more;
        if (more.Length <= 24 && already.EndsWith(more, StringComparison.Ordinal)) return "";
        int max = Math.Min(already.Length, more.Length);
        for (int len = max; len >= 4; len--)
        {
            if (string.CompareOrdinal(already, already.Length - len, more, 0, len) == 0)
                return more[len..];
        }
        return more;
    }

    /// <summary>续写用的消息表：在原始对话后面补一段「我已经写到这儿了」。</summary>
    private static List<ApiMsg> WithContinuation(IReadOnlyList<ApiMsg> messages, string partial)
    {
        var list = new List<ApiMsg>(messages.Count + 2);
        list.AddRange(messages);
        if (partial.Length > 0)
        {
            list.Add(new ApiMsg("assistant", partial));
            list.Add(new ApiMsg("user", "（上一条回复在传输中断了。请紧接着写下去，不要重复已经写过的内容，也不要重新开头。）"));
        }
        return list;
    }

/// <summary>发一次 SSE 请求，把增量回调出去，重试交给调用方。</summary>
    private async Task<StreamOutcome> StreamOnceAsync(string system,
        IReadOnlyList<ApiMsg> messages,
        Action<string> onDelta, CancellationToken outerCt, Action<string>? onReasoning,
        int idleSeconds, int timeoutSeconds, int maxOut = 8192, bool noThinking = false,
        bool allowThinkingParam = true, IReadOnlyList<ApiTool>? tools = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        // 每次有动静就重新上弦；离总超时越近，上弦越短，避免两套超时各算各的
        void Rearm()
        {
            double left = (deadline - DateTime.UtcNow).TotalSeconds;
            if (left <= 0) { try { cts.Cancel(); } catch { } return; }
            double idle = Math.Max(1, Math.Min(idleSeconds, left));
            try { cts.CancelAfter(TimeSpan.FromSeconds(idle)); } catch { }
        }
        Rearm();
        try
        {
            var msgs = BuildOpenAiMsgs(messages, system);
            bool sentThinkingOff = false;
            var payload = new Dictionary<string, object?>
            {
                ["model"] = Model,
                ["messages"] = msgs,
                ["temperature"] = 0.9,
                ["max_tokens"] = maxOut,
                ["stream"] = true,
            };
            bool sentTools = false;
            if (tools is { Count: > 0 })
            {
                payload["tools"] = tools.Select(ToolPayload).ToArray();
                payload["tool_choice"] = "auto";
                sentTools = true;
            }
            if (Thinking)
            {
                payload["thinking"] = new { type = "enabled" };
                if (!string.IsNullOrWhiteSpace(EffortParam))
                    payload["reasoning_effort"] = EffortParam;
            }
            else if (allowThinkingParam && (noThinking || ForceThinkingOff))
            {
                // 有些推理模型默认就会思考，不传参数等于让它一直想：长对话里输出预算全被思考吃掉，
                // 正文一个字不剩（finish_reason=length）。设置里既然关着思考，就得明说。
                payload["thinking"] = new { type = "disabled" };
                sentThinkingOff = true;
            }
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey.Trim());

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                                       .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = HttpError(resp);
                string errBody = "";
                try
                {
                    errBody = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    if (errBody.Length > 200) errBody = errBody[..200];
                    LastError += " " + errBody;
                }
                catch { /* 只保留状态码 */ }
                // 这个网关/模型不认 tools 参数：记住这条线，去掉 tools 重发一次，退回文本 JSON 协议。
                // 别把「不支持函数调用」直接甩给用户一句 400 —— 老网关、部分国产模型都这样。
                if (sentTools && ((int)resp.StatusCode == 400 || (int)resp.StatusCode == 422) &&
                    LooksLikeToolsUnsupported(errBody))
                {
                    _toolsRejected.Add(ToolKey);
                    LastError = "接口不支持原生工具调用，已退回文本 JSON 协议";
                    return await StreamOnceAsync(system, messages, onDelta, outerCt, onReasoning,
                        idleSeconds, timeoutSeconds, maxOut, noThinking, allowThinkingParam, tools: null)
                        .ConfigureAwait(false);
                }
                // 这个模型不认 thinking 参数：记住，去掉它重发一次。
                // 别把「参数不支持」当成「模型不可用」——以前这种情况只会甩给用户一句 400。
                if (sentThinkingOff && ((int)resp.StatusCode == 400 || (int)resp.StatusCode == 422))
                {
                    _thinkingRejectedFor = Url + "|" + Model;
                    return await StreamOnceAsync(system, messages, onDelta, outerCt, onReasoning,
                        idleSeconds, timeoutSeconds, maxOut, noThinking, allowThinkingParam: false)
                        .ConfigureAwait(false);
                }
                return StreamOutcome.Failed;
            }
            Rearm();   // 响应头到了：从现在起重新算「多久没动静」

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            // 原生工具调用是流式拼出来的：先按 index 归位，name / arguments 逐段累加
            var calls = new List<ApiToolCall>();
            while (true)
            {
                //  这里必须用带看门狗的 token。以前传的是外层 ct，CancelAfter 压根管不到这句，
                //   服务端一声不吭挂住时就能一直等下去（用户看到的是「转圈转到天荒地老」）。
                string? line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
                if (line == null) break;
                Rearm();   // 来字节了就算活着（空行 / keep-alive 注释也算）
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
                        fr.ValueKind == JsonValueKind.String && fr.GetString() is string frs && frs.Length > 0)
                    {
                        LastFinish = frs;
                        if (frs == "length") LastTruncated = true;
                    }
                    // 有些网关在最后一包里带 usage，能直接看出思考吃掉了多少输出预算
                    if (doc.RootElement.TryGetProperty("usage", out var us) && us.ValueKind == JsonValueKind.Object &&
                        us.TryGetProperty("completion_tokens_details", out var ctd) &&
                        ctd.ValueKind == JsonValueKind.Object &&
                        ctd.TryGetProperty("reasoning_tokens", out var rt) && rt.TryGetInt32(out int rtn))
                        LastReasoningTokens = rtn;
                    var delta = choices[0].GetProperty("delta");
                    // 原生工具调用（一轮可能好几条）：OpenAI 兼容线路走 delta.tool_calls
                    if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                        AccumulateToolCalls(tcs, calls);
                    // 思考增量单独收集：界面上折叠，不跟正文混在一起。
                    // 同时按「累积全文」回调给界面 —— 思考也要流式显示，不能等整轮跑完才出现。
                    if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                    {
                        AddReasoning(rc.GetString());
                        onReasoning?.Invoke(Reasoning);
                    }
                    else if (delta.TryGetProperty("reasoning", out var rj) && rj.ValueKind == JsonValueKind.String)
                    {
                        AddReasoning(rj.GetString());
                        onReasoning?.Invoke(Reasoning);
                    }
                    if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        string chunk = c.GetString() ?? "";
                        if (chunk.Length > 0) onDelta(chunk);
                    }
                }
                catch { /* 跳过无法解析的 SSE 行 */ }
            }
            if (sentTools && calls.Count > 0) CommitToolCalls(calls);
            return StreamOutcome.Done;
        }
        catch (OperationCanceledException)
        {
            if (outerCt.IsCancellationRequested)
            {
                LastError = "已取消";
                return StreamOutcome.Cancelled;
            }
            LastError = $"模型 {idleSeconds} 秒没有响应";
            return StreamOutcome.Idle;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return StreamOutcome.Failed;
        }
    }

    // ── 原生工具调用（function calling）通用零件 ──────────────────────────────

    /// <summary>OpenAI 兼容消息表：assistant 带 tool_calls、tool 带 tool_call_id，模型才认这是「同一轮的工具往返」。</summary>
    private static List<object> BuildOpenAiMsgs(IReadOnlyList<ApiMsg> messages, string? system = null)
    {
        var msgs = new List<object>(messages.Count + 1);
        // 系统提示词必须排在最前面：这里替换过一次消息构造，漏了它，模型就看不到身份、规则、记忆和技能
        // （表现是「答得像个没有设定的通用助手」，而且 Router 的规划提示也一并失效）
        if (!string.IsNullOrWhiteSpace(system))
            msgs.Add(new { role = "system", content = system });
        foreach (var m in messages)
        {
            if (m.Role == "tool" && !string.IsNullOrEmpty(m.ToolCallId))
            {
                msgs.Add(new { role = "tool", tool_call_id = m.ToolCallId, content = m.Content ?? "" });
                continue;
            }
            if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
            {
                msgs.Add(new
                {
                    role = "assistant",
                    content = string.IsNullOrWhiteSpace(m.Content) ? (object?)null : BuildContent(m.Content),
                    tool_calls = m.ToolCalls.Select(c => new
                    {
                        id = c.Id,
                        type = "function",
                        function = new
                        {
                            name = c.Name,
                            arguments = string.IsNullOrWhiteSpace(c.Arguments) ? "{}" : c.Arguments,
                        },
                    }).ToArray(),
                });
                continue;
            }
            msgs.Add(new { role = MapRole(m.Role), content = BuildContent(m.Content ?? "") });
        }
        return msgs;
    }

    /// <summary>OpenAI 的 tools 项。</summary>
    private static object ToolPayload(ApiTool t) => new
    {
        type = "function",
        function = new
        {
            name = t.Name,
            description = t.Description,
            parameters = SchemaElement(t.ParametersJson),
        },
    };

    /// <summary>tools 被拒？只在「401/400 且明确说不支持这个参数」时判是，别的 400 不能被误判成不支持。</summary>
    private static bool LooksLikeToolsUnsupported(string body)
    {
        string b = (body ?? "").ToLowerInvariant();
        if (b.Length == 0) return false;
        if (!(b.Contains("tool") || b.Contains("function"))) return false;
        return b.Contains("unsupported") || b.Contains("not support") || b.Contains("does not support") ||
               b.Contains("doesn't support") || b.Contains("unknown parameter") ||
               b.Contains("unrecognized") || b.Contains("invalid parameter") ||
               b.Contains("extra fields") || b.Contains("not allowed") || b.Contains("not permitted");
    }

    /// <summary>流式 delta.tool_calls 累加：按 index 归位，name / arguments 分段拼接。</summary>
    private static void AccumulateToolCalls(JsonElement tcs, List<ApiToolCall> calls)
    {
        foreach (var tc in tcs.EnumerateArray())
        {
            if (tc.ValueKind != JsonValueKind.Object) continue;
            int idx = calls.Count;
            if (tc.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number &&
                ix.TryGetInt32(out int iv) && iv >= 0) idx = iv;
            while (calls.Count <= idx) calls.Add(new ApiToolCall());
            var call = calls[idx];
            if (tc.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String)
            {
                string id = idv.GetString() ?? "";
                if (id.Length > 0) call.Id = id;
            }
            if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
            {
                if (fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                    call.Name += nm.GetString();
                if (fn.TryGetProperty("arguments", out var ag) && ag.ValueKind == JsonValueKind.String)
                    call.Arguments += ag.GetString();
            }
        }
    }

    /// <summary>把这一轮的调用交给上层（补 id、丢掉没有名字的残片）。</summary>
    private void CommitToolCalls(IEnumerable<ApiToolCall> calls)
    {
        foreach (var c in calls)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) continue;
            if (string.IsNullOrEmpty(c.Id)) c.Id = "call_" + (ToolCalls.Count + 1);
            if (string.IsNullOrWhiteSpace(c.Arguments)) c.Arguments = "{}";
            ToolCalls.Add(c);
        }
        NativeToolsUsed = ToolCalls.Count > 0;
    }

    public void Dispose() => _http.Dispose();

    // Responses API：系统提示走 instructions，对话走 input，结果在 output。
    private async Task<string?> ChatResponsesAsync(string system,
        IReadOnlyList<ApiMsg> messages, CancellationToken ct,
        bool stream, Action<string>? onDelta, int maxOut = 8192, IReadOnlyList<ApiTool>? tools = null)
    {
        var input = new List<object>();
        foreach (var m in messages)
        {
            string content = m.Content ?? "";
            if (m.Role == "tool" && !string.IsNullOrEmpty(m.ToolCallId))
            {
                // 原生工具回执：call_id 必须和上面的 function_call 对上
                input.Add(new { type = "function_call_output", call_id = m.ToolCallId, output = content });
                continue;
            }
            if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
            {
                if (!string.IsNullOrWhiteSpace(content))
                    input.Add(new { role = "assistant", content = BuildResponsesContent(content) });
                foreach (var c in m.ToolCalls)
                    input.Add(new
                    {
                        type = "function_call",
                        call_id = c.Id,
                        name = c.Name,
                        arguments = string.IsNullOrWhiteSpace(c.Arguments) ? "{}" : c.Arguments,
                    });
                continue;
            }
            if (string.IsNullOrEmpty(content)) continue;
            if (m.Role == "tool")
            {
                // Responses API 没有 tool 角色，文本协议的工具结果用 user 交回
                input.Add(new { role = "user", content = "[工具执行结果]\n" + content });
                continue;
            }
            input.Add(new
            {
                role = m.Role == "assistant" ? "assistant" : "user",
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
        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools.Select(t => new
            {
                type = "function",
                name = t.Name,
                description = t.Description,
                parameters = SchemaElement(t.ParametersJson),
            }).ToArray();
            payload["tool_choice"] = "auto";
        }
        // Responses 端点对参数更严格：只带必要字段，temperature 之类留给服务端默认，
        // 只有显式开了深度思考才带 reasoning.effort。
        if (Thinking)
            payload["reasoning"] = new { effort = string.IsNullOrWhiteSpace(EffortParam) ? "medium" : EffortParam };

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey.Trim());

        using var resp = await _http.SendAsync(req,
            stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            LastError = HttpError(resp);
            string errBody = "";
            try
            {
                errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (errBody.Length > 200) errBody = errBody[..200];
                LastError += " " + errBody;
            }
            catch { /* 只保留状态码 */ }
            if (tools is { Count: > 0 } && ((int)resp.StatusCode == 400 || (int)resp.StatusCode == 422) &&
                LooksLikeToolsUnsupported(errBody))
            {
                _toolsRejected.Add(ToolKey);
                return await ChatResponsesAsync(system, messages, ct, stream, onDelta, maxOut, tools: null)
                    .ConfigureAwait(false);
            }
            return null;
        }

        if (stream)
        {
            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var calls = new List<ApiToolCall>();
            var byItem = new Dictionary<string, int>();
            string? text = await ReadSseAsync(s, ct, onDelta, ev =>
            {
                if (!ev.TryGetProperty("type", out var ty) || ty.ValueKind != JsonValueKind.String) return null;
                string type = ty.GetString() ?? "";
                // 原生工具调用：output_item.added 报名字，arguments 是分段来的
                if (type == "response.output_item.added" && ev.TryGetProperty("item", out var item) &&
                    item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("type", out var it) && it.ValueKind == JsonValueKind.String &&
                    it.GetString() == "function_call")
                {
                    string itemId = item.TryGetProperty("id", out var iid) && iid.ValueKind == JsonValueKind.String
                        ? iid.GetString() ?? "" : "";
                    string callId = item.TryGetProperty("call_id", out var cid) && cid.ValueKind == JsonValueKind.String
                        ? cid.GetString() ?? "" : itemId;
                    string name = item.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                        ? nm.GetString() ?? "" : "";
                    string args = item.TryGetProperty("arguments", out var ag) && ag.ValueKind == JsonValueKind.String
                        ? ag.GetString() ?? "" : "";
                    calls.Add(new ApiToolCall { Id = callId, Name = name, Arguments = args });
                    if (itemId.Length > 0) byItem[itemId] = calls.Count - 1;
                    return null;
                }
                if (type == "response.function_call_arguments.delta")
                {
                    string itemId = ev.TryGetProperty("item_id", out var iid2) && iid2.ValueKind == JsonValueKind.String
                        ? iid2.GetString() ?? "" : "";
                    if (byItem.TryGetValue(itemId, out int idx) &&
                        ev.TryGetProperty("delta", out var d2) && d2.ValueKind == JsonValueKind.String)
                        calls[idx].Arguments += d2.GetString();
                    return null;
                }
                if (type == "response.output_item.done" && ev.TryGetProperty("item", out var done) &&
                    done.ValueKind == JsonValueKind.Object &&
                    done.TryGetProperty("type", out var dt) && dt.ValueKind == JsonValueKind.String &&
                    dt.GetString() == "function_call")
                {
                    // 收尾包里通常带完整 arguments，用它覆盖分段拼出来的结果
                    string itemId = done.TryGetProperty("id", out var did) && did.ValueKind == JsonValueKind.String
                        ? did.GetString() ?? "" : "";
                    if (byItem.TryGetValue(itemId, out int idx))
                    {
                        if (done.TryGetProperty("arguments", out var da) && da.ValueKind == JsonValueKind.String)
                        {
                            string full = da.GetString() ?? "";
                            if (full.Length > 0) calls[idx].Arguments = full;
                        }
                        if (done.TryGetProperty("call_id", out var dc) && dc.ValueKind == JsonValueKind.String)
                        {
                            string cid = dc.GetString() ?? "";
                            if (cid.Length > 0) calls[idx].Id = cid;
                        }
                    }
                    return null;
                }
                if (type == "response.output_text.delta")
                    return ev.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString() : null;
                // 思考 / 推理事件：只收进思考缓冲，绝不返回给正文
                if (type is "response.reasoning_summary_text.delta" or "response.reasoning_text.delta" or
                    "response.reasoning_summary.delta")
                {
                    if (ev.TryGetProperty("delta", out var rd) && rd.ValueKind == JsonValueKind.String)
                        AddReasoning(rd.GetString());
                    return null;
                }
                if (type == "response.incomplete")
                {
                    // 被输出长度上限截断：标记一下，上层会自动接着往下写，别停在半截
                    LastTruncated = true;
                    LastError = "Responses 流式事件：" + type;
                }
                else if (type == "response.completed" && ResponsesEventTruncated(ev))
                {
                    LastTruncated = true;
                }
                else if (type is "response.failed" or "error")
                {
                    LastError = "Responses 流式事件：" + type;
                }
                return null;
            }).ConfigureAwait(false);
            if (calls.Count > 0) CommitToolCalls(calls);
            // 流式没拿到内容（部分兼容端点不支持）→ 退回非流式，保证能出话
            if (string.IsNullOrEmpty(text) && calls.Count == 0)
            {
                ResetReasoning();
                return await ChatResponsesAsync(system, messages, ct, true, null, maxOut, tools);
            }
            LastError = null;
            return text;
        }

        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // 非流式：output 里可能有 function_call 条目
        if (root.TryGetProperty("output", out var outs) && outs.ValueKind == JsonValueKind.Array)
        {
            var calls = new List<ApiToolCall>();
            foreach (var item in outs.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("type", out var it) || it.ValueKind != JsonValueKind.String ||
                    it.GetString() != "function_call") continue;
                string callId = item.TryGetProperty("call_id", out var cid) && cid.ValueKind == JsonValueKind.String
                    ? cid.GetString() ?? "" : "";
                string name = item.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                    ? nm.GetString() ?? "" : "";
                string args = item.TryGetProperty("arguments", out var ag) && ag.ValueKind == JsonValueKind.String
                    ? ag.GetString() ?? "" : "{}";
                calls.Add(new ApiToolCall { Id = callId, Name = name, Arguments = args });
            }
            if (calls.Count > 0) CommitToolCalls(calls);
        }

        string got = ExtractResponsesText(root, out string status, out string reason, out string reasoning);
        AddReasoning(reasoning);
        if (got.Length > 0)
        {
            // 即使被判定为 incomplete（例如刚好卡在 token 上限），也先把已经拿到的部分交给用户，别整段丢掉；
            // 同时标记截断，上层会自动接着写（以前这里不标记，回答就断在半截，用户得手动说「继续」）
            if (status == "incomplete") LastTruncated = true;
            LastError = null;
            return got;
        }

        // 这一轮只是「调工具」，本来就没有正文：算成功，别报「响应里没有可用的文本」
        if (ToolCalls.Count > 0)
        {
            LastError = null;
            return "";
        }

        // 完全没拿到文本：如果是 token 用尽，放大上限重试一次
        if (status == "incomplete" && reason.Contains("max_output_tokens", StringComparison.OrdinalIgnoreCase) &&
            maxOut < 32768)
        {
            return await ChatResponsesAsync(system, messages, ct, true, null, Math.Min(32768, maxOut * 2), tools)
                .ConfigureAwait(false);
        }

        LastError = string.IsNullOrEmpty(status)
            ? "响应里没有可用的文本（检查模型名是否支持 Responses API）"
            : $"响应里没有可用的文本（status={status}" + (string.IsNullOrEmpty(reason) ? "" : " · " + reason) + "）";
        return null;
    }

    /// <summary>response.completed / response.incomplete 里是否写着「输出被长度上限截断」。</summary>
    private static bool ResponsesEventTruncated(JsonElement ev)
    {
        if (!ev.TryGetProperty("response", out var r) || r.ValueKind != JsonValueKind.Object) return false;
        if (r.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String &&
            st.GetString() == "incomplete") return true;
        return r.TryGetProperty("incomplete_details", out var det) && det.ValueKind == JsonValueKind.Object &&
               det.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String &&
               (rs.GetString() ?? "").Contains("max_output_tokens", StringComparison.OrdinalIgnoreCase);
    }

/// <summary>宽容解析 Responses API 的返回。</summary>
    private static string ExtractResponsesText(JsonElement root, out string status, out string reason,
                                               out string reasoning)
    {
        status = root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString() ?? "" : "";
        reason = "";
        if (root.TryGetProperty("incomplete_details", out var inc) && inc.ValueKind == JsonValueKind.Object &&
            inc.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String)
            reason = rs.GetString() ?? "";

        var sb = new StringBuilder();
        var rsb = new StringBuilder();
        if (root.TryGetProperty("output", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                string itype = item.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String
                    ? ty.GetString() ?? "" : "";
                bool isReasoning = itype.Contains("reasoning", StringComparison.OrdinalIgnoreCase);
                var target = isReasoning ? rsb : sb;
                if (item.TryGetProperty("content", out var cs) && cs.ValueKind == JsonValueKind.Array)
                    foreach (var c in cs.EnumerateArray())
                        if (c.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                            target.Append(tx.GetString());
                if (isReasoning && item.TryGetProperty("summary", out var sm) && sm.ValueKind == JsonValueKind.Array)
                    foreach (var c in sm.EnumerateArray())
                        if (c.TryGetProperty("text", out var tx2) && tx2.ValueKind == JsonValueKind.String)
                            target.Append(tx2.GetString());
                if (item.TryGetProperty("text", out var itx) && itx.ValueKind == JsonValueKind.String)
                    target.Append(itx.GetString());
            }
        }
        reasoning = rsb.ToString();
        if (root.TryGetProperty("output_text", out var ot) && ot.ValueKind == JsonValueKind.String)
        {
            string s = ot.GetString() ?? "";
            if (s.Length > 0) return s;
        }
        return sb.ToString();
    }

    /// <summary>Responses API 的多模态内容格式：input_text / input_image。</summary>
    private static object BuildResponsesContent(string content)
    {
        var img = ReadImage(content);
        if (img == null) return ImgText(content);
        return new object[]
        {
            new { type = "input_text", text = img.Value.Text },
            new { type = "input_image", image_url = "data:" + img.Value.Mime + ";base64," + img.Value.Base64 },
        };
    }

    /// <summary>Anthropic 消息表：tool_use / tool_result 都是内容块；连续的工具回执合成一条 user 消息， 否则 Claude 会因为「连续两条 user」直接 400。</summary>
    private static List<object> BuildAnthropicMsgs(IReadOnlyList<ApiMsg> messages)
    {
        var list = new List<(string Role, List<object> Blocks)>();
        void Push(string role, List<object> blocks)
        {
            if (list.Count > 0 && list[^1].Role == role && role == "user")
            {
                list[^1].Blocks.AddRange(blocks);
                return;
            }
            list.Add((role, blocks));
        }
        foreach (var m in messages)
        {
            if (m.Role == "tool" && !string.IsNullOrEmpty(m.ToolCallId))
            {
                Push("user", new List<object>
                {
                    new { type = "tool_result", tool_use_id = m.ToolCallId, content = m.Content ?? "" },
                });
                continue;
            }
            if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
            {
                var blocks = new List<object>();
                if (!string.IsNullOrWhiteSpace(m.Content)) blocks.Add(new { type = "text", text = PlainText(m.Content!) });
                foreach (var c in m.ToolCalls)
                    blocks.Add(new { type = "tool_use", id = c.Id, name = c.Name, input = ArgsElement(c.Arguments) });
                list.Add(("assistant", blocks));
                continue;
            }
            // Claude 不接受空的 text 块，空消息整条跳过（不影响 tool_use / tool_result 的配对）
            if (string.IsNullOrWhiteSpace(m.Content)) continue;
            // 多模态：Claude 的图片是独立块（base64 source），不是 image_url
            var img = ReadImage(m.Content!);
            if (img != null)
            {
                var pic = new List<object>
                {
                    new { type = "image", source = new { type = "base64", media_type = img.Value.Mime, data = img.Value.Base64 } },
                };
                if (!string.IsNullOrWhiteSpace(img.Value.Text))
                    pic.Add(new { type = "text", text = img.Value.Text });
                Push(m.Role == "assistant" ? "assistant" : "user", pic);
                continue;
            }
            Push(m.Role == "assistant" ? "assistant" : "user",
                new List<object> { new { type = "text", text = PlainText(m.Content!) } });
        }
        return list.Select(x => (object)new { role = x.Role, content = x.Blocks }).ToList();
    }

    /// <summary>Anthropic 的 tools 项。</summary>
    private static object ToolPayloadAnthropic(ApiTool t) => new
    {
        name = t.Name,
        description = t.Description,
        input_schema = SchemaElement(t.ParametersJson),
    };

    // Anthropic Claude（POST /v1/messages）
    private async Task<string?> ChatAnthropicAsync(string system,
        IReadOnlyList<ApiMsg> messages, CancellationToken ct,
        bool stream, Action<string>? onDelta, int maxOut = 8192, int idleSeconds = 0,
        IReadOnlyList<ApiTool>? tools = null)
    {
        var msgs = BuildAnthropicMsgs(messages);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = Model,
            // 以前写死 8192：上层遇到「预算被思考吃光」把 maxOut 提到 32768 时，这条路径收不到，补救等于没做。
            ["max_tokens"] = maxOut,
            ["system"] = system,
            ["messages"] = msgs,
            ["stream"] = stream,
        };
        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools.Select(ToolPayloadAnthropic).ToArray();
        }
        // 这里不发 thinking：Anthropic 的扩展思考要求多轮历史回传 thinking 签名，
        // 而历史消息在这里都被摊平成纯文本，开了只会 400。设置里的思考强度只作用于 OpenAI 兼容线路。
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("x-api-key", ApiKey.Trim());
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        // 等响应头单独计时：网关排队时可能几十秒不回头，不能让它一直干等，也不能算进正文的看门狗。
        using var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (stream && idleSeconds > 0) headCts.CancelAfter(TimeSpan.FromSeconds(idleSeconds));
        // ResponseHeadersRead：默认的 ResponseContentRead 会等整个流结束才返回，
        // 长回答要憋到最后才一次性显示，也测不出真实的首字延迟。
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headCts.Token)
            .ConfigureAwait(false);
        // 头已经拿到，后面的等待交给读取阶段的看门狗；这里撤掉定时器，否则它会连正在读的流一起掐断。
        if (stream && idleSeconds > 0) headCts.CancelAfter(Timeout.InfiniteTimeSpan);
        if (!resp.IsSuccessStatusCode)
        {
            LastError = HttpError(resp);
            string errBody = "";
            try
            {
                errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (errBody.Length > 200) errBody = errBody[..200];
                LastError += " " + errBody;
            }
            catch { }
            if (tools is { Count: > 0 } && ((int)resp.StatusCode == 400 || (int)resp.StatusCode == 422) &&
                LooksLikeToolsUnsupported(errBody))
            {
                _toolsRejected.Add(ToolKey);
                return await ChatAnthropicAsync(system, messages, ct, stream, onDelta, maxOut, idleSeconds,
                    tools: null).ConfigureAwait(false);
            }
            return null;
        }
        if (!stream)
        {
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                LastError = "网关返回的结构不对（没有 content 数组）";
                return null;
            }
            LastError = null;
            string stop = doc.RootElement.TryGetProperty("stop_reason", out var sr) &&
                          sr.ValueKind == JsonValueKind.String ? sr.GetString() ?? "" : "";
            LastTruncated = false;
            LastFinish = stop switch
            {
                "max_tokens" => "length",
                "tool_use" => "tool_calls",
                "" => null,
                _ => "stop",
            };
            if (stop == "max_tokens") LastTruncated = true;
            // content 是块数组：第一个块不一定是正文，开了思考时排在最前面的是 thinking 块。
            // 以前直接取 content[0].text，遇到 thinking 就抛异常，上层只看到「返回 0 字」。
            var text = new StringBuilder();
            var calls = new List<ApiToolCall>();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                string type = block.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String
                    ? ty.GetString() ?? "" : "";
                if (type == "text")
                {
                    if (block.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                        text.Append(tx.GetString());
                }
                else if (type == "thinking")
                {
                    if (block.TryGetProperty("thinking", out var th) && th.ValueKind == JsonValueKind.String)
                        AddReasoning(th.GetString());
                }
                else if (type == "tool_use")
                {
                    // 原生工具调用：input 是对象，转成 JSON 字符串交回上层
                    string id = block.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String
                        ? idv.GetString() ?? "" : "";
                    string name = block.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                        ? nm.GetString() ?? "" : "";
                    string args = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                    calls.Add(new ApiToolCall { Id = id, Name = name, Arguments = args });
                }
            }
            if (calls.Count > 0) CommitToolCalls(calls);
            if (text.Length == 0 && content.GetArrayLength() == 0) LastError = "响应中没有 content";
            return text.ToString();
        }
        LastError = null;
        LastFinish = null;
        var streamCalls = new List<ApiToolCall>();
        var blockIndex = new Dictionary<int, int>();
        string? res = await ReadSseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            ct, onDelta, ev =>
            {
                string type = ev.TryGetProperty("type", out var tp) && tp.ValueKind == JsonValueKind.String
                    ? tp.GetString() ?? "" : "";
                // 工具调用的开头：content_block_start 带 id / name
                if (type == "content_block_start")
                {
                    if (ev.TryGetProperty("content_block", out var cb) && cb.ValueKind == JsonValueKind.Object &&
                        cb.TryGetProperty("type", out var cbt) && cbt.ValueKind == JsonValueKind.String &&
                        cbt.GetString() == "tool_use")
                    {
                        string id = cb.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String
                            ? idv.GetString() ?? "" : "";
                        string name = cb.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                            ? nm.GetString() ?? "" : "";
                        streamCalls.Add(new ApiToolCall { Id = id, Name = name, Arguments = "" });
                        if (ev.TryGetProperty("index", out var ix) && ix.TryGetInt32(out int iv))
                            blockIndex[iv] = streamCalls.Count - 1;
                    }
                    return null;
                }
                if (!ev.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                    return null;
                // 结束事件只带 stop_reason，正文增量在 content_block_delta 里
                if (delta.TryGetProperty("stop_reason", out var sr2) && sr2.ValueKind == JsonValueKind.String)
                {
                    string st = sr2.GetString() ?? "";
                    LastFinish = st == "max_tokens" ? "length" : (st == "tool_use" ? "tool_calls" : (st.Length > 0 ? "stop" : null));
                    if (st == "max_tokens") LastTruncated = true;
                    if (streamCalls.Count > 0) CommitToolCalls(streamCalls);
                    return null;
                }
                if (type != "content_block_delta") return null;
                string dtype = delta.TryGetProperty("type", out var dt) && dt.ValueKind == JsonValueKind.String
                    ? dt.GetString() ?? "" : "";
                if (dtype == "input_json_delta")
                {
                    // 参数是分段的 JSON 字符串，按块序号拼回去
                    if (ev.TryGetProperty("index", out var dix) && dix.TryGetInt32(out int div) &&
                        blockIndex.TryGetValue(div, out int ci) &&
                        delta.TryGetProperty("partial_json", out var pj) && pj.ValueKind == JsonValueKind.String)
                        streamCalls[ci].Arguments += pj.GetString();
                    return null;
                }
                if (dtype == "thinking_delta" && delta.TryGetProperty("thinking", out var th2))
                {
                    AddReasoning(th2.GetString());
                    return null;
                }
                if (delta.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    return t.GetString() ?? "";
                return null;
            }, idleSeconds);
        // 有的网关不发 message_delta 就断流：这里兜一次，别让已经拼好的工具调用丢掉
        if (streamCalls.Count > 0 && ToolCalls.Count == 0) CommitToolCalls(streamCalls);
        return res;
    }

    // Google Gemini（generateContent / streamGenerateContent）
    private async Task<string?> ChatGeminiAsync(string system,
        IReadOnlyList<ApiMsg> messages, CancellationToken ct,
        bool stream, Action<string>? onDelta, int maxOut = 8192, IReadOnlyList<ApiTool>? tools = null)
    {
        string url = Endpoint.Replace("{model}", Model);
        if (!url.Contains(":generateContent"))
            url = url.TrimEnd('/') + "/v1beta/models/" + Model + ":generateContent";
        if (stream)
            url = url.Replace(":generateContent", ":streamGenerateContent?alt=sse");
        var contents = new List<object>();
        foreach (var m in messages)
        {
            string content = m.Content ?? "";
            // 原生工具回执：Gemini 只认函数名（不认 id），结果是 {result: ...}
            if (m.Role == "tool" && !string.IsNullOrEmpty(m.ToolName))
            {
                contents.Add(new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { functionResponse = new { name = m.ToolName, response = new { result = content } } },
                    },
                });
                continue;
            }
            if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
            {
                var parts = new List<object>();
                if (!string.IsNullOrWhiteSpace(content)) parts.Add(new { text = PlainText(content) });
                foreach (var c in m.ToolCalls)
                    parts.Add(new { functionCall = new { name = c.Name, args = ArgsElement(c.Arguments) } });
                contents.Add(new { role = "model", parts });
                continue;
            }
            if (m.Role == "tool")
            {
                // 文本协议的工具结果（没有函数名）：用 user 文本交回，以前这里直接丢掉
                contents.Add(new
                {
                    role = "user",
                    parts = new object[] { new { text = "[工具执行结果]\n" + content } },
                });
                continue;
            }
            // Gemini 的空 text part 会被判非法：空消息整条跳过
            if (string.IsNullOrWhiteSpace(content)) continue;
            // 多模态：Gemini 用 inline_data（base64 直接内联）
            var img = ReadImage(content);
            if (img != null)
            {
                var parts = new List<object>
                {
                    new { inline_data = new { mime_type = img.Value.Mime, data = img.Value.Base64 } },
                };
                if (!string.IsNullOrWhiteSpace(img.Value.Text)) parts.Add(new { text = img.Value.Text });
                contents.Add(new { role = m.Role == "assistant" ? "model" : "user", parts });
                continue;
            }
            contents.Add(new
            {
                role = m.Role == "assistant" ? "model" : "user",
                parts = new object[] { new { text = PlainText(content) } },
            });
        }
        var payload = new Dictionary<string, object?>
        {
            ["contents"] = contents,
            ["systemInstruction"] = new { parts = new object[] { new { text = system } } },
            ["generationConfig"] = new { maxOutputTokens = maxOut },
        };
        if (tools is { Count: > 0 })
        {
            payload["tools"] = new object[]
            {
                new
                {
                    functionDeclarations = tools.Select(t => new
                    {
                        name = t.Name,
                        description = t.Description,
                        parameters = SchemaElement(t.ParametersJson),
                    }).ToArray(),
                },
            };
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("x-goog-api-key", ApiKey.Trim());
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            LastError = HttpError(resp);
            string errBody = "";
            try
            {
                errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (errBody.Length > 200) errBody = errBody[..200];
                LastError += " " + errBody;
            }
            catch { }
            if (tools is { Count: > 0 } && ((int)resp.StatusCode == 400 || (int)resp.StatusCode == 422) &&
                LooksLikeToolsUnsupported(errBody))
            {
                _toolsRejected.Add(ToolKey);
                return await ChatGeminiAsync(system, messages, ct, stream, onDelta, maxOut, tools: null)
                    .ConfigureAwait(false);
            }
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
            if (cands[0].TryGetProperty("finishReason", out var frg) && frg.ValueKind == JsonValueKind.String &&
                (frg.GetString() ?? "").Contains("MAX_TOKENS", StringComparison.OrdinalIgnoreCase)) LastTruncated = true;
            var sb = new StringBuilder();
            var calls = new List<ApiToolCall>();
            if (cands[0].TryGetProperty("content", out var c0) && c0.ValueKind == JsonValueKind.Object &&
                c0.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                CollectGeminiParts(parts, sb, calls);
            if (calls.Count > 0) CommitToolCalls(calls);
            return sb.ToString();
        }
        LastError = null;
        var streamCalls = new List<ApiToolCall>();
        var res = await ReadSseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            ct, onDelta, ev =>
            {
                if (ev.TryGetProperty("candidates", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0)
                {
                    if (c[0].TryGetProperty("finishReason", out var frs) && frs.ValueKind == JsonValueKind.String &&
                        (frs.GetString() ?? "").Contains("MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
                        LastTruncated = true;
                    if (c[0].TryGetProperty("content", out var cc) && cc.ValueKind == JsonValueKind.Object &&
                        cc.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    {
                        var one = new StringBuilder();
                        CollectGeminiParts(parts, one, streamCalls);
                        return one.ToString();
                    }
                }
                return null;
            });
        if (streamCalls.Count > 0) CommitToolCalls(streamCalls);
        return res;
    }

    /// <summary>Gemini 的 parts：文本进 sb，函数调用进 calls（一个 part 就是一次调用，参数是完整对象）。</summary>
    private static void CollectGeminiParts(JsonElement parts, StringBuilder sb, List<ApiToolCall> calls)
    {
        foreach (var p in parts.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object) continue;
            if (p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            {
                sb.Append(t.GetString());
                continue;
            }
            if (p.TryGetProperty("functionCall", out var fc) && fc.ValueKind == JsonValueKind.Object)
            {
                string name = fc.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                    ? nm.GetString() ?? "" : "";
                string args = fc.TryGetProperty("args", out var ag) ? ag.GetRawText() : "{}";
                calls.Add(new ApiToolCall { Id = "call_" + (calls.Count + 1), Name = name, Arguments = args });
            }
        }
    }

    /// <summary>通用 SSE 读取：逐行解析 data: JSON，用 extract 提取增量文本。</summary>
    private static async Task<string?> ReadSseAsync(Stream stream, CancellationToken ct,
        Action<string>? onDelta, Func<JsonElement, string?> extract, int idleSeconds = 0)
    {
        // idleSeconds > 0 时启用「多久没数据就掐断」：网关真的卡死时不用等满总超时。
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        void Rearm()
        {
            if (idleSeconds <= 0) return;
            try { idle.CancelAfter(TimeSpan.FromSeconds(idleSeconds)); } catch { }
        }
        Rearm();
        var sb = new StringBuilder();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            string? line = await reader.ReadLineAsync(idleSeconds > 0 ? idle.Token : ct).ConfigureAwait(false);
            if (line == null) break;
            Rearm();
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

    /// <summary>把多模态 IMG|… 标记降级为纯文本（图片发不出去时用）。</summary>
    private static string PlainText(string content) => ImgText(content);
}

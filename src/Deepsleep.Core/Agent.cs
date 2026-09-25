﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler;

/// <summary>智能体消息（多轮上下文用）。Role：user / assistant / tool。</summary>
public sealed class AgentMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string Meta { get; set; } = "";
    /// <summary>可选：本消息附带一张本地图片（生成图片工具的结果）。</summary>
    public string? ImagePath { get; set; }
}

/// <summary>
/// AI 助手核心：类似 Codex / Claude Code 的智能体循环。
/// 通过 JSON 工具调用协议驱动，兼容 DeepSeek API 与本地 Ollama。
/// </summary>
public sealed class Agent
{
    public const string ToolCounter = "以理服人";
    public const string ToolRunCommand = "运行命令";
    public const string ToolPython = "运行Python脚本";
    public const string ToolImage = "生成图片";
    public const string ToolVision = "看图";
    public const string ToolSearch = "联网搜索";
    public const string ToolFetch = "抓取网页";
    public const string ToolOpenFile = "打开文件";
    public const string ToolReadFile = "读取文件";
    public const string ToolWriteFile = "写入文件";
    public const string ToolRemember = "记住";
    public const string ToolForget = "删除记忆";

    /// <summary>以理服人回怼引擎系统提示（GLM-4-Flash-250414 生成）。</summary>
    public const string CounterSystemPrompt =
        "你是「以理服人」回怼引擎，目标是让网络键盘侠破防、自取其辱。用户给你一句攻击言论，请生成一条回怼：\n" +
        "铁律：\n" +
        "1. 全文零脏话、零侮辱词（含谐音），违者作废；\n" +
        "2. 绝不附和、绝不认错、绝不当和事佬；\n" +
        "3. 必须引用对方原话里的关键片段（用「」括起来）逐条拆解、逐条打脸，具体指出对方哪句错、哪句站不住；\n" +
        "4. 语气要损、要阴阳怪气、居高临下、带刺但文明——像评论区老阴阳师，不像心理咨询师；\n" +
        "5. 2~4 行短句为主，结尾用句号/问号/感叹号；\n" +
        "6. 禁止书面套话（首先/其次/综上/不难发现/希望）和 AI 腔。\n" +
        "风格对照：\n" +
        "差（太客气像劝导员）：「你说得对，我理解你的心情」「建议你冷静下来好好沟通」\n" +
        "好（损且零脏话）：「『你懂个屁』四个字，把你有理说不出这件事描述得挺准确」「『你也配』？配不配不由你定，你这句除了嗓门大，没有任何信息量」「翻来覆去就这一句，词汇量跟你的人一样贫瘠」「你越急我越觉得好玩，继续，反正浪费的是你的时间」\n" +
        "只输出回怼内容本身，不要解释、不要 JSON。";

    private const int MaxTurns = 500;   // 工具调用无实际次数限制（仅作为防止失控的极高层安全网）
    private const int MaxToolOutput = 4000;
    private const int CommandTimeoutSeconds = 600;

    private readonly ApiClient _api = new();
    private readonly ApiClient _counterLlm = new();
    private readonly OllamaClient _ollama = new();
    private readonly ImageGenClient _image = new();
    private readonly VisionClient _vision = new();
    private readonly Engine _counter;
    private readonly Dictionary<int, List<AgentMessage>> _sessions = new();
    private int _counterSid = 900001;
    private string? _lastImagePath;
    private SemaphoreSlim? _callGate;

    /// <summary>后端：auto（已配置 API 则走 API，否则 Ollama）/ api / llm / local。</summary>
    public string Backend { get; set; } = "auto";

    /// <summary>运行模式：chat（纯聊天，禁用工具）/ work（工作，执行命令需确认）/ boom（爆破，全自动）。</summary>
    public string RunMode { get; set; } = "work";

    /// <summary>是否启用联网搜索工具（界面上的 🌐 开关控制）。</summary>
    public bool WebSearchEnabled { get; set; } = true;

    /// <summary>是否启用真·流式输出（逐 token 实时显示）。</summary>
    public bool Streaming { get; set; } = true;

    /// <summary>极速模式：用更短的提示词与更小的输出预算换取响应速度。</summary>
    public bool FastMode { get; set; } = false;

    /// <summary>同一时刻允许的最大并发 LLM 调用数（0=不限制）。集群并行时用于避免触发 API 429 限流。</summary>
    public int MaxConcurrentCalls { get; set; }

    /// <summary>追加到系统提示词末尾的附加说明（如集群的 boom 权限与工作区约定）。</summary>
    public string ExtraPrompt { get; set; } = "";

    /// <summary>已安装技能（用户提到技能名时，把对应 SKILL.md 说明注入系统提示词）。</summary>
    public List<SkillInfo> Skills { get; set; } = new();

    /// <summary>长期记忆（跨会话）：内容会注入系统提示词，模型可用「记住/删除记忆」工具维护。</summary>
    public MemoryStore Memory { get; set; } = new("");

    /// <summary>自动记忆：从用户话里提取重要事实/偏好自动写入长期记忆（集群设为 false 避免污染）。</summary>
    public bool AutoRemember { get; set; } = true;

    /// <summary>主模型是否为多模态：开启后图片直接发送给主模型，不再调用「看图」工具。</summary>
    public bool MultimodalMain { get; set; } = false;

    /// <summary>work 模式执行命令/脚本前触发确认（参数：会话 id、命令详情；返回 true 才执行）。</summary>
    public event Func<int, string, Task<bool>>? ConfirmRequested;

    /// <summary>产生新消息时触发（UI 订阅，追加到对应会话的对话列表）。参数：会话 id、消息。</summary>
    public event Action<int, AgentMessage>? MessageAdded;

    /// <summary>技能被自动匹配并启用时触发（参数：会话 id、技能名），用于 UI 显示技能调用链。</summary>
    public event Action<int, string>? SkillUsed;

    /// <summary>流式增量内容（参数：会话 id、增量文本），用于真·流式输出。</summary>
    public event Action<int, string>? DeltaAdded;

    /// <summary>工具开始执行时触发（参数：会话 id、工具名），UI 显示「正在…」状态。</summary>
    public event Action<int, string>? ToolStarted;

    private int _currentSid;

    public Agent(Engine counter) => _counter = counter;

    public void ApplyConfig(AppConfig cfg)
    {
        _api.Url = cfg.ApiUrl;
        _api.Model = cfg.ApiModel;
        _api.ApiKey = cfg.ApiKey;
        _api.Format = cfg.ApiFormat;
        _api.Thinking = cfg.ApiThinking;
        _api.ReasoningEffort = cfg.ReasoningEffort;
        _counterLlm.Url = cfg.CounterApiUrl;
        _counterLlm.ApiKey = cfg.CounterApiKey;
        _counterLlm.Model = cfg.CounterModel;
        _ollama.BaseUrl = cfg.OllamaUrl;
        _ollama.Model = cfg.OllamaModel;
        _image.Url = cfg.ImageApiUrl;
        _image.ApiKey = cfg.ImageApiKey;
        _image.Model = cfg.ImageModel;
        _image.SaveDir = string.IsNullOrWhiteSpace(DataDir)
            ? ""
            : System.IO.Path.Combine(DataDir, "images");
        _vision.Url = cfg.VisionApiUrl;
        _vision.ApiKey = cfg.VisionApiKey;
        _vision.Model = cfg.VisionModel;
    }

    /// <summary>应用数据目录（图片保存到其下 images 子目录）。</summary>
    public string DataDir { get; set; } = "";

    /// <summary>诊断日志（写入 data\agent.log），用于排查流式/后端问题。</summary>
    private void Log(string msg)
    {
        try
        {
            if (string.IsNullOrEmpty(DataDir)) return;
            File.AppendAllText(Path.Combine(DataDir, "agent.log"),
                $"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        }
        catch { /* 日志失败不影响运行 */ }
    }

    public bool ApiConfigured => _api.Configured;

    public void NewSession(int sid) => _sessions[sid] = new List<AgentMessage>();

    /// <summary>从磁盘恢复会话历史（跨重启断点恢复用），自动限长。</summary>
    public void RestoreSession(int sid, IEnumerable<AgentMessage> messages)
    {
        var list = messages.Where(m => m != null && !string.IsNullOrWhiteSpace(m.Content)).ToList();
        if (list.Count > 30) list.RemoveRange(0, list.Count - 30);
        _sessions[sid] = list;
    }

    /// <summary>向会话追加一条历史（断点标记用）。</summary>
    public void AppendHistory(int sid, AgentMessage m)
    {
        if (!_sessions.TryGetValue(sid, out var history)) return;
        history.Add(m);
        TrimHistory(history);
    }

    /// <summary>导出全部会话快照（供持久化）。</summary>
    public IEnumerable<KeyValuePair<int, List<AgentMessage>>> SnapshotSessions()
    {
        foreach (var kv in _sessions)
            yield return new KeyValuePair<int, List<AgentMessage>>(kv.Key, kv.Value.ToList());
    }

    public void ResetSession(int sid)
    {
        if (_sessions.TryGetValue(sid, out var history)) history.Clear();
    }

    /// <summary>截断到最后一个用户消息之后：清掉被中止轮的残留（assistant/tool 尾巴），保留历史对话。</summary>
    public void TruncateToLastUser(int sid)
    {
        if (!_sessions.TryGetValue(sid, out var history) || history.Count == 0) return;
        int lastUser = history.FindLastIndex(m => m.Role == "user");
        if (lastUser >= 0) history.RemoveRange(lastUser + 1, history.Count - lastUser - 1);
    }

    /// <summary>从会话历史中删除一条指定消息（界面删除消息时同步上下文）。</summary>
    public void RemoveMessage(int sid, string role, string content)
    {
        if (!_sessions.TryGetValue(sid, out var history)) return;
        int idx = history.FindIndex(m => m.Role == role && m.Content == content);
        if (idx >= 0) history.RemoveAt(idx);
    }

    /// <summary>向会话注入一条用户侧上下文（上传文件等，不重复显示）。</summary>
    public void InjectContext(int sid, string text)
    {
        if (!_sessions.TryGetValue(sid, out var history)) return;
        history.Add(new AgentMessage { Role = "user", Content = text });
    }

    /// <summary>一句话指挥：让指挥官模型把目标拆解成子任务，返回 JSON 计划文本。</summary>
    public async Task<string> PlanAsync(string instruction, string? feedback = null)
    {
        const string system =
            "你是 Agent 集群的指挥官。用户用一句话给出目标，你需要把目标拆解成 3~10 个可并行执行的子任务。" +
            "必须拆出至少 3 个子任务，严禁只拆 1 个、严禁拒绝拆解。只输出严格 JSON 数组，不要任何其他文字或代码块：" +
            "[{\"角色\":\"名称\",\"任务\":\"具体任务\"},...]。子任务要具体、可执行，覆盖实现、测试、素材等必要环节。";
        string content = instruction;
        if (!string.IsNullOrWhiteSpace(feedback))
            content += "\n\n" + feedback;
        var msgs = new List<(string Role, string Content)> { ("user", content) };
        try
        {
            if (Backend == "api" || (Backend == "auto" && _api.Configured))
                return await _api.ChatAsync(system, msgs) ?? "";
            return await _ollama.ChatAsync(system, msgs) ?? "";
        }
        catch { return ""; }
    }

    /// <summary>最近一次运行是否被用户停止（用于界面切换"恢复"状态）。</summary>
    public bool LastRunStopped { get; private set; }

    /// <summary>跑一轮智能体：多轮工具调用，直到模型给出最终回答（支持中途停止/断点恢复）。</summary>
    public async Task<string> RunAsync(int sid, string userText, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sid, out var history))
        {
            history = new List<AgentMessage>();
            _sessions[sid] = history;
        }
        history.Add(new AgentMessage { Role = "user", Content = userText });
        MessageAdded?.Invoke(sid, history[^1]);
        if (AutoRemember) AutoRememberFromUser(userText);
        string result = await RunLoopAsync(sid, history, ct);
        CompressAndRemember(userText, result);
        return result;
    }

    /// <summary>
    /// 自动压缩对话存入长期记忆：每轮「问→答」结束后，把双方内容压成一条摘要写入记忆。
    /// 全部摘要都保留；总字数超 1000 时自动把最旧的摘要再压短并入「更早纪要」，不删除内容。
    /// 失败/停止/报错轮不存。
    /// </summary>
    private void CompressAndRemember(string userText, string? finalReply)
    {
        if (!AutoRemember || Memory == null || string.IsNullOrWhiteSpace(finalReply)) return;
        if (finalReply.Contains("已停止", StringComparison.Ordinal) ||
            finalReply.StartsWith("LLM 后端不可用", StringComparison.Ordinal) ||
            finalReply.Length < 4)
            return;
        string q = Compact(userText, 80);
        string a = Compact(finalReply, 130);
        if (string.IsNullOrWhiteSpace(q) && string.IsNullOrWhiteSpace(a)) return;
        string entry = $"对话摘要（{DateTime.Now:MM-dd HH:mm}）：问「{q}」答「{a}」";
        if (Memory.Entries.Any(e => e.Contains(q, StringComparison.Ordinal) &&
                                    e.Contains(a, StringComparison.Ordinal)))
            return;   // 同样的问答已存过
        Memory.Add(entry);
        Memory.CompressIfOver(1000);   // 超出 1000 字自动压缩旧摘要，不删除
        Log($"对话已压缩入记忆：{entry}");
    }

    /// <summary>把长文本压缩成单行（去 Markdown 标记、折叠空白、限长）。</summary>
    private static string Compact(string s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = MarkdownRenderer.ToPlainText(s);
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    /// <summary>
    /// 强制记忆：自动从用户话里提取「我叫/我喜欢/我不喜欢/我习惯/记住/以后都」等关键事实，
    /// 写入长期记忆（去重、限量），让 AI 跨会话记住用户，不依赖模型自觉。
    /// </summary>
    private void AutoRememberFromUser(string userText)
    {
        if (Memory == null || string.IsNullOrWhiteSpace(userText) || userText.Length > 500) return;
        var facts = new List<string>();
        void Add(string label, Match m, int take = 60)
        {
            string v = m.Groups[1].Value.Trim().Trim('，', '。', ',', '.', '！', '!', '？', '?', '：', ':');
            if (v.Length >= 2 && v.Length <= take)
            {
                string f = $"{label}{v}";
                if (!facts.Contains(f)) facts.Add(f);
            }
        }
        foreach (Match m in Regex.Matches(userText, @"记住[:：]?\s*(.{2,120})"))
            Add("记住：", m, 120);
        foreach (Match m in Regex.Matches(userText, @"(?:我叫|我的名字叫|名字是)\s*([\u4e00-\u9fffA-Za-z0-9_]{2,20})"))
            Add("用户名字：", m, 20);
        foreach (Match m in Regex.Matches(userText, @"我是([\u4e00-\u9fffA-Za-z0-9_]{2,20})(?:的)?(?:人|师|员|生|主|作者|老板|经理|总监|工程师|up主|UP主)"))
            Add("用户身份：", m, 20);
        foreach (Match m in Regex.Matches(userText, @"我(?:比较|最)?喜欢(.{2,60}?)(?:[，。！!？?；;]|$)"))
            Add("用户喜欢：", m, 60);
        foreach (Match m in Regex.Matches(userText, @"我(?:比较|最)?不喜欢(.{2,60}?)(?:[，。！!？?；;]|$)"))
            Add("用户不喜欢：", m, 60);
        foreach (Match m in Regex.Matches(userText, @"我习惯(.{2,60}?)(?:[，。！!？?；;]|$)"))
            Add("用户习惯：", m, 60);
        foreach (Match m in Regex.Matches(userText, @"(?:以后|每次|请)(?:都|要|尽量|记得|别|不要)?(.{2,60}?)(?:[。！!？?]|$)"))
        {
            string v = m.Groups[1].Value.Trim().Trim('，', '。', ',', '.', '！', '!', '？', '?');
            if (v.Length >= 2 && v.Length <= 60 && !v.StartsWith("帮我", StringComparison.Ordinal) && !v.StartsWith("不要", StringComparison.Ordinal))
                facts.Add("用户要求：" + v);
        }
        if (facts.Count == 0) return;
        int added = 0;
        foreach (string f in facts)
        {
            if (added >= 3) break;
            if (Memory.Entries.Any(e => e.Contains(f, StringComparison.Ordinal))) continue;
            Memory.Add(f);
            added++;
            Log($"自动记忆：{f}");
        }
    }

    /// <summary>从断点恢复：继续已有会话的循环（不重复添加用户消息）。</summary>
    public async Task<string> ResumeAsync(int sid, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sid, out var history))
            return "没有可恢复的会话。";
        return await RunLoopAsync(sid, history, ct);
    }

    /// <summary>重新生成：截断到最后一个用户消息，重跑该轮。</summary>
    public async Task<string> RerunLastAsync(int sid, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sid, out var history) || history.Count == 0)
            return "没有可重新生成的内容。";
        int lastUser = history.FindLastIndex(m => m.Role == "user");
        if (lastUser < 0) return "没有可重新生成的内容。";
        history.RemoveRange(lastUser + 1, history.Count - lastUser - 1);
        return await RunLoopAsync(sid, history, ct);
    }

    private async Task<string> RunLoopAsync(int sid, List<AgentMessage> history, CancellationToken ct)
    {
        _currentSid = sid;
        LastRunStopped = false;
        var injectedSkills = new HashSet<string>();
        int formatFixes = 0;
        bool searched = false;
        bool userWantsSearch = history.Any(m => m.Role == "user" && IsSearchIntent(m.Content));
        for (int turn = 0; turn < MaxTurns; turn++)
        {
            if (ct.IsCancellationRequested)
            {
                LastRunStopped = true;
                return "（已停止）";
            }

            // 本地自训练模式（非 chat）：用启发式识别工具指令并直接执行，让模型也能真正干活
            if (Backend == "local" && RunMode != "chat" && history.Count > 0 && history[^1].Role == "user" &&
                TryLocalToolCall(history[^1].Content, out string ltool, out JsonElement largs))
            {
                ToolStarted?.Invoke(sid, ltool);
                string lresult = await ExecuteToolAsync(ltool, largs, sid, ct);
                if (ct.IsCancellationRequested)
                {
                    LastRunStopped = true;
                    return "（已停止）";
                }
                if (ltool == ToolSearch) searched = true;
                history.Add(new AgentMessage { Role = "tool", Content = lresult, Meta = ltool });
                TrimHistory(history);
                MessageAdded?.Invoke(sid, history[^1]);
                string final = LocalToolSummary(ltool, lresult);
                history.Add(new AgentMessage { Role = "assistant", Content = final });
                MessageAdded?.Invoke(sid, history[^1]);
                return final;
            }

            // 真·流式：逐段接收增量；工具调用/JSON 前缀不流式显示
            var streamBuffer = new StringBuilder();
            bool streamed = false;
            bool suppressToolStream = false;
            Action<string>? sink = null;
            if (Streaming)
            {
                sink = d =>
                {
                    streamBuffer.Append(d);
                    if (suppressToolStream) return;   // 已判定为工具调用，后续全部不显示
                    string t = streamBuffer.ToString();
                    // 疑似工具调用（出现 "tool" 键）→ 停止流式显示，交给工具提示
                    if (t.Contains("\"tool\"", StringComparison.Ordinal) &&
                        (t.Contains('{') || t.Contains('[')))
                    {
                        suppressToolStream = true;
                        return;
                    }
                    string ts = t.TrimStart();
                    if (ts.StartsWith('{') || ts.StartsWith('[')) return;   // JSON 前缀不显示
                    streamed = true;
                    DeltaAdded?.Invoke(sid, d);
                };
            }
            // 工具决策阶段：软件先发请求，模型这一轮只准输出 JSON（或用「完成」给最终回答），
            // 不流式、不进气泡；软件解析 → 执行工具 → 结果塞回上下文 → 让模型继续决策。
            string? reply = RunMode != "chat"
                ? await CallLlmForDecisionAsync(history, ct, injectedSkills)
                : await CallLlmAsync(history, ct, sink, injectedSkills);
            Log($"RunLoop 回复长度={reply?.Length ?? 0} 已流式显示={streamed}");
            if (ct.IsCancellationRequested)
            {
                LastRunStopped = true;
                return "（已停止）";
            }
            if (string.IsNullOrWhiteSpace(reply))
            {
                var errs = new List<string>();
                if (!string.IsNullOrWhiteSpace(_api.LastError)) errs.Add($"API：{_api.LastError}（地址 {_api.Endpoint} · 模型 {_api.Model}）");
                if (!string.IsNullOrWhiteSpace(_ollama.LastError)) errs.Add($"Ollama：{_ollama.LastError}");
                string detail = errs.Count > 0 ? "（" + string.Join("；", errs) + "）" : "";
                bool rateLimited = !string.IsNullOrWhiteSpace(_api.LastError) && IsRateLimit(_api.LastError);
                string advice = rateLimited
                    ? "API 限流（模型访问量过大），已自动重试仍失败：可稍后重试，或把 AI 助手模式切到「本地（自训练）」/「大模型 1.5B」走本地。"
                    : "请在 ⚙ 设置检查 API 地址/模型名/API Key：OpenAI 兼容地址一般要写到 /chat/completions（例如 https://api.deepseek.com/chat/completions），模型名要和账号可用模型一致。";
                string msg = $"LLM 后端不可用{detail}。{advice}";
                history.Add(new AgentMessage { Role = "assistant", Content = msg });
                MessageAdded?.Invoke(sid, history[^1]);
                return msg;
            }

            string tool = "";
            JsonElement args = default;
            bool isToolCall = RunMode != "chat" && TryParseToolCall(reply, out tool, out args);
            // 决策阶段判定「不需要工具 / 已完成」→ 进入【生成最终回复】阶段：
            // 工具结果已经在上下文里，这一轮让模型用自然语言回答（流式），决策用的 JSON 不进历史也不进气泡。
            bool decisionDone = RunMode != "chat" &&
                ((isToolCall && IsFinalTool(tool)) || IsNoToolDecision(reply));
            if (decisionDone)
            {
                string keep = ExtractFinalText(reply, args);      // 模型若顺手写了答案，作为兜底
                if (LooksLikeJson(keep)) keep = "";
                isToolCall = false;
                tool = "";
                string? final = await CallLlmFinalAsync(history, ct, sink, injectedSkills);
                if (string.IsNullOrWhiteSpace(final)) final = keep;
                if (string.IsNullOrWhiteSpace(final))
                {
                    var errs2 = new List<string>();
                    if (!string.IsNullOrWhiteSpace(_api.LastError))
                        errs2.Add($"API：{_api.LastError}（地址 {_api.Endpoint} · 模型 {_api.Model}）");
                    if (!string.IsNullOrWhiteSpace(_ollama.LastError)) errs2.Add($"Ollama：{_ollama.LastError}");
                    string msg2 = "生成最终回复失败" +
                        (errs2.Count > 0 ? "（" + string.Join("；", errs2) + "）" : "") +
                        "。请在 ⚙ 设置检查模型配置。";
                    history.Add(new AgentMessage { Role = "assistant", Content = msg2 });
                    MessageAdded?.Invoke(sid, history[^1]);
                    return msg2;
                }
                reply = final;
            }

            bool malformedCall = !isToolCall && !decisionDone && reply.Contains("\"tool\"", StringComparison.Ordinal);
            // 历史里保留模型原始输出（上下文需要），界面只显示 Meta 提示，避免暴露 {JSON}
            var assistantMsg = new AgentMessage
            {
                Role = "assistant",
                Content = reply,
                Meta = isToolCall ? $"调用了工具「{tool}」"
                     : malformedCall ? "工具调用格式修正中…" : "",
            };
            history.Add(assistantMsg);
            if (!streamed) MessageAdded?.Invoke(sid, assistantMsg);

            // 形似工具调用但解析失败 → 注入格式修正并重试，保证"调用了就一定会执行"
            if (malformedCall)
            {
                var corr = new AgentMessage
                {
                    Role = "tool",
                    Meta = "格式修正",
                    Content = "你的工具调用格式错误：只能输出合法 JSON。调用工具写 {\"tool_call\":{\"name\":\"工具名\",\"arguments\":{...}}}；不需要工具写 {\"tool_call\":null}。字符串内换行写 \\n，双引号写 \\\"，不要解释。",
                };
                history.Add(corr);
                MessageAdded?.Invoke(sid, corr);
                continue;
            }

            if (!isToolCall)
            {
                // 最终回答必须是纯文本：是 JSON/结构化片段则要求重写成文字
                if (LooksLikeJson(reply) && formatFixes < 3)
                {
                    formatFixes++;
                    var corr = new AgentMessage
                    {
                        Role = "tool",
                        Meta = "格式修正",
                        Content = "你的回答是 JSON/结构化片段，但用户需要纯文本中文回答。请用普通文字重写（可以分点，但不要输出 JSON 或代码块）。",
                    };
                    history.Add(corr);
                    MessageAdded?.Invoke(sid, corr);
                    continue;
                }
                // 最终回答里含代码块 → 自动写入文件（保证 LLM 给的代码一定落盘）
                string? saved = TrySaveCodeBlock(sid, reply, history);
                if (saved != null) return saved;
                // 模型说"不会/做不到"且存在相关技能 → 注入技能说明重试
                string? boost = SuggestSkill(reply, history, injectedSkills) ??
                                SuggestUnusedSkill(reply, history, injectedSkills);
                if (boost != null)
                {
                    var corr = new AgentMessage { Role = "tool", Meta = "技能提示", Content = boost };
                    history.Add(corr);
                    TrimHistory(history);
                    MessageAdded?.Invoke(sid, corr);
                    continue;
                }
                return reply;
            }

            // 搜索意图拦截：用户要搜索时，第一个工具必须是「联网搜索」，
            // 否则直接把模型的无关工具调用纠正回来（不执行）
            if (userWantsSearch && !searched && tool != ToolSearch)
            {
                var corr = new AgentMessage
                {
                    Role = "tool",
                    Meta = "工具纠正",
                    Content = $"⚠️ 用户要求的是搜索/查资料，必须用「联网搜索」工具，而不是「{tool}」。请立即调用 {{\"tool\":\"联网搜索\",\"args\":{{\"关键词\":\"用户要查的内容\"}}}}；如果联网搜索提示已关闭，请提醒用户打开 🌐 开关。",
                };
                history.Add(corr);
                MessageAdded?.Invoke(sid, corr);
                continue;
            }


            _lastImagePath = null;
            ToolStarted?.Invoke(sid, tool);
            string result;
            try
            {
                result = await ExecuteToolAsync(tool, args, sid, ct);
            }
            catch (OperationCanceledException)
            {
                // 被终止：把"未完成"记进上下文，恢复时可从断点继续
                history.Add(new AgentMessage { Role = "tool", Meta = tool, Content = "（执行被用户停止，未完成，可在恢复后继续）" });
                TrimHistory(history);
                MessageAdded?.Invoke(sid, history[^1]);
                LastRunStopped = true;
                return "（已停止）";
            }
            if (tool == ToolSearch) searched = true;
            // 停止后立刻结束本轮，不再继续调用其他工具
            if (ct.IsCancellationRequested)
            {
                LastRunStopped = true;
                return "（已停止）";
            }
            history.Add(new AgentMessage
            {
                Role = "tool",
                Content = result,
                Meta = tool,
                ImagePath = tool == ToolImage ? _lastImagePath : null,
            });
            TrimHistory(history);
            MessageAdded?.Invoke(sid, history[^1]);
            continue;
        }

        string limit = $"本轮工具调用已达安全上限（{MaxTurns} 次），已自动结束。任务没做完的话，直接继续发消息即可，我会接着处理。";
        history.Add(new AgentMessage { Role = "assistant", Content = limit });
        MessageAdded?.Invoke(sid, history[^1]);
        return limit;
    }

    /// <summary>控制上下文长度：只保留最近 30 条消息，避免长任务把 token 打爆（更快更省）。</summary>
    private static void TrimHistory(List<AgentMessage> history)
    {
        if (history.Count <= 30) return;
        history.RemoveRange(0, history.Count - 30);
    }

    // ------------------------------------------------------------------
    // LLM 调用
    // ------------------------------------------------------------------

    private const string SystemPrompt =
        "你是一个运行在 Windows 电脑上的 AI 助手，能力类似 Claude Code / Codex。\n" +
        "你可以调用工具完成实际任务。当你需要工具时，**只输出一行 JSON**，不要输出任何解释，格式：\n" +
        "{\"tool\":\"工具名\",\"args\":{...}}\n\n" +
        "可用工具：\n" +
        "- 运行命令：参数 {\"命令\":\"要执行的 PowerShell 命令\"}。在用户电脑上执行命令并返回输出（最长 10 分钟）。\n" +
        "- 运行Python脚本：参数 {\"代码\":\"Python 3 源码\",\"工作目录\":\"可选\"}。自动选择 Python 3 解释器，写入临时脚本并运行，返回输出与报错。\n" +
        "- 生成图片：参数 {\"提示词\":\"详细的画面描述\",\"尺寸\":\"可选 1024x1024 / 768x1344 / 1344x768\"}。调用 CogView-3-Flash 生成图片并保存到本地，返回本地图片路径。用户要求画图/生成图片/配图时使用。\n" +
        "- 看图：参数 {\"图片路径\":\"本地图片绝对路径\",\"问题\":\"可选，要问的问题\"}。调用 GLM-4.6V-Flash 多模态模型识别/分析图片（描述内容、读图、检查截图、审查生成的图片等）。\n" +
        "- 抓取网页：参数 {\"网址\":\"https://...\"}。用内置爬虫下载网页并提取正文文本（自动去标签、限长），用于阅读搜索结果指向的文章、新闻、价格页等。\n" +
        "- 打开文件：参数 {\"路径\":\"绝对路径\"}。用系统默认程序打开本地文件（文档/图片/网页/媒体等）；" +
        "可执行文件/脚本也能打开（等于把它运行起来），work 模式下系统会先找用户确认，别自作主张反复调用。\n" +
        "- 读取文件：参数 {\"路径\":\"绝对路径\"}。读取文本文件内容。\n" +
        "- 写入文件：参数 {\"路径\":\"绝对路径\",\"内容\":\"要写入的内容\"}。写入或覆盖文本文件。\n" +
        "- 记住：参数 {\"内容\":\"要记住的事实或用户偏好\"}。把重要信息写入长期记忆（跨会话有效），之后任何对话都能用到。\n" +
        "- 删除记忆：参数 {\"序号\":1}。删除系统提示词【长期记忆】里对应序号的一条记忆。\n\n" +
        "规则：\n" +
        "1. 需要工具时只输出那一行 JSON；收到工具结果后继续思考，直到能给出最终回答。\n" +
        "2. 最终回答用普通中文文本，简洁、直接、专业，不要输出 JSON。\n" +
        "3. 运行命令要安全、可逆；写文件前先说明要写什么；路径必须使用绝对路径。\n" +
        "5. 遇到现有工具做不了、或做起来很麻烦的任务（批量处理、解析、转换、抓取、计算、自动化、文件操作等），" +
        "优先用「运行Python脚本」写 Python 3 脚本解决：根据返回的报错修改代码重跑，直到成功。" +
        "脚本会自动选择本机 Python 3（注意系统默认 python 可能是 2.7，不要依赖它）。\n" +
        "6. 工具调用 JSON 必须是合法 JSON：字符串内换行写成 \\n，双引号写成 \\\"（Python 代码里尽量用单引号），花括号不需要转义；不要用 Markdown 代码块包裹 JSON。\n" +
        "7. 严禁声称已执行：在真实调用工具并收到工具结果之前，禁止说「已运行」「已完成」之类的话；每一步执行都必须先调用工具，工具结果会返回给你。\n" +
        "8. 写代码 / 做网页 / 做项目时需要图片素材（图标、Logo、横幅、占位图、插图、配图等）时，用「生成图片」工具生成真实图片并引用本地路径，不要跳过、不要用手写 SVG 或纯色占位代替。\n" +
        "9. 工具执行失败、超时或被停止时：如实告知用户发生了什么并询问下一步；严禁调用无关工具、严禁假装任务已完成、严禁用「好的，我明白了」之类的通用话术敷衍。\n" +
        "10. 用户要求搜索、查价格、查资料、找最新信息时，必须用「联网搜索」工具；搜到链接后如需原文，用「抓取网页」读取；严禁用 curl / wget / Invoke-WebRequest 等命令行代替（会触发拦截并导致失败）。\n" +
        "11. 工具选择指南：搜索/查价格/查资料/找最新信息→「联网搜索」；阅读搜索结果的具体页面→「抓取网页」；执行命令/系统操作→「运行命令」；写代码跑脚本→「运行Python脚本」或「写入文件」；生成图片→「生成图片」；看图片→「看图」；读写文件→「读取文件/写入文件」；用默认程序打开文件→「打开文件」。\n" +
        "12. 最终回答必须是纯文本中文，禁止输出任何 JSON、代码块或结构化片段。\n" +
        "13. 用户提到重要事实、偏好、要求（比如称呼、习惯、项目约定、不想要什么）时，用「记住」工具保存；保存后告知用户已记住。\n" +
        "14. 当【已安装技能】里出现与你任务匹配的技能时，必须真正执行技能：按说明调用「运行Python脚本」「写入文件」「运行命令」「生成图片」等工具，把产物（PPT/文档/网页/脚本等）真实生成到磁盘，并在最终回复里给出完整本地路径；严禁只讲解步骤、只描述怎么做、或假装已完成。";

    /// <summary>
    /// 工具决策阶段的协议：软件先发这条指令，模型这一轮**只准输出 JSON**，
    /// 正文放到「完成」的 回答 字段里；由软件解析、执行工具、把结果塞回上下文，再让模型继续干活。
    /// </summary>
    private const string ToolDecisionPrompt =
        "\n\n【工具决策阶段 · 本轮只准输出 JSON】\n" +
        "整体流程：用户输入 → 你分析意图 → 需要工具就输出 tool_call → 软件执行工具 → 结果回传给你 → 你继续决策，直到不再需要工具。\n" +
        "这一轮软件只要你的工具决策，不要正文。你必须只输出一个 JSON 对象：不要 Markdown 代码块、不要解释、不要前后缀文字。\n" +
        "合法输出只有两种：\n" +
        "① 需要调用工具：{\"tool_call\":{\"name\":\"工具名\",\"arguments\":{...}}}\n" +
        "② 不需要任何工具 / 已经拿到足够结果：{\"tool_call\":null}\n" +
        "要点：\n" +
        "· 需要动手的任务（读写文件、跑命令、跑脚本、搜索、抓网页、画图、看图、装技能）必须先走 ①，不许直接说「已完成」；\n" +
        "· 代码、命令、文件内容都放进 arguments 字段，字符串内换行写 \\n、双引号写 \\\"；\n" +
        "· 这一轮的输出不会显示给用户：软件执行完会把结果塞回上下文，最后再单独让你用自然语言写最终回复；\n" +
        "· 严禁在没收到工具结果前宣称「已运行 / 已完成」。";

    /// <summary>阶段三协议：工具结果已回传，这一轮只负责用自然语言写最终回复。</summary>
    private const string FinalAnswerPrompt =
        "\n\n【最终回复阶段】工具执行结果已经在上面的对话里了，这一轮请直接用自然语言回答用户：\n" +
        "不要输出 JSON、不要调用工具、不要用代码块包住整段回答；简洁、专业、说人话。\n" +
        "如果工具失败了，如实说明失败原因与下一步建议，不要假装成功。";

    private const string ChatSystemPrompt =
        "你是一个运行在 Windows 电脑上的 AI 助手（聊天模式）。\n" +
        "当前模式禁用所有工具：不能执行命令、不能读写文件。\n" +
        "请直接以中文回答用户的问题，简洁、清楚、友好，不要输出 JSON 或提及工具。";

    private string SystemPromptForMode()
    {
        if (FastMode && RunMode != "chat")
        {
            return
                "你是运行在 Windows 上的 AI 助手（类似 Claude Code/Codex），能用工具干活。\n" +
                "工具：运行命令/运行Python脚本/生成图片/看图/读取文件/写入文件/打开文件/联网搜索/抓取网页/记住(保存长期记忆)/删除记忆。\n" +
                "工具调用只输出一行 JSON：{\"tool_call\":{\"name\":\"工具名\",\"arguments\":{...}}}；不需要工具时输出 {\"tool_call\":null}。\n" +
                "这一轮只准输出 JSON 本身，不要写正文；工具结果由软件回传，最后再单独让你写最终回复。\n" +
                "选工具：搜索/查价格/查资料→联网搜索（禁止 curl 等命令代替）；读搜索结果原文→抓取网页；写代码→Python/写入文件；画图→生成图片；看图→看图。\n" +
                "失败/超时/停止时如实说明，禁止假装完成、乱调无关工具或用通用话术敷衍。\n" +
                "用户提供重要事实/偏好时用「记住」保存，用户要求删除某条记忆时用「删除记忆」。";
        }
        string prompt = RunMode switch
        {
            "chat" => ChatSystemPrompt,
            "boom" => SystemPrompt + "\n\n当前是 boom（爆破）模式：你可以直接执行任何命令和文件操作，无需请求用户确认。",
            _ => SystemPrompt + "\n\n当前是 work（工作）模式：执行「运行命令」前系统会自动请求用户确认，确认结果会以工具结果返回给你；其余工具可直接使用。",
        };
        if (RunMode != "chat")
            prompt += "\n- 联网搜索：参数 {\"关键词\":\"搜索词\"}。由内置爬虫脚本直接抓取搜索结果页（Bing → 搜狗 → 360 → DuckDuckGo 依次尝试），返回标题/链接/摘要，无需 API Key。用户要求搜索、查价格、查资料、找最新信息时优先使用；若工具提示已关闭，请提醒用户打开 🌐 开关，不要用命令行代替。";
            prompt += "\n- 抓取网页：参数 {\"网址\":\"https://...\"}。用内置爬虫抓取网页正文（自动去标签、限长 8000 字）。搜索得到链接后需要看原文时使用；若网页抓不到正文，如实说明，不要假装成功。";
            prompt += "\n- 打开文件：参数 {\"路径\":\"绝对路径\"}。用系统默认程序打开本地文件；" +
                      "可执行文件/脚本也允许打开（打开就是运行它，work 模式下会先请用户确认）。";
        if (!string.IsNullOrWhiteSpace(ExtraPrompt))
            prompt += "\n\n" + ExtraPrompt;
        if (MultimodalMain)
            prompt += "\n\n注意：主模型是多模态模型，用户上传的图片会作为多模态输入直接发给你。看到图片直接描述/分析，严禁调用「看图」工具。";
        string mem = Memory?.AllText() ?? "";
        if (!string.IsNullOrWhiteSpace(mem))
            prompt += "\n\n【长期记忆】以下是你跨会话保存的关于用户/任务的事实与偏好（序号即「删除记忆」工具的序号）：\n" +
                      mem +
                      "\n\n规则：回答与这些记忆相关时直接使用；用户提供了新的重要事实/偏好时调用「记住」保存；用户说某条不再需要时调用「删除记忆」。";
        return prompt;
    }

    /// <summary>
    /// 阶段一：工具决策。把模型这一轮的输出当控制通道——只收 JSON，不流式、不进气泡。
    /// </summary>
    private async Task<string?> CallLlmForDecisionAsync(List<AgentMessage> history, CancellationToken ct,
                                                        HashSet<string>? injectedSkills)
    {
        if (MaxConcurrentCalls > 0)
        {
            _callGate ??= new SemaphoreSlim(MaxConcurrentCalls, MaxConcurrentCalls);
            await _callGate.WaitAsync(ct);
            try { return await DecisionInnerAsync(history, ct, injectedSkills); }
            finally { _callGate.Release(); }
        }
        return await DecisionInnerAsync(history, ct, injectedSkills);
    }

    private async Task<string?> DecisionInnerAsync(List<AgentMessage> history, CancellationToken ct,
                                                   HashSet<string>? injectedSkills)
    {
        var msgs = MultimodalMain
            ? history.Select(m => (m.Role, MaybeAttachImage(m))).ToList()
            : history.Select(m => (m.Role, m.Content)).ToList();
        string sys = SystemPromptForMode() + BuildSkillsPrompt(history, injectedSkills) + ToolDecisionPrompt;
        return await CallOnceAsync(sys, msgs, ct, null);
    }

    /// <summary>
    /// 阶段三：生成最终回复。工具结果已经在 history 里，这一轮流式产出自然语言回答。
    /// </summary>
    private async Task<string?> CallLlmFinalAsync(List<AgentMessage> history, CancellationToken ct,
                                                  Action<string>? onDelta, HashSet<string>? injectedSkills)
    {
        if (MaxConcurrentCalls > 0)
        {
            _callGate ??= new SemaphoreSlim(MaxConcurrentCalls, MaxConcurrentCalls);
            await _callGate.WaitAsync(ct);
            try { return await FinalInnerAsync(history, ct, onDelta, injectedSkills); }
            finally { _callGate.Release(); }
        }
        return await FinalInnerAsync(history, ct, onDelta, injectedSkills);
    }

    private async Task<string?> FinalInnerAsync(List<AgentMessage> history, CancellationToken ct,
                                                Action<string>? onDelta, HashSet<string>? injectedSkills)
    {
        var msgs = MultimodalMain
            ? history.Select(m => (m.Role, MaybeAttachImage(m))).ToList()
            : history.Select(m => (m.Role, m.Content)).ToList();
        string sys = SystemPromptForMode() + BuildSkillsPrompt(history, injectedSkills) + FinalAnswerPrompt;
        return await CallOnceAsync(sys, msgs, ct, onDelta);
    }

    /// <summary>决策阶段是否明确表示「不需要工具」（{"tool_call":null} 等）。</summary>
    private static bool IsNoToolDecision(string? reply)
    {
        string t = (reply ?? "").Trim();
        if (t.Length == 0) return false;
        return t.Equals("null", StringComparison.OrdinalIgnoreCase) ||
               t.Equals("{}", StringComparison.Ordinal) ||
               Regex.IsMatch(t, "\"tool_call\"\\s*:\\s*null") ||
               Regex.IsMatch(t, "\"tool\"\\s*:\\s*null") ||
               Regex.IsMatch(t, "\"tool_call\"\\s*:\\s*\\{\\s*\\}");
    }

    /// <summary>决策阶段表示「不需要工具 / 任务已完成」的工具名。</summary>
    private static bool IsFinalTool(string tool) =>
        tool is "完成" or "结束" or "回答" or "done" or "final" or "answer";

    /// <summary>把「完成」决策里的 回答 字段取出来当最终回答；JSON 被 max_tokens 截断时做兜底抢救。</summary>
    private static string ExtractFinalText(string reply, JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object)
        {
            foreach (string key in new[] { "回答", "答复", "内容", "content", "text", "answer", "reply" })
            {
                if (args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    string s = v.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
        }
        string raw = (reply ?? "").Trim();
        int i = raw.IndexOf("\"回答\"", StringComparison.Ordinal);
        if (i < 0) i = raw.IndexOf("\"答复\"", StringComparison.Ordinal);
        if (i < 0) i = raw.IndexOf("\"content\"", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            int colon = raw.IndexOf(':', i);
            int q = colon >= 0 ? raw.IndexOf('"', colon + 1) : -1;
            if (q >= 0)
            {
                string body = raw[(q + 1)..];
                int last = body.LastIndexOf('"');
                if (last >= 0) body = body[..last];
                body = body.Replace("\\r", "").Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
                if (!string.IsNullOrWhiteSpace(body)) return body;
            }
        }
        return raw;
    }

    private async Task<string?> CallLlmAsync(List<AgentMessage> history, CancellationToken ct = default,
                                             Action<string>? onDelta = null,
                                             HashSet<string>? injectedSkills = null)
    {
        if (MaxConcurrentCalls > 0)
        {
            _callGate ??= new SemaphoreSlim(MaxConcurrentCalls, MaxConcurrentCalls);
            await _callGate.WaitAsync(ct);
            try
            {
                return await CallLlmInnerAsync(history, ct, onDelta, injectedSkills);
            }
            finally
            {
                _callGate.Release();
            }
        }
        return await CallLlmInnerAsync(history, ct, onDelta, injectedSkills);
    }

    private async Task<string?> CallLlmInnerAsync(List<AgentMessage> history, CancellationToken ct,
                                                  Action<string>? onDelta, HashSet<string>? injectedSkills)
    {
        var msgs = history.Select(m => (m.Role, m.Content)).ToList();
        if (MultimodalMain)
            msgs = history.Select(m => (m.Role, MaybeAttachImage(m))).ToList();
        return await CallOnceAsync(SystemPromptForMode() + BuildSkillsPrompt(history, injectedSkills), msgs, ct, onDelta);
    }

    /// <summary>多模态模式：把用户上传的图片路径转为 IMG|路径|文本 标记，由 API 客户端内联为 image_url。</summary>
    private string MaybeAttachImage(AgentMessage m)
    {
        string c = m.Content;
        if (m.Role != "user" || !c.Contains("用户上传了文件：", StringComparison.Ordinal)) return c;
        int s = c.IndexOf("用户上传了文件：", StringComparison.Ordinal) + "用户上传了文件：".Length;
        int e = c.IndexOf('（', s);
        if (e <= s) return c;
        string path = c[s..e].Trim();
        if (!File.Exists(path)) return c;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        bool isImage = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
        return isImage ? "IMG|" + path + "|" + c : c;
    }

    private string BuildSkillsPrompt(List<AgentMessage> history, HashSet<string>? injected)
    {
        if (Skills.Count == 0) return "";
        string user = "";
        for (int i = history.Count - 1; i >= 0; i--)
            if (history[i].Role == "user") { user = history[i].Content; break; }
        var sb = new StringBuilder("\n\n【已安装技能】\n");
        bool anyContent = false;
        foreach (var s in Skills)
        {
            bool kwHit = DescriptionTokens(s.Description).Any(k => user.Contains(k, StringComparison.Ordinal));
            bool aliasHit = s.Aliases.Any(a => a.Length >= 2 && user.Contains(a, StringComparison.OrdinalIgnoreCase));
            bool hit = user.Contains(s.Name, StringComparison.OrdinalIgnoreCase) ||
                       user.Contains("技能", StringComparison.Ordinal) ||
                       (injected != null && injected.Contains(s.Name)) ||
                       BigramScore(user, s.Name + " " + s.Description) >= 1 ||
                       kwHit ||
                       aliasHit;
            sb.AppendLine($"- {s.Name}：{s.Description}");
            if (hit)
            {
                injected?.Add(s.Name);
                SkillUsed?.Invoke(_currentSid, s.Name);
                Log($"技能启用：{s.Name}");
                anyContent = true;
                sb.AppendLine($"\n【技能「{s.Name}」使用说明】\n{Truncate(s.Content, 4000)}");
                if (s.Files.Count > 0)
                    sb.AppendLine($"\n技能目录包含文件（需要更详细说明时用「读取文件」查看）：{string.Join("、", s.Files.Take(12))}");
            }
        }
        if (!anyContent)
            sb.AppendLine("\n当用户要求使用某个已安装技能时，按对应说明执行；没有匹配技能时忽略本段。");
        return sb.ToString();
    }

    /// <summary>把技能描述拆成关键词（中文词元/英文单词，长度 ≥2），用于更宽松的自动匹配。</summary>
    private static IEnumerable<string> DescriptionTokens(string text)
    {
        foreach (Match m in Regex.Matches(text, @"[\u4e00-\u9fff]{2,}|[A-Za-z0-9_]{2,}"))
        {
            string t = m.Value;
            if (t.Length >= 2) yield return t;
        }
    }

    /// <summary>自动匹配：任务文本与技能名称/描述共享 ≥1 个二字词元即视为相关。</summary>
    private static int BigramScore(string a, string b)
    {
        var set = new HashSet<string>();
        for (int i = 0; i + 1 < a.Length; i++) set.Add(a.Substring(i, 2));
        int n = 0;
        for (int i = 0; i + 1 < b.Length; i++)
            if (set.Contains(b.Substring(i, 2))) n++;
        return n;
    }

    /// <summary>模型表示无法完成时，自动挑一个最相关的未注入技能补一次提示。</summary>
    private string? SuggestSkill(string reply, List<AgentMessage> history, HashSet<string> injected)
    {
        if (Skills.Count == 0 || string.IsNullOrWhiteSpace(reply)) return null;
        if (!Regex.IsMatch(reply, @"不会|无法|做不到|不知道怎么|不能|失败|没找到|不清楚|没有.*技能"))
            return null;
        string user = "";
        for (int i = history.Count - 1; i >= 0; i--)
            if (history[i].Role == "user") { user = history[i].Content; break; }
        var best = Skills
            .Where(s => !injected.Contains(s.Name))
            .OrderByDescending(s => BigramScore(user, s.Name + " " + s.Description) +
                                    AliasBonus(user, s) + (user.Contains(s.Name) ? 5 : 0))
            .FirstOrDefault();
        if (best == null) return null;
        int score = BigramScore(user, best.Name + " " + best.Description) + AliasBonus(user, best) + (user.Contains(best.Name) ? 5 : 0);
        if (score < 1) return null;
        injected.Add(best.Name);
        SkillUsed?.Invoke(_currentSid, best.Name);
        Log($"技能补救启用：{best.Name}");
        return $"AI 表示无法完成。已自动找到可能适用的技能「{best.Name}」：{best.Description}\n\n技能说明：\n{Truncate(best.Content, 3000)}\n\n请立即按该技能说明重新完成任务，不要说不会。";
    }

    /// <summary>任务与技能高度相关但模型没用技能时，补一次技能说明并重试（每轮最多 2 次）。</summary>
    private string? SuggestUnusedSkill(string reply, List<AgentMessage> history, HashSet<string> injected)
    {
        if (Skills.Count == 0 || injected.Count >= 2) return null;
        string user = "";
        for (int i = history.Count - 1; i >= 0; i--)
            if (history[i].Role == "user") { user = history[i].Content; break; }
        if (!Regex.IsMatch(user, @"做|生成|写|抓取|制作|创建|开发|网页|爬"))
            return null;
        var best = Skills
            .Where(s => !injected.Contains(s.Name))
            .OrderByDescending(s => BigramScore(user, s.Name + " " + s.Description) +
                                    AliasBonus(user, s) + (user.Contains(s.Name) ? 5 : 0))
            .FirstOrDefault();
        if (best == null) return null;
        int score = BigramScore(user, best.Name + " " + best.Description) + AliasBonus(user, best) + (user.Contains(best.Name) ? 5 : 0);
        if (score < 3 || (reply ?? "").Contains(best.Name, StringComparison.Ordinal)) return null;
        injected.Add(best.Name);
        SkillUsed?.Invoke(_currentSid, best.Name);
        Log($"技能补强：{best.Name}");
        return $"检测到任务与技能「{best.Name}」高度相关但尚未使用。技能说明：\n{Truncate(best.Content, 3000)}\n\n请按该技能说明重新完成任务，并真实调用对应工具。";
    }

    private static int AliasBonus(string user, SkillInfo s)
        => s.Aliases.Count(a => a.Length >= 2 && user.Contains(a, StringComparison.OrdinalIgnoreCase)) * 4;

    /// <summary>单次 LLM 调用（不跑工具循环）：按后端选择 API / Ollama，429 自动退避重试。</summary>
    private async Task<string?> CallOnceAsync(string system,
        List<(string Role, string Content)> msgs, CancellationToken ct, Action<string>? onDelta)
    {
        if (Backend == "local")
        {
            string user = msgs.Count > 0 ? msgs[^1].Content : "";
            string reply = LocalNormalReply(user);
            Log($"本地自训练 回复长度={reply.Length}");
            return reply;
        }
        if (Backend == "llm")
        {
            string? r = Streaming
                ? await _ollama.ChatStreamAsync(system, msgs, onDelta, ct: ct)
                : await _ollama.ChatAsync(system, msgs, ct: ct);
            if (string.IsNullOrEmpty(r) && Streaming && !ct.IsCancellationRequested)
                r = await _ollama.ChatAsync(system, msgs, ct: ct);   // 流式失败回退普通请求
            Log($"Ollama 回复长度={r?.Length ?? 0} LastError={_ollama.LastError}");
            return SanitizeOllamaReply(r);
        }

        // api / auto：优先外部 API；429 限流自动退避重试，失败再降级本地 Ollama
        if (Backend == "api" || _api.Configured)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                bool any = false;
                Action<string>? sink = Streaming && onDelta != null
                    ? d => { any = true; onDelta(d); }
                    : null;
                string? reply = null;
                if (Streaming && onDelta != null)
                {
                    reply = await _api.ChatStreamAsync(system, msgs, sink, ct: ct);
                    // 流式不支持/失败（非限流、非取消）→ 回退普通请求，保证能出话
                    if (string.IsNullOrEmpty(reply) && !ct.IsCancellationRequested &&
                        !IsRateLimit(_api.LastError))
                        reply = await _api.ChatAsync(system, msgs, ct: ct);
                }
                else
                {
                    reply = await _api.ChatAsync(system, msgs, ct: ct);
                }
                Log($"API 回复长度={reply?.Length ?? 0} 流式增量={any} LastError={_api.LastError}");
                if (!string.IsNullOrEmpty(reply)) return reply;
                if (ct.IsCancellationRequested) return null;
                if (!IsRateLimit(_api.LastError) || any) break;
                await Task.Delay(TimeSpan.FromSeconds(3) * (attempt + 1), ct);
            }
        }
        // API 已配置但返回的是配置类错误（4xx，限流除外）→ 不许静默降级到本地小模型，
        // 否则用户看到的是「AI 突然不会用工具了、还胡说八道」，其实是 API 地址/模型名/key 写错了。
        if (_api.Configured && IsClientConfigError(_api.LastError)) return null;

        return SanitizeOllamaReply(Streaming && onDelta != null
            ? await _ollama.ChatStreamAsync(system, msgs, onDelta, ct: ct)
            : await _ollama.ChatAsync(system, msgs, ct: ct));
    }

    /// <summary>集群汇总：单次 LLM 调用，不走工具循环；支持流式与限流重试。</summary>
    public async Task<string> SummarizeAsync(string system, string content,
        CancellationToken ct = default, Action<string>? onDelta = null)
    {
        var msgs = new List<(string Role, string Content)> { ("user", content) };
        if (MaxConcurrentCalls > 0)
        {
            _callGate ??= new SemaphoreSlim(MaxConcurrentCalls, MaxConcurrentCalls);
            await _callGate.WaitAsync(ct);
            try
            {
                return (await CallOnceAsync(system, msgs, ct, onDelta)) ?? "";
            }
            finally
            {
                _callGate.Release();
            }
        }
        return (await CallOnceAsync(system, msgs, ct, onDelta)) ?? "";
    }

    /// <summary>
    /// 是否属于「配置/网络问题」。这类失败**不许静默降级到本地小模型**——
    /// 否则用户看到的是「AI 突然不会用工具了、还胡说八道」，其实是地址/模型名/Key 写错或根本连不上。
    /// 限流（429）不算，那是要退避重试的。
    /// </summary>
    private static bool IsClientConfigError(string? err)
    {
        if (string.IsNullOrWhiteSpace(err)) return false;
        var m = Regex.Match(err, @"HTTP\s*(\d{3})");
        if (m.Success)
        {
            int code = int.Parse(m.Groups[1].Value);
            if (code == 429) return false;
            return code >= 400 && code < 500;              // 4xx：地址/模型名/Key 写错
        }
        // 连不上 / 超时 / DNS 失败：同样如实报错，不要偷偷换成本地小模型
        string[] netHints =
        {
            "超时", "连接", "未能解析", "拒绝", "远程名称", "网络",
            "timed out", "timeout", "No such host", "actively refused",
            "connection", "SSL", "certificate",
        };
        foreach (string k in netHints)
            if (err.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsRateLimit(string? err)
        => err != null &&
           (err.Contains("429", StringComparison.Ordinal) ||
            err.Contains("访问量过大", StringComparison.Ordinal) ||
            err.Contains("限流", StringComparison.Ordinal) ||
            err.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("too many requests", StringComparison.OrdinalIgnoreCase));

    /// <summary>把本地自训练模型输出规范化：清理重复标点、悬空连接符，让回复更通顺。</summary>
    private static string NormalizeLocalReply(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "（本地模型暂无输出，请换个说法再试。）";
        string t = text;
        t = Regex.Replace(t, @"[，,。！？!?]{2,}", m => m.Value[^1].ToString());
        t = Regex.Replace(t, @"——+[，,。]?", "，");
        t = Regex.Replace(t, @"[，,]\s*[，,]+", "，");
        t = Regex.Replace(t, @"\s{2,}", " ");
        t = t.Trim();
        if (t.Length == 0) return "（本地模型暂无输出，请换个说法再试。）";
        if (!t.EndsWith("。") && !t.EndsWith("！") && !t.EndsWith("？") && !t.EndsWith("…") && !t.EndsWith("\""))
            t += "。";
        return t;
    }

    /// <summary>兜底落盘：LLM 直接在回答里吐代码块时，把代码写入工作区文件并返回路径。</summary>
    private string? TrySaveCodeBlock(int sid, string reply, List<AgentMessage> history)
    {
        var m = Regex.Match(reply ?? "", @"```([\w+#-]*)\s*\r?\n([\s\S]*?)```");
        if (!m.Success) return null;
        string lang = m.Groups[1].Value.Trim().ToLowerInvariant();
        string code = m.Groups[2].Value.TrimEnd();
        if (code.Length == 0) return null;
        string dir = string.IsNullOrWhiteSpace(DataDir) ? Path.GetTempPath() : Path.Combine(DataDir, "cluster_workspace");
        Directory.CreateDirectory(dir);
        string ext = lang switch
        {
            "html" or "htm" => "html",
            "css" => "css",
            "js" or "javascript" => "js",
            "python" or "py" => "py",
            "c#" or "cs" or "csharp" => "cs",
            "json" => "json",
            "ts" or "typescript" => "ts",
            "sql" => "sql",
            "sh" or "bash" or "powershell" or "ps1" => "ps1",
            _ => "txt",
        };
        string path = Path.Combine(dir, GuessCodeFileName(history, lang, ext));
        string result = WriteFileTool(Obj(("路径", path), ("内容", code)));
        history.Add(new AgentMessage { Role = "tool", Content = result, Meta = ToolWriteFile });
        TrimHistory(history);
        MessageAdded?.Invoke(sid, history[^1]);
        string preview = code.Length > 800 ? code[..800] + "…" : code;
        return $"已将代码写入文件：{path}\n\n```{lang}\n{preview}\n```";
    }

    private static string GuessCodeFileName(List<AgentMessage> history, string lang, string ext)
    {
        string user = "";
        for (int i = history.Count - 1; i >= 0; i--)
            if (history[i].Role == "user") { user = history[i].Content; break; }
        if (user.Contains("网页", StringComparison.Ordinal) || user.Contains("待办", StringComparison.Ordinal))
            return "index.html";
        if (user.Contains("爬虫", StringComparison.Ordinal) || user.Contains("抓取", StringComparison.Ordinal))
            return "crawler.py";
        if (user.Contains("python", StringComparison.OrdinalIgnoreCase) || user.Contains("脚本", StringComparison.Ordinal))
            return "script.py";
        return $"code_{DateTime.Now:HHmmss}.{ext}";
    }

    /// <summary>本地自训练模式下的正常应答：攻击言论才回怼，普通问题给友好说明，绝不对用户开火。</summary>
    private string LocalNormalReply(string user)
    {
        if (string.IsNullOrWhiteSpace(user))
            return "（本地模型暂无输出，请换个说法再试。）";
        if (Regex.IsMatch(user, @"骂|喷|傻|蠢|滚|废物|垃圾|去死|白痴|猪|狗|脑残|智障|闭嘴|欠揍|欠骂"))
        {
            int sid = _counterSid++;
            _counter.NewSession(sid);
            var r = _counter.Generate(user, sid, manual: "auto", tone: "default");
            return NormalizeLocalReply(r.Response);
        }
        string brief = user.Length > 40 ? user[..40] + "…" : user;
        string kind = "";
        if (user.Contains("翻译", StringComparison.Ordinal)) kind = "翻译任务";
        else if (user.Contains("代码", StringComparison.Ordinal) || user.Contains("程序", StringComparison.Ordinal))
            kind = "写代码任务";
        else if (user.Contains("总结", StringComparison.Ordinal) || user.Contains("概括", StringComparison.Ordinal))
            kind = "总结任务";
        else if (user.Contains("搜索", StringComparison.Ordinal) || user.Contains("查", StringComparison.Ordinal))
            kind = "搜索/查资料任务";
        string tail = kind.Length > 0
            ? $"\n\n你提到的是{kind}。本地自训练模型主要用于回怼训练，做这类任务建议点掉「🧠 自训练」切回 API 或 Ollama，以获得完整可靠的结果。"
            : "";
        return $"已收到你的消息：{brief}。当前使用本地自训练模型（零依赖）；需要更强能力时切回 API 或 Ollama 后端。{tail}";
    }

    /// <summary>启发式解析本地模式下的工具指令（无需大模型，也能调用工具干活）。</summary>
    private bool TryLocalToolCall(string text, out string tool, out JsonElement args)
    {
        tool = "";
        args = default;
        string t = text.Trim();
        if (t.Length == 0) return false;

        var code = Regex.Match(t, @"```(?:python|py)?\s*\r?\n([\s\S]*?)```", RegexOptions.IgnoreCase);
        if (code.Success)
        {
            tool = ToolPython;
            args = Obj(("代码", code.Groups[1].Value.Trim()));
            return true;
        }
        if (t.Contains("待办", StringComparison.Ordinal) ||
            (t.Contains("做一个", StringComparison.Ordinal) && t.Contains("网页", StringComparison.Ordinal)))
        {
            string dir = string.IsNullOrWhiteSpace(DataDir) ? Path.GetTempPath() : Path.Combine(DataDir, "cluster_workspace");
            Directory.CreateDirectory(dir);
            tool = ToolWriteFile;
            args = Obj(("路径", Path.Combine(dir, "todo.html")), ("内容", LocalTodoHtml()));
            return true;
        }
        if (t.Contains("运行Python", StringComparison.Ordinal) || t.Contains("运行 python", StringComparison.OrdinalIgnoreCase))
        {
            string body = Regex.Replace(t, @"^.*?运行\s*[Pp]ython\s*脚本?\s*[:：]?\s*", "", RegexOptions.Singleline);
            tool = ToolPython;
            args = Obj(("代码", body.Trim()));
            return true;
        }
        if (t.Contains("生成图片", StringComparison.Ordinal) || t.Contains("画一张", StringComparison.Ordinal) ||
            t.Contains("画个", StringComparison.Ordinal) || t.Contains("画一下", StringComparison.Ordinal) ||
            t.Contains("帮我画", StringComparison.Ordinal) || t.Contains("给我画", StringComparison.Ordinal))
        {
            string p = Regex.Replace(t, @"^(生成图片|帮我画|给我画|画一张|画个|画一下)\s*[:：]?\s*", "");
            if (p.Length > 0)
            {
                tool = ToolImage;
                args = Obj(("提示词", p));
                return true;
            }
        }
        if (t.Contains("看图", StringComparison.Ordinal) || t.Contains("识别图片", StringComparison.Ordinal) ||
            t.Contains("看下这张图", StringComparison.Ordinal))
        {
            var pm = Regex.Match(t, @"[""']([A-Za-z]:\\(?:[^""']+))[""']");
            if (!pm.Success) pm = Regex.Match(t, @"([A-Za-z]:\\(?:[^,\s""']+))");
            if (pm.Success)
            {
                tool = ToolVision;
                args = Obj(("图片路径", pm.Groups[1].Value), ("问题", "描述这张图片的内容"));
                return true;
            }
        }
        if (t.Contains("打开文件", StringComparison.Ordinal))
        {
            var om = Regex.Match(t, @"[""']([A-Za-z]:\\(?:[^""']+))[""']");
            if (!om.Success) om = Regex.Match(t, @"([A-Za-z]:\\(?:[^,\s""']+))");
            if (om.Success)
            {
                tool = ToolOpenFile;
                args = Obj(("路径", om.Groups[1].Value));
                return true;
            }
        }
        if (t.Contains("读取文件", StringComparison.Ordinal))
        {
            var rm = Regex.Match(t, @"[""']([A-Za-z]:\\(?:[^""']+))[""']");
            if (!rm.Success) rm = Regex.Match(t, @"([A-Za-z]:\\(?:[^,\s""']+))");
            if (rm.Success)
            {
                tool = ToolReadFile;
                args = Obj(("路径", rm.Groups[1].Value));
                return true;
            }
        }
        if (t.Contains("写入文件", StringComparison.Ordinal))
        {
            var wm = Regex.Match(t, @"[""']([A-Za-z]:\\(?:[^""']+))[""']");
            if (wm.Success)
            {
                string content = t.Substring(wm.Index + wm.Length).TrimStart('，', ',', ' ', ':', '：', '\n', '\r');
                content = Regex.Replace(content, @"^(内容|写入内容)\s*[:：]?\s*", "");
                tool = ToolWriteFile;
                args = Obj(("路径", wm.Groups[1].Value), ("内容", content));
                return true;
            }
        }
        if (t.StartsWith("运行命令", StringComparison.Ordinal))
        {
            string cmd = Regex.Replace(t, @"^运行命令\s*[:：]?\s*", "");
            if (cmd.Length > 0)
            {
                tool = ToolRunCommand;
                args = Obj(("命令", cmd));
                return true;
            }
        }
        if (t.Contains("抓取网页", StringComparison.Ordinal) || t.Contains("打开网页", StringComparison.Ordinal) ||
            t.Contains("读取网页", StringComparison.Ordinal))
        {
            var fm = Regex.Match(t, @"https?://[^\s，,；;""']+");
            if (fm.Success)
            {
                tool = ToolFetch;
                args = Obj(("网址", fm.Value.TrimEnd('。', '，', ',', '.', '！', '？')));
                return true;
            }
        }
        if (t.StartsWith("搜索", StringComparison.Ordinal) || t.Contains("搜一下", StringComparison.Ordinal) ||
            t.Contains("搜一搜", StringComparison.Ordinal) || t.StartsWith("查一下", StringComparison.Ordinal) ||
            t.StartsWith("查查", StringComparison.Ordinal) || t.StartsWith("查价格", StringComparison.Ordinal) ||
            t.StartsWith("查资料", StringComparison.Ordinal))
        {
            string q = Regex.Replace(t, @"^(搜索|搜一下|搜一搜|搜搜|查一查|查查|查一下|查价格|查资料|帮我搜|帮我查)\s*[:：]?\s*", "");
            if (q.Length > 0)
            {
                tool = ToolSearch;
                args = Obj(("关键词", q));
                return true;
            }
        }
        return false;
    }

    private static JsonElement Obj(params (string k, string v)[] kv)
        => JsonSerializer.SerializeToElement(kv.ToDictionary(x => x.k, x => x.v));

    private static string LocalToolSummary(string tool, string result)
    {
        string brief = result.Length > 500 ? result[..500] + "…" : result;
        return $"已用工具「{tool}」完成：\n{brief}";
    }

    private static string LocalTodoHtml() =>
        "<!doctype html><html lang=\"zh\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
        "<title>待办事项</title><style>body{font-family:system-ui,sans-serif;max-width:560px;margin:40px auto;padding:0 16px;color:#222}" +
        "h1{font-size:22px}.row{display:flex;gap:8px;margin:16px 0}input{flex:1;padding:10px;border:1px solid #ccc;border-radius:8px;font-size:15px}" +
        "button{padding:10px 14px;border:0;border-radius:8px;background:#2f7dff;color:#fff;font-size:14px;cursor:pointer}" +
        "li{display:flex;align-items:center;justify-content:space-between;padding:10px 0;border-bottom:1px solid #eee}" +
        "li.done span{text-decoration:line-through;color:#999}</style></head><body>" +
        "<h1>✅ 待办事项</h1><div class=\"row\"><input id=\"t\" placeholder=\"输入待办…\" onkeydown=\"if(event.key==='Enter')add()\">" +
        "<button onclick=\"add()\">添加</button></div><ul id=\"list\"></ul>" +
        "<script>const k='todos';const list=document.getElementById('list');" +
        "function save(){localStorage.setItem(k,JSON.stringify(todos))}let todos=JSON.parse(localStorage.getItem(k)||'[]');" +
        "function render(){list.innerHTML='';todos.forEach((x,i)=>{const li=document.createElement('li');li.className=x.done?'done':'';" +
        "const s=document.createElement('span');s.textContent=x.t;s.onclick=()=>{x.done=!x.done;save();render()};" +
        "const b=document.createElement('button');b.textContent='删除';b.onclick=()=>{todos.splice(i,1);save();render()};" +
        "li.append(s,b);list.append(li)})}function add(){const v=document.getElementById('t').value.trim();" +
        "if(!v)return;todos.push({t:v,done:false});save();document.getElementById('t').value='';render()}render();</script></body></html>";

    /// <summary>判断用户消息是否包含"搜索/查价格/查资料"等搜索意图。</summary>
    private static bool IsSearchIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Regex.IsMatch(text,
            @"搜索|搜一下|搜一搜|搜搜|查一下|查查|查价格|查资料|多少钱|报价|行情|最新消息|最新价格|最新动态|新闻");
    }

    /// <summary>本地模型回复兜底检查：空内容或思考链泄漏视为失败，避免把思考当答案。</summary>
    private static string? SanitizeOllamaReply(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return LooksLikeThinking(text) ? null : text;
    }

    private static readonly string[] ThinkingMarkers =
    {
        "首先", "其次", "然后", "最后", "综上", "分析", "用户说", "用户发",
        "用户问", "对方说", "我需要", "关键点", "回顾", "硬规则", "思考",
    };

    private static bool LooksLikeThinking(string text)
    {
        int hits = 0;
        foreach (var m in ThinkingMarkers)
            if (text.Contains(m, StringComparison.Ordinal)) hits++;
        return hits >= 3;
    }

    /// <summary>判断文本是否是 JSON / 结构化片段（最终回答不应是 JSON）。</summary>
    private static bool LooksLikeJson(string text)
    {
        string t = text.TrimStart();
        if (!t.StartsWith('{') && !t.StartsWith('[')) return false;
        try
        {
            using var doc = JsonDocument.Parse(t);
            return true;
        }
        catch
        {
            int open = t.Count(c => c is '{' or '[');
            int close = t.Count(c => c is '}' or ']');
            return open >= 2 && close >= 2;
        }
    }

    // ------------------------------------------------------------------
    // 工具调用协议（JSON 解析，兼容各家模型）
    // ------------------------------------------------------------------

    private static bool TryParseToolCall(string reply, out string tool, out JsonElement args)
    {
        tool = "";
        args = default;

        // 去掉 ```json ... ``` 围栏（部分模型会把 JSON 包在代码块里）
        string candidate = (reply ?? "").Trim();
        if (candidate.StartsWith("```", StringComparison.Ordinal))
        {
            int nl = candidate.IndexOf('\n');
            if (nl > 0) candidate = candidate[(nl + 1)..];
            int closing = candidate.LastIndexOf("```", StringComparison.Ordinal);
            if (closing >= 0) candidate = candidate[..closing];
            candidate = candidate.Trim();
        }

        // ① 整体就是合法 JSON
        if (TryParseToolObject(candidate, out tool, out args)) return true;

        // ② 从包裹文本里找平衡的 JSON 对象（字符串内的 { } 不参与配对）
        int idx = candidate.IndexOf('{');
        while (idx >= 0)
        {
            int end = FindJsonEnd(candidate, idx);
            if (end > idx && TryParseToolObject(candidate.Substring(idx, end - idx), out tool, out args))
                return true;
            idx = candidate.IndexOf('{', idx + 1);
        }

        // ③ 容错：字符串值里的裸换行修复为 \n 后重试（Python 代码常带换行）
        string repaired = Regex.Replace(reply ?? "", @"\r?\n", "\\n");
        if (TryParseToolObject(repaired, out tool, out args)) return true;

        // ④ 只抠出工具名（args 为空 → 工具返回参数错误，模型会据此修正重试）
        var m = Regex.Match(reply ?? "", @"""tool""\s*:\s*""([^""]+)""");
        if (m.Success)
        {
            tool = m.Groups[1].Value;
            return true;
        }
        return false;
    }

    private static bool TryParseToolObject(string json, out string tool, out JsonElement args)
    {
        tool = "";
        args = default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 标准 tool_call 形状：{"tool_call":{"name":"x","arguments":{...}}} 或 {"name":"x","arguments":"{...}"}
            JsonElement call = root;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tool_call", out var tcv) &&
                tcv.ValueKind == JsonValueKind.Object)
                call = tcv;
            if (call.ValueKind == JsonValueKind.Object && call.TryGetProperty("name", out var nmv) &&
                nmv.ValueKind == JsonValueKind.String)
            {
                string nm = nmv.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(nm))
                {
                    tool = nm;
                    JsonElement raw = call.TryGetProperty("arguments", out var arv) ? arv.Clone()
                                    : call.TryGetProperty("args", out var arv2) ? arv2.Clone() : default;
                    if (raw.ValueKind == JsonValueKind.String)
                    {
                        // 有些模型把 arguments 序列化成字符串
                        try
                        {
                            using var inner = JsonDocument.Parse(raw.GetString() ?? "{}");
                            raw = inner.RootElement.Clone();
                        }
                        catch { /* 不是 JSON 就原样保留 */ }
                    }
                    args = raw;
                    return true;
                }
            }

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("tool", out var t) && t.ValueKind == JsonValueKind.String)
            {
                tool = t.GetString() ?? "";
                args = root.TryGetProperty("args", out var a) ? a.Clone() : default;
                return !string.IsNullOrEmpty(tool);
            }
        }
        catch { /* 尝试下一层 */ }
        return false;
    }

    private static int FindJsonEnd(string s, int start)
    {
        int depth = 0;
        bool inStr = false, esc = false;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) return i + 1; }
        }
        return -1;
    }

    // ------------------------------------------------------------------
    // 工具实现
    // ------------------------------------------------------------------

    private async Task<string> ExecuteToolAsync(string tool, JsonElement args, int sid,
                                                 CancellationToken ct)
    {
        try
        {
            return tool switch
            {
                ToolRunCommand => await RunCommandTool(args, sid, ct),
                ToolPython => await PythonTool(args, sid, ct),
                ToolImage => await ImageTool(args, sid, ct),
                ToolVision => MultimodalMain
                    ? "图片已作为多模态输入直接发送给主模型，请直接描述图片内容，不要再调用「看图」工具。"
                    : await VisionTool(args, sid, ct),
                ToolSearch => await WebSearchTool(args, ct),
                ToolFetch => await FetchUrlTool(args, ct),
                ToolOpenFile => await OpenFileTool(args, sid),
                ToolReadFile => ReadFileTool(args),
                ToolWriteFile => WriteFileTool(args),
                ToolRemember => RememberTool(args),
                ToolForget => ForgetTool(args),
                _ => $"未知工具：{tool}",
            };
        }
        catch (Exception ex)
        {
            return $"工具「{tool}」执行失败：{ex.Message}";
        }
    }

    private string RememberTool(JsonElement args)
    {
        string? content = GetArg(args, "内容");
        if (string.IsNullOrWhiteSpace(content))
            return "参数错误：缺少「内容」。";
        Memory.Add(content);
        Log($"已记住：{content}");
        return $"已写入长期记忆：{content}\n当前共 {Memory.Entries.Count} 条记忆。";
    }

    private string ForgetTool(JsonElement args)
    {
        string? idx = GetArg(args, "序号");
        if (!int.TryParse(idx, out int n) || !Memory.RemoveAt(n))
            return "参数错误：序号无效。当前记忆：" + (string.IsNullOrWhiteSpace(Memory.AllText()) ? "（空）" : "\n" + Memory.AllText());
        Log($"已删除记忆 #{n}");
        return $"已删除第 {n} 条记忆。当前共 {Memory.Entries.Count} 条记忆。";
    }

    private async Task<string> CounterTool(JsonElement args, CancellationToken ct)
    {
        string? text = GetArg(args, "言论");
        if (string.IsNullOrWhiteSpace(text))
            return "参数错误：缺少「言论」。";

        string tone = (GetArg(args, "语气") ?? "") switch
        {
            "sharp" => "sharp",
            "default" => "default",
            "live" => "live",
            _ => RunMode == "boom" ? "sharp" : "live",
        };

        // 全面采用 GLM-4-Flash-250414 生成回怼；失败/未配置回退本地引擎
        if (_counterLlm.Configured)
        {
            string? llm = await _counterLlm.ChatAsync(CounterSystemPrompt, text, ct: ct);
            if (!string.IsNullOrWhiteSpace(llm))
                return $"回复（GLM-4-Flash）：\n{llm}";
        }

        int sid = _counterSid++;
        _counter.NewSession(sid);
        var r = _counter.Generate(text, sid, manual: "auto", tone: tone);
        return $"攻击类型：{r.CategoryName}（强度 {r.Aggression}/10）\n策略：{r.StrategyName}\n回复：\n{r.Response}";
    }

    private async Task<string> RunCommandTool(JsonElement args, int sid, CancellationToken ct)
    {
        string? cmd = GetArg(args, "命令");
        if (string.IsNullOrWhiteSpace(cmd))
            return "参数错误：缺少「命令」。";
        string? workDir = GetArg(args, "工作目录") ?? GetArg(args, "目录");
        if (!string.IsNullOrWhiteSpace(workDir) && !Directory.Exists(workDir))
            return $"工作目录不存在：{workDir}";

        // 拦截"命令行抓网页"：搜索类需求应走「联网搜索」工具，而不是 curl 等
        if (LooksLikeWebFetch(cmd))
            return "⚠️ 检测到你在用命令行抓取网页。网页搜索/查资料请改用「联网搜索」工具；" +
                   "如果该工具提示已关闭，请提醒用户打开 🌐 联网搜索开关。不要用 curl / wget / Invoke-WebRequest / Python requests 等代替。";
        string? sandboxErr = Sandbox.CheckCommand(cmd);
        if (sandboxErr != null) return sandboxErr;

        // work 模式：执行命令前请求用户确认
        if (RunMode == "work" && ConfirmRequested != null)
        {
            bool ok = await ConfirmRequested(sid,
                $"命令：{cmd}\n工作目录：{(string.IsNullOrWhiteSpace(workDir) ? "（默认）" : workDir)}");
            if (!ok)
                return "用户拒绝执行该命令。请换一种方式，或提示用户可切到 boom 模式。";
        }

        // 让 PowerShell 以 UTF-8 输出，避免中文乱码
        string full = "$OutputEncoding=[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + cmd;
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{full.Replace("\"", "\\\"")}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(workDir)) psi.WorkingDirectory = workDir;

        Process? proc = null;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 PowerShell");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            var timeout = Task.Delay(TimeSpan.FromSeconds(CommandTimeoutSeconds), ct);
            if (await Task.WhenAny(proc.WaitForExitAsync(ct), timeout) == timeout)
            {
                try { proc.Kill(true); } catch { /* 忽略 */ }
                if (ct.IsCancellationRequested) return "命令已停止。";
                return $"命令执行超时（{CommandTimeoutSeconds} 秒），已终止。";
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            string result = string.Join("\n",
                new[] { stdout.Trim(), stderr.Trim() }.Where(s => s.Length > 0));
            if (proc.ExitCode != 0)
                result = $"（退出码 {proc.ExitCode}）\n" + result;
            string fullResult =
                $"命令：{cmd}\n工作目录：{(string.IsNullOrWhiteSpace(workDir) ? "（默认）" : workDir)}\n" +
                (string.IsNullOrWhiteSpace(result) ? "（命令无输出）" : result);
            return Truncate(fullResult, MaxToolOutput);
        }
        catch (OperationCanceledException)
        {
            try { proc?.Kill(true); } catch { /* 忽略 */ }
            return "命令已停止。";
        }
    }

    /// <summary>
    /// 写并运行 Python 3 脚本：自动选择解释器、写入临时文件、运行并返回输出/报错。
    /// 干不了的事 → 写脚本解决。
    /// </summary>
    private async Task<string> PythonTool(JsonElement args, int sid, CancellationToken ct)
    {
        string? code = GetArg(args, "代码");
        string? file = GetArg(args, "文件");
        if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(file))
            return "参数错误：缺少「代码」（或「文件」）。";
        string? workDir = GetArg(args, "工作目录");
        if (!string.IsNullOrWhiteSpace(workDir) && !Directory.Exists(workDir))
            return $"工作目录不存在：{workDir}";
        string codeText = code ?? (File.Exists(file ?? "") ? File.ReadAllText(file!) : "");
        string? pySandbox = Sandbox.CheckPython(codeText);
        if (pySandbox != null) return pySandbox;

        if (RunMode == "work" && ConfirmRequested != null)
        {
            string preview = string.IsNullOrWhiteSpace(code)
                ? $"运行 Python 脚本文件：{file}"
                : $"运行 Python 脚本（{code.Length} 字符）\n脚本预览：\n{Preview(code, 300)}";
            bool ok = await ConfirmRequested(sid, preview);
            if (!ok)
                return "用户拒绝运行该脚本。请换一种方式，或提示用户可切到 boom 模式。";
        }

        string? py = await FindPython3Async();
        if (py == null)
            return "未找到 Python 3 解释器。请先安装 Python 3（或告知用户安装）。";

        if (string.IsNullOrWhiteSpace(code))
        {
            if (!File.Exists(file))
                return $"脚本文件不存在：{file}";
        }
        else
        {
            string dir = Path.Combine(Path.GetTempPath(), "agent_py");
            Directory.CreateDirectory(dir);
            file = Path.Combine(dir, $"agent_{Guid.NewGuid():N}.py");
            File.WriteAllText(file, code, new UTF8Encoding(false));
        }

        var psi = new ProcessStartInfo(py, $"\"{file}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(workDir)) psi.WorkingDirectory = workDir;

        Process? proc = null;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 Python");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            var timeout = Task.Delay(TimeSpan.FromSeconds(120), ct);
            if (await Task.WhenAny(proc.WaitForExitAsync(ct), timeout) == timeout)
            {
                try { proc.Kill(true); } catch { /* 忽略 */ }
                if (ct.IsCancellationRequested) return $"Python 脚本已停止。脚本已保存：{file}";
                return $"Python 脚本执行超时（120 秒），已终止。脚本已保存：{file}";
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            string result = string.Join("\n",
                new[] { stdout.Trim(), stderr.Trim() }.Where(s => s.Length > 0));
            if (proc.ExitCode != 0)
                result = $"（退出码 {proc.ExitCode}）\n" + result;
            result = $"脚本：{file}\n" + result;
            return Truncate(string.IsNullOrWhiteSpace(result) ? "（脚本无输出）" : result, MaxToolOutput);
        }
        catch (OperationCanceledException)
        {
            try { proc?.Kill(true); } catch { /* 忽略 */ }
            return $"Python 脚本已停止。脚本已保存：{file}";
        }
    }

    /// <summary>查找可用的 Python 3 解释器（命令 + 常见安装路径，按顺序探测）。</summary>
    private static async Task<string?> FindPython3Async()
    {
        var candidates = new List<string> { "py", "python3", "python" };
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var ver in new[] { "Python313", "Python312", "Python311", "Python310" })
        {
            string full = Path.Combine(local, "Programs", "Python", ver, "python.exe");
            if (File.Exists(full)) candidates.Add(full);
        }

        foreach (var cand in candidates)
        {
            try
            {
                string args = cand == "py" ? "-3 --version" : "--version";
                var psi = new ProcessStartInfo(cand, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null) continue;
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();
                var exitTask = proc.WaitForExitAsync();
                if (await Task.WhenAny(exitTask, Task.Delay(3000)) != exitTask)
                {
                    try { proc.Kill(); } catch { /* 忽略 */ }
                    continue;
                }
                string v = (await outTask) + (await errTask);
                if (v.Contains("Python 3")) return cand;
            }
            catch { /* 尝试下一个候选 */ }
        }
        return null;
    }

    private static string Preview(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    /// <summary>判断命令是否在"用命令行抓网页"（应改用联网搜索工具）。</summary>
    private static bool LooksLikeWebFetch(string cmd)
    {
        string c = cmd.ToLowerInvariant();
        // 全命令扫描抓网页特征（不只看开头，防 powershell -Command / cmd /c / python -c 绕过）
        if (Regex.IsMatch(c, @"\b(curl|wget|iwr|invoke-webrequest|invoke-restmethod)\b"))
            return true;
        if (Regex.IsMatch(c, @"\b(http|https)://") &&
            Regex.IsMatch(c, @"\b(requests|urllib|httpx|aiohttp|webrequest|download)\b"))
            return true;
        return false;
    }

    /// <summary>生成图片：调用 CogView-3-Flash，保存到本地并返回路径。</summary>
    private async Task<string> ImageTool(JsonElement args, int sid, CancellationToken ct)
    {
        string? prompt = GetArg(args, "提示词");
        if (string.IsNullOrWhiteSpace(prompt))
            return "参数错误：缺少「提示词」。";
        string? size = GetArg(args, "尺寸");

        if (RunMode == "work" && ConfirmRequested != null)
        {
            bool ok = await ConfirmRequested(sid, $"生成图片\n提示词：{Preview(prompt, 200)}");
            if (!ok)
                return "用户拒绝生成图片。";
        }

        if (!_image.Configured)
            return "图片生成不可用：请到 ⚙ 设置 → 图像生成 里填写 CogView 的 API Key。";

        string? path = await _image.GenerateAsync(prompt, size, ct);
        if (path == null)
        {
            if (ct.IsCancellationRequested) return "图片生成已停止。";
            return "图片生成失败：接口返回异常，请检查图像 API Key/模型名，或稍后重试。";
        }

        _lastImagePath = path;
        return $"图片已生成并保存到：{path}\n提示词：{prompt}";
    }

    /// <summary>看图：调用 GLM-4.6V-Flash 多模态模型识别/分析本地图片。</summary>
    private async Task<string> VisionTool(JsonElement args, int sid, CancellationToken ct)
    {
        string? path = GetArg(args, "图片路径");
        if (string.IsNullOrWhiteSpace(path))
            return "参数错误：缺少「图片路径」。";
        if (!File.Exists(path))
            return $"图片不存在：{path}";
        string? question = GetArg(args, "问题");

        if (RunMode == "work" && ConfirmRequested != null)
        {
            bool ok = await ConfirmRequested(sid, $"看图分析：{path}");
            if (!ok)
                return "用户拒绝看图。";
        }

        if (!_vision.Configured)
            return "看图不可用：请到 ⚙ 设置 → 图像理解 里填写 GLM-4.6V 的 API Key。";

        string? result = await _vision.AnalyzeAsync(path, question, ct);
        if (result == null)
        {
            if (ct.IsCancellationRequested) return "看图已停止。";
            return "看图失败：接口返回异常，请检查图像理解 Key/模型名，或稍后重试。";
        }
        return $"图片：{path}\n识别结果：\n{result}";
    }

    /// <summary>
    /// 调用随程序分发的爬虫脚本 tools\websearch.py 搜索（多引擎，无需 API Key）。
    /// 脚本不存在 / 本机无 Python 3 / 脚本失败时返回 null，由内置爬虫兜底。
    /// </summary>
    private static async Task<List<SearchHit>?> SearchViaScriptAsync(string query, CancellationToken ct)
    {
        try
        {
            string script = Path.Combine(AppContext.BaseDirectory, "tools", "websearch.py");
            if (!File.Exists(script)) return null;
            string? py = await FindPython3Async();
            if (string.IsNullOrWhiteSpace(py)) return null;

            var psi = new ProcessStartInfo(py)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("--json");
            psi.ArgumentList.Add("--limit");
            psi.ArgumentList.Add("8");
            psi.ArgumentList.Add(query);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var outTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errTask = proc.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                if (ct.IsCancellationRequested) return null;
                return null;
            }
            string json = await outTask;
            _ = errTask;
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(json)) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string engine = root.TryGetProperty("engine", out var eng) ? eng.GetString() ?? "" : "";
            if (!root.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            var hits = new List<SearchHit>();
            foreach (var it in arr.EnumerateArray())
            {
                string title = it.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                string url = it.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                string snippet = it.TryGetProperty("snippet", out var s) ? s.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;
                hits.Add(new SearchHit { Title = title, Url = url, Snippet = snippet, Engine = engine });
            }
            return hits.Count > 0 ? hits : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>联网搜索：内置多引擎爬虫（Bing → 搜狗 → 360 → DuckDuckGo），无需 API Key。</summary>
    private async Task<string> WebSearchTool(JsonElement args, CancellationToken ct)
    {
        if (!WebSearchEnabled)
            return "联网搜索已关闭。请提醒用户打开 AI 助手页的 🌐 联网搜索开关。";
        string? q = GetArg(args, "关键词");
        if (string.IsNullOrWhiteSpace(q))
            return "参数错误：缺少「关键词」。";

        var hits = await SearchViaScriptAsync(q, ct);
        string engine = hits?.FirstOrDefault()?.Engine ?? "";
        if (hits == null || hits.Count == 0)
            (engine, hits) = await WebCrawler.SearchAsync(q, ct);
        hits ??= new List<SearchHit>();
        if (hits.Count == 0)
            return $"搜索「{q}」失败：Bing / 搜狗 / 360 / DuckDuckGo 都没能返回结果，" +
                   "可能是网络不可达或全部被风控拦截。请如实告知用户搜索失败，不要编造结果。";

        var sb = new StringBuilder($"搜索结果（{q}｜来源：{engine}）：\n");
        for (int i = 0; i < hits.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {hits[i].Title}");
            sb.AppendLine($"   {hits[i].Url}");
            if (!string.IsNullOrEmpty(hits[i].Snippet))
                sb.AppendLine($"   {Truncate(hits[i].Snippet, 160)}");
        }
        return Truncate(sb.ToString(), MaxToolOutput);
    }

    /// <summary>抓取网页：下载 URL 并提取正文文本（去标签、限长），可阅读搜索结果指向的文章。</summary>
    private async Task<string> FetchUrlTool(JsonElement args, CancellationToken ct)
    {
        string? url = GetArg(args, "网址") ?? GetArg(args, "url");
        if (string.IsNullOrWhiteSpace(url))
            return "参数错误：缺少「网址」。";
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "参数错误：网址必须以 http:// 或 https:// 开头。";
        try
        {
            string text = await WebCrawler.FetchTextAsync(url, ct, 8000);
            if (string.IsNullOrWhiteSpace(text))
                return $"抓取「{url}」成功，但未能提取到可见文本（可能是 JS 渲染页面或非 HTML 内容）。";
            return $"网页内容（{url}）：\n{text}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return "抓取网页已停止。";
        }
        catch (Exception ex)
        {
            return $"抓取「{url}」失败：{ex.Message}";
        }
    }

    private static string ReadFileTool(JsonElement args)
    {
        string? path = GetArg(args, "路径");
        if (string.IsNullOrWhiteSpace(path))
            return "参数错误：缺少「路径」。";
        if (!File.Exists(path))
            return $"文件不存在：{path}";
        string content = File.ReadAllText(path, Encoding.UTF8);
        return Truncate($"文件：{path}（{content.Length} 字符）\n\n{content}", MaxToolOutput);
    }

    private static string WriteFileTool(JsonElement args)
    {
        string? path = GetArg(args, "路径");
        string? content = GetArg(args, "内容") ?? "";
        if (string.IsNullOrWhiteSpace(path))
            return "参数错误：缺少「路径」。";
        string? wb = Sandbox.CheckWritePath(path);
        if (wb != null) return wb;
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        return $"已写入：{full}（{content.Length} 字符）";
    }

    /// <summary>
    /// 用系统默认程序打开文件。可执行 / 脚本类文件（打开即运行）不再被沙箱一刀切拦掉：
    /// work 模式先弹一次确认，boom 模式直接放行。
    /// </summary>
    private async Task<string> OpenFileTool(JsonElement args, int sid)
    {
        string? path = GetArg(args, "路径");
        if (string.IsNullOrWhiteSpace(path))
            return "参数错误：缺少「路径」。";
        if (!File.Exists(path))
            return $"文件不存在：{path}";

        string full = Path.GetFullPath(path);
        bool runsCode = Sandbox.IsExecutableLike(full);
        if (runsCode && RunMode == "work" && ConfirmRequested != null)
        {
            bool ok = await ConfirmRequested(sid,
                $"启动可执行文件（打开即运行）：{full}\n只会启动它本身，不带任何参数。");
            if (!ok)
                return $"用户拒绝：已取消启动 {full}。";
        }
        try
        {
            Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
            return runsCode ? $"已启动：{full}" : $"已用系统默认程序打开：{full}";
        }
        catch (Exception ex)
        {
            return $"打开文件失败：{ex.Message}";
        }
    }

    private static string? GetArg(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        if (args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return null;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + $"\n…（已截断，共 {s.Length} 字符）";
}

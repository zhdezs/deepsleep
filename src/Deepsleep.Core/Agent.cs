using System;
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

using TrollWrangler.CoreHost;

/// <summary>智能体消息（多轮上下文用）。Role：user / assistant / tool。</summary>
public sealed class AgentMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string Meta { get; set; } = "";
    /// <summary>可选：界面卡片用的明细（写文件时是路径 + 内容）。
    /// 只给界面看，不会饱给模型（避免把上下文撞爆）。</summary>
    public string Detail { get; set; } = "";
    /// <summary>可选：本消息附带一张本地图片（生成图片工具的结果）。</summary>
    public string? ImagePath { get; set; }
    /// <summary>可选：本消息附带一段本地视频（生成视频工具的结果）。</summary>
    public string? VideoPath { get; set; }
    /// <summary>
    /// 可选：这条消息里**给用户看的那句话**。
    /// 工具调用轮的 Content 是「人话 + 工具 JSON」的原文（模型上下文要这个），
    /// 界面只该显示剥掉 JSON 后的那部分 —— 否则要么整条被藏掉（用户看不见 AI 说了什么），
    /// 要么把一串 {"tool":...} 糊在脸上。
    /// </summary>
    public string Say { get; set; } = "";
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
    public const string ToolVideo = "生成视频";
    public const string ToolInstallExt = "安装扩展";
    public const string ToolMakeExt = "制作扩展";
    public const string ToolComputer = "操作电脑";
    public const string ToolVision = "看图";
    public const string ToolSearch = "网络搜索";
    public const string ToolResearch = "深度研究";
    public const string ToolFetch = "抓取网页";
    public const string ToolOpenFile = "打开文件";
    public const string ToolReadFile = "读取文件";
    public const string ToolWriteFile = "写入文件";
    public const string ToolRemember = "记住";
    public const string ToolForget = "删除记忆";
    /// <summary>技能不是模型调用的「工具」，但界面上按同一套卡片展示（调用链默认折叠）。</summary>
    public const string ToolSkill = "技能";
    /// <summary>
    /// 拆分任务：把一个复杂任务拆成若干子任务，交给独立的子智能体各自执行，最后汇总。
    /// 以前这能力只存在于「Agent 集群」标签页里、要用户手动配团队；现在由主助手自己决定拆不拆。
    /// </summary>
    public const string ToolSplit = "拆分任务";
    /// <summary>查 deepsleep 知识库。极速模式刻意不开放：多一次往返 + 长文本会拖慢响应。</summary>
    public const string ToolKnowledge = "查知识库";

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
    // 界面卡片里那份可以更宽（点开「运行的命令」能看到完整输出），喂给模型的那份仍然按 MaxToolOutput 截断
    private const int MaxToolDisplay = 20000;
    private const int CommandTimeoutSeconds = 600;

    private readonly ApiClient _api = new();
    private readonly ApiClient _counterLlm = new();
    private readonly OllamaClient _ollama = new();
    private readonly ImageGenClient _image = new();
    private readonly VideoGenClient _video = new();
    private readonly VisionClient _vision = new();
    private readonly Engine _counter;
    private readonly Dictionary<int, List<AgentMessage>> _sessions = new();
    private int _counterSid = 900001;
    /// <summary>已启用的 .dslt 自定义工具（由 Kernel 在启动/安装后灌进来）。</summary>
    private List<CustomTool> _customTools = new();
    /// <summary>最近一条用户输入：某些提示词片段（如扩展格式规范）要按用户意图决定是否注入。</summary>
    private string _lastUserInput = "";
    /// <summary>还能保持几轮「扩展格式规范」注入（用户连续做扩展时不会中途丢上下文）。</summary>
    private int _extHoldTurns;
    public void SetCustomTools(List<CustomTool> list) => _customTools = list ?? new();

    private string? _lastImagePath;
    private string? _lastVideoPath;
    private string? _lastToolFull;   // 本次工具输出全文：只给界面卡片用
    /// <summary>chat 模式下这一轮是否允许调用「网络搜索 / 深度研究」（用户消息带搜索/研究意图时开启）。</summary>
    private bool _chatToolMode;
    private SemaphoreSlim? _callGate;

    /// <summary>后端：auto（已配置 API 则走 API，否则 Ollama）/ api / llm / local。</summary>
    public string Backend { get; set; } = "auto";

    /// <summary>运行模式：chat（纯聊天，禁用工具）/ work（工作，执行命令需确认）/ boom（爆破，全自动）。</summary>
    public string RunMode { get; set; } = "work";

    /// <summary>初始提示词（来自 data\AGENT.md）：所有对话共享，会话里不可改，只能在 ⚙ 设置里改。</summary>
    public string CustomPrompt { get; set; } = "";

    /// <summary>是否启用网络搜索 / 深度研究工具（界面上的 🌐 开关控制）。</summary>
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

    /// <summary>
    /// deepsleep 知识库条目（内置 + 用户自建）。只在**非极速模式的决策阶段**注入目录索引，
    /// 细节由「查知识库」工具按需取回。极速模式下这份清单完全不进提示词。
    /// </summary>
    public List<KnowledgeBase.Entry> Knowledge { get; set; } = new();

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

    /// <summary>
    /// 技能被自动匹配 / 补救启用时触发（参数：会话 id、技能名、触发方式），
    /// 用于 UI 显示技能调用链。触发方式取值：自动匹配 / 补救启用 / 相关补强。
    /// </summary>
    public event Action<int, string, string>? SkillUsed;

    /// <summary>流式增量内容（参数：会话 id、增量文本），用于真·流式输出。</summary>
    public event Action<int, string>? DeltaAdded;

    /// <summary>工具开始执行时触发（参数：会话 id、工具名），UI 显示「正在…」状态。</summary>
    public event Action<int, string>? ToolStarted;

    /// <summary>
    /// 模型吐出的思考 / 推理内容（会话 id, 思考全文）。界面把它折叠在正文上方展示，
    /// 绝不混进正文 —— 以前 Responses 接口的英文思考会被当答案拼进去。
    /// </summary>
    public event Action<int, string>? ReasoningAdded;

    /// <summary>
    /// 思考内容的**流式**版本（会话 id, 累积到当前的思考全文）。
    /// 和 <see cref="ReasoningAdded"/> 的区别：那个只在整轮结束时抛一次，这个边生成边抛。
    /// </summary>
    public event Action<int, string>? ReasoningStreaming;

    private int _currentSid;

    public Agent(Engine counter) => _counter = counter;

    public void ApplyConfig(AppConfig cfg)
    {
        // 内置免费 Key（网页版同一把）：用户没填自己的 Key、且地址是智谱时自动接上。
        // 只在智谱地址上用 —— 绝不能把这把共用 Key 发到别家平台。
        string freeK = cfg.UseFreeKey ? FreeKey.Value : "";
        bool chatFree = FreeKey.Applies(cfg.UseFreeKey, cfg.ApiKey, cfg.ApiUrl);
        bool imgFree = FreeKey.Applies(cfg.UseFreeKey, cfg.ImageApiKey, cfg.ImageApiUrl);
        bool visFree = FreeKey.Applies(cfg.UseFreeKey, cfg.VisionApiKey, cfg.VisionApiUrl);

        _api.Url = cfg.ApiUrl;
        // 用免费 Key 时顺手把「从没改过的默认模型」换成免费矩阵里的那个 ——
        // 免费额度只覆盖 GLM-4.7-Flash 这一档，仍按老的默认模型发会直接报无权限。
        _api.Model = chatFree && (string.IsNullOrWhiteSpace(cfg.ApiModel) || cfg.ApiModel == "glm-4-flash-250414")
            ? FreeKey.ChatModel : cfg.ApiModel;
        _api.ApiKey = chatFree ? freeK : cfg.ApiKey;
        _api.Format = cfg.ApiFormat;
        _api.Thinking = cfg.ApiThinking;
        _api.ReasoningEffort = cfg.ReasoningEffort;
        _counterLlm.Url = cfg.CounterApiUrl;
        _counterLlm.ApiKey = cfg.CounterApiKey;
        _counterLlm.Model = cfg.CounterModel;
        _ollama.BaseUrl = cfg.OllamaUrl;
        _ollama.Model = cfg.OllamaModel;
        // 接口地址可自定义；留空就回到默认（智谱），避免用户清空输入框后一发请求就报地址为空
        _image.Url = string.IsNullOrWhiteSpace(cfg.ImageApiUrl)
            ? "https://open.bigmodel.cn/api/paas/v4/images/generations" : cfg.ImageApiUrl.Trim();
        _image.ApiKey = imgFree ? freeK : cfg.ImageApiKey;
        _image.Model = cfg.ImageModel;
        _image.SaveDir = string.IsNullOrWhiteSpace(DataDir)
            ? ""
            : System.IO.Path.Combine(DataDir, "images");
        // 视频：Key 留空就**复用图像那套**（同属智谱，不用为同一个 Key 填两遍）；
        // 地址不能跟着图像走（那个是 images/generations），留空只用视频自己的默认地址。
        _video.Url = string.IsNullOrWhiteSpace(cfg.VideoApiUrl)
            ? "https://open.bigmodel.cn/api/paas/v4/videos/generations" : cfg.VideoApiUrl.Trim();
        // 视频 Key 的优先级：自己的视频 Key → 自己的图像 Key（同属智谱时通常通用）→ 空。
        // ⚠ **绝不用内置免费 Key**：那把只覆盖聊天/看图/画图，视频接口对它一直回 1305
        //   （访问量过大 = 没额度）。硬塞过去只会让用户等十几秒再收到一句莫名其妙的报错。
        _video.ApiKey = !string.IsNullOrWhiteSpace(cfg.VideoApiKey) ? cfg.VideoApiKey.Trim()
                      : !string.IsNullOrWhiteSpace(cfg.ImageApiKey) ? cfg.ImageApiKey.Trim()
                      : "";
        _video.PollUrlTemplate = cfg.VideoPollUrl ?? "";
        _video.Model = string.IsNullOrWhiteSpace(cfg.VideoModel) ? "cogvideox-flash" : cfg.VideoModel;
        _video.SaveDir = string.IsNullOrWhiteSpace(DataDir)
            ? ""
            : System.IO.Path.Combine(DataDir, "videos");
        _vision.Url = string.IsNullOrWhiteSpace(cfg.VisionApiUrl)
            ? "https://open.bigmodel.cn/api/paas/v4/chat/completions" : cfg.VisionApiUrl.Trim();
        _vision.ApiKey = visFree ? freeK : cfg.VisionApiKey;
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

    /// <summary>一句话指挥：让指挥官模型把目标拆解成子任务，返回 JSON 计划文本（给了名册就优先复用老成员）。</summary>
    public async Task<string> PlanAsync(string instruction, string? feedback = null, string? roster = null)
    {
        string system =
            "你是 Agent 集群的指挥官。用户用一句话给出目标，你把目标拆成 1~10 个能并行执行的子任务。" +
            "只输出严格 JSON 数组，不要任何其他文字或代码块：" +
            "[{\"角色\":\"名称\",\"任务\":\"具体任务\",\"复用\":true},...]。" +
            "拆解原则：活小就少派人（1~2 个就够），活大才多派人；严禁为了凑数把不相干的角色都拉上；" +
            "每个子任务都要具体、可执行。";
        if (!string.IsNullOrWhiteSpace(roster))
            system += "\n\n这个集群的名册是整场对话共享的，能复用就复用（复用成员会保留它之前的工作记忆），别重复造人：\n" +
                      roster +
                      "\n规则：① 确实缺角色才新增；② 复用名册里的某位成员时，「角色」就写它的名字并加 \"复用\":true" +
                      "（也可以写 \"成员\":\"名字\" 明确指定）；③ 本轮用不上的成员不要列进来，他们待命就行。";
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
        _lastUserInput = userText;              // 给「要不要灌扩展格式规范」判断用
        // 做扩展往往要连着好几轮（写清单 → 写脚本 → 打包 → 修 bug），
        // 只在首轮匹配但下一轮说「继续」就没规范了，模型容易把字段写歪。
        if (WantsExtension(userText)) _extHoldTurns = 6;
        else if (_extHoldTurns > 0) _extHoldTurns--;
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
        // 搜索 / 研究意图在所有模式下都要认（深度研究不是 chat 模式的专属）
        bool userWantsSearch = history.Any(m => m.Role == "user" &&
            (IsSearchIntent(m.Content) || WantsResearch(m.Content)));
        string lastUserText = history.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        // chat（纯聊天）默认不用工具；只有这一句带搜索/研究意图时才开一个只读工具通道
        bool chatToolTurn = RunMode == "chat" && WantsSearchOrResearch(lastUserText);
        bool useDecision = RunMode != "chat" || chatToolTurn;
        for (int turn = 0; turn < MaxTurns; turn++)
        {
            if (ct.IsCancellationRequested)
            {
                LastRunStopped = true;
                return "（已停止）";
            }

            // 本地自训练模式（非 chat）：用启发式识别工具指令并直接执行，让模型也能真正干活
            if (Backend == "local" && history.Count > 0 && history[^1].Role == "user" &&
                TryLocalToolCall(history[^1].Content, out string ltool, out JsonElement largs) &&
                (RunMode != "chat" || ltool is ToolSearch or ToolResearch))
            {
                ToolStarted?.Invoke(sid, ltool);
                string lresult = await ExecuteToolAsync(ltool, largs, sid, ct);
                if (ct.IsCancellationRequested)
                {
                    LastRunStopped = true;
                    return "（已停止）";
                }
                if (ltool is ToolSearch or ToolResearch) searched = true;
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
                    if ((t.Contains("\"tool\"", StringComparison.Ordinal) &&
                         (t.Contains('{') || t.Contains('['))) ||
                        t.Contains("DSML", StringComparison.Ordinal) ||
                        t.Contains("invoke name=\"", StringComparison.Ordinal))
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
            _chatToolMode = useDecision && RunMode == "chat";
            string? reply = useDecision
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
            bool isToolCall = useDecision && TryParseToolCall(reply, out tool, out args);
            if (isToolCall) tool = NormalizeToolName(tool);
            // chat 模式只准用网络搜索 / 深度研究：别的工具直接当成"该直接回答"处理
            bool blockedToolInChat = isToolCall && !ToolAllowedInMode(tool);
            if (blockedToolInChat) isToolCall = false;
            // 决策阶段判定「不需要工具 / 已完成」→ 进入【生成最终回复】阶段：
            // 工具结果已经在上下文里，这一轮让模型用自然语言回答（流式），决策用的 JSON 不进历史也不进气泡。
            bool decisionDone = useDecision &&
                (blockedToolInChat ||
                 (isToolCall && IsFinalTool(tool)) || IsNoToolDecision(reply));
            if (decisionDone)
            {
                string keep = ExtractFinalText(reply, args);      // 模型若顺手写了答案，作为兜底
                if (LooksLikeJson(keep)) keep = "";
                isToolCall = false;
                tool = "";
                bool prevChatTool = _chatToolMode;
                _chatToolMode = false;              // 这一轮要自然语言，不再要求只输出 JSON
                string? final = await CallLlmFinalAsync(history, ct, sink, injectedSkills);
                _chatToolMode = prevChatTool;
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

            // 工具调用标记（我们的 JSON，或模型自家的 DSML）没解析成功 → 这是格式问题，不能当最终回答
            bool looksLikeToolMarkup = LooksLikeToolAttempt(reply);
            bool malformedCall = useDecision && !isToolCall && !decisionDone && looksLikeToolMarkup;
            // 最终回答里含代码块 → 顺手落盘（保证 LLM 随口给的代码也有对应文件）；
            // chat（纯聊天）模式是「不改动电脑」，不落盘。
            // 关键：提示并进正文，绝不替换正文——以前是拿"已写入…"当整条回复返回，
            // 用户就只看到"写入了文件 + 一段被截断的代码"，看起来像 AI 突然停了。
            bool codeSaved = false;
            if (!isToolCall && !malformedCall && RunMode != "chat" && !LooksLikeJson(reply))
            {
                string? note = TrySaveCodeBlock(sid, reply, history);
                if (note != null)
                {
                    reply = reply.TrimEnd() + "\n\n> 💾 " + note;
                    codeSaved = true;
                }
            }

            // 历史里保留模型原始输出（上下文需要），界面只显示 Meta 提示，避免暴露 {JSON}
            var assistantMsg = new AgentMessage
            {
                Role = "assistant",
                Content = reply,
                Meta = isToolCall ? $"调用了工具「{tool}」"
                     : malformedCall ? "工具调用格式修正中…" : "",
                // 模型在工具调用前写的那句人话（"我先看看这个文件…"）要显示出来 ——
                // 以前界面把整条带 "调用了工具" 的消息直接丢弃，用户只看得到工具卡片。
                Say = isToolCall ? ExtractToolProse(reply) : "",
            };
            history.Add(assistantMsg);
            // 格式错的内容要重发一次：把流式已经刷到界面上的原文覆盖掉
            // 格式错的内容要重发一次：把流式已经刷到界面上的原文覆盖掉；
            // 存过代码的也要重发一次：上面那张"编辑的文件"卡片会把流式气泡顶掉，
            // 不补一条正文，用户就只剩一张文件卡片、正文整段消失（就是他说的"突然变成编辑了文件"）。
            if (!streamed || malformedCall || codeSaved) MessageAdded?.Invoke(sid, assistantMsg);

            // 形似工具调用但解析失败 → 注入格式修正并重试，保证"调用了就一定会执行"
            if (malformedCall)
            {
                var corr = new AgentMessage
                {
                    Role = "tool",
                    Meta = "格式修正",
                    Content = ToolFormatRule(),
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
                return reply;
            }

            // 搜索意图拦截：用户要搜索时，第一个工具必须是「网络搜索」或「深度研究」，
            // 否则直接把模型的无关工具调用纠正回来（不执行）
            if (userWantsSearch && !searched && tool != ToolSearch && tool != ToolResearch)
            {
                var corr = new AgentMessage
                {
                    Role = "tool",
                    Meta = "工具纠正",
                    Content = $"⚠️ 用户要求的是搜索/查资料，必须用「网络搜索」或「深度研究」工具，而不是「{tool}」。请立即调用 {{\"tool\":\"网络搜索\",\"args\":{{\"关键词\":\"用户要查的内容\"}}}}（需要多轮调研、写报告时用 {{\"tool\":\"深度研究\",\"args\":{{\"主题\":\"...\"}}}}）；如果提示已关闭，请提醒用户打开 🌐 开关。",
                };
                history.Add(corr);
                MessageAdded?.Invoke(sid, corr);
                continue;
            }


            _lastImagePath = null;
            _lastVideoPath = null;
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
            if (tool is ToolSearch or ToolResearch) searched = true;
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
                Detail = tool == ToolWriteFile ? WriteFileDetail(args) : (_lastToolFull ?? ""),
                ImagePath = tool == ToolImage ? _lastImagePath : null,
                VideoPath = tool == ToolVideo ? _lastVideoPath : null,
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

    private static readonly string SystemPrompt =
        "你是一个运行在 " + Platform.DisplayName + " 电脑上的 AI 助手，能力类似 Claude Code / Codex。\n" +
        "你可以调用工具完成实际任务。当你需要工具时，**只输出一行 JSON**，不要输出任何解释，格式：\n" +
        "{\"tool\":\"工具名\",\"args\":{...}}\n\n" +
        "可用工具：\n" +
        "- 运行命令：参数 {\"命令\":\"要执行的 " + Platform.ShellName + " 命令\"}。在用户电脑上执行命令并返回输出（最长 10 分钟）。\n" +
        "- 运行Python脚本：参数 {\"代码\":\"Python 3 源码\",\"工作目录\":\"可选\"}。自动选择 Python 3 解释器，写入临时脚本并运行，返回输出与报错。\n" +
        "- 生成图片：参数 {\"提示词\":\"详细的画面描述\",\"尺寸\":\"可选 1024x1024 / 768x1344 / 1344x768\"}。调用 CogView-3-Flash 生成图片并保存到本地，返回本地图片路径。用户要求画图/生成图片/配图时使用。\n" +
        "- 生成视频：参数 {\"提示词\":\"详细的画面描述\",\"尺寸\":\"可选 1920x1080 / 1280x960 / 1024x1024\"}。调用 CogVideoX-Flash 生成一段短视频（异步任务，通常 30~120 秒），保存到本地并返回路径。用户要求生成视频/动画/动态画面时使用。\n" +
        "- 安装扩展：参数 {\"路径\":\"本地 .dslt / .dslp 文件绝对路径\"}。把用户下载的扩展包装进 deepsleep（工具包进 data\\tools，皮肤包进 data\\skins）。\n" +
        "- 制作扩展：参数 {\"类型\":\"tool 或 skin\",\"目录\":\"放清单和文件的绝对路径\",\"输出\":\"要生成的 .dslt / .dslp 绝对路径\"}。把目录打成可分发的扩展包。\n" +
        "- 操作电脑：参数 {\"动作\":\"截图|移动|点击|双击|右键|输入|按键|滚动|等待\",\"x\":0.42,\"y\":0.31,\"relative\":true,\"text\":\"要输入的文字\",\"key\":\"enter 或 ctrl+c\",\"amount\":3}。执行完自动截图并读屏，返回界面描述与可点元素坐标，用来看一步走一步（网页点按钮、操作别的软件、填表单）。\n" +
        "- 看图：参数 {\"图片路径\":\"本地图片绝对路径\",\"问题\":\"可选，要问的问题\"}。调用 GLM-4.6V-Flash 多模态模型识别/分析图片（描述内容、读图、检查截图、审查生成的图片等）。\n" +
        "- 网络搜索：参数 {\"关键词\":\"搜索词\"}。用内置爬虫直接抓取搜索结果页（Bing → 搜狗 → 360 → DuckDuckGo 依次尝试），返回标题/链接/摘要，无需 API Key；搜索/查价格/查资料/找最新信息时用它。\n" +
        "- 深度研究：参数 {\"主题\":\"研究主题\",\"深度\":1-3}。多轮子查询搜索 + 抓取多个网页正文 + 汇总成一份带来源的 Markdown 研究报告（自动保存到本地，返回文件路径）；用户说「深度研究 / 调研 / 全面了解 / 整理一份报告」时用它。\n" +
        "- 抓取网页：参数 {\"网址\":\"https://...\"}。用内置爬虫下载网页并提取正文文本（自动去标签、限长），用于阅读搜索结果指向的文章、新闻、价格页等。\n" +
        "- 打开文件：参数 {\"路径\":\"绝对路径\"}。用系统默认程序打开本地文件（文档/图片/网页/媒体等）；" +
        "可执行文件/脚本也能打开（等于把它运行起来），work 模式下系统会先找用户确认，别自作主张反复调用。\n" +
        "- 读取文件：参数 {\"路径\":\"绝对路径\"}。读取文本文件内容。\n" +
        "- 写入文件：参数 {\"路径\":\"绝对路径\",\"内容\":\"要写入的内容\"}。写入或覆盖文本文件。\n" +
        "- 记住：参数 {\"内容\":\"要记住的事实或用户偏好\"}。把重要信息写入长期记忆（跨会话有效），之后任何对话都能用到。\n" +
        "- 删除记忆：参数 {\"序号\":1}。删除系统提示词【长期记忆】里对应序号的一条记忆。\n" +
        "- 拆分任务：参数 {\"tasks\":[{\"role\":\"角色名（如 前端开发）\",\"task\":\"这一路要独立完成的具体任务\"}]}。" +
        "把一个复杂任务拆成 2~5 个子任务，交给各自独立的子智能体**并行**执行，最后把各路的产出汇总回给你。" +
        "适合「需要多方向同时推进」「可以分成互相独立的几块」的活（比如同时写前后端、同时调研几个方向、同时生成多份材料）。" +
        "拆出来的每个子任务要**自包含**：说清楚目标、要给什么产物、放到哪个目录，因为子智能体看不到你和用户的上下文。" +
        "子智能体不能反过来问你，所以别把需要用户拍板的事情拆出去。\n\n" +
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
        "10. 用户要求搜索、查价格、查资料、找最新信息时，必须用「网络搜索」工具（需要系统调研、写报告时用「深度研究」）；搜到链接后如需原文，用「抓取网页」读取；严禁用 curl / wget / Invoke-WebRequest 等命令行代替（会触发拦截并导致失败）。\n" +
        "11. 工具选择指南：搜索/查价格/查资料/找最新信息→「网络搜索」；要系统调研/写报告→「深度研究」；阅读搜索结果的具体页面→「抓取网页」；执行命令/系统操作→「运行命令」；写代码跑脚本→「运行Python脚本」或「写入文件」；生成图片→「生成图片」；生成视频/动画→「生成视频」；看图片→「看图」；读写文件→「读取文件/写入文件」；用默认程序打开文件→「打开文件」；任务可以拆成几块互相独立的活→「拆分任务」。\n" +
        "12. 最终回答必须是纯文本中文，禁止输出任何 JSON、代码块或结构化片段。\n" +
        "13. 用户提到重要事实、偏好、要求（比如称呼、习惯、项目约定、不想要什么）时，用「记住」工具保存；保存后告知用户已记住。\n" +
        "14. 当【已安装技能】里出现与你任务匹配的技能时，必须真正执行技能：按说明调用「运行Python脚本」「写入文件」「运行命令」「生成图片」等工具，把产物（PPT/文档/网页/脚本等）真实生成到磁盘，并在最终回复里给出完整本地路径；严禁只讲解步骤、只描述怎么做、或假装已完成。" +
        "15. 思考 / 推理过程也必须用简体中文，禁止用英文思考；思考内容界面会单独折叠展示，不要写进正文。";

    /// <summary>
    /// 工具决策阶段的协议：软件先发这条指令，模型这一轮**只准输出 JSON**，
    /// 正文放到「完成」的 回答 字段里；由软件解析、执行工具、把结果塞回上下文，再让模型继续干活。
    /// </summary>
    /// <summary>用户是不是要做扩展（工具/皮肤）。命中就把完整格式规范灌进系统提示词。</summary>
    private static readonly Regex ExtensionIntent = new(
        // .dslt/.dslp 直呼其名；或者「做一个…工具/皮肤」这类意图（光写"工具"太宽，会误伤"工具调用"）
        @"\.dslt|\.dslp|扩展包|工具包|自定义(的)?工具|皮肤|主题包|换肤|美化(界面|一下)?|动效|" +
        @"(做|写|造|加|开发|制作|来个|弄个)[\s\S]{0,20}(工具|插件|皮肤|主题)|skin|theme",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool WantsExtension(string? text)
        => !string.IsNullOrWhiteSpace(text) && ExtensionIntent.IsMatch(text!);

    /// <summary>
    /// 扩展创作规范。模型不可能凭空知道 .dslt / .dslp 里该写什么，
    /// 所以用户一提「做个工具 / 换皮肤」，这段就整段灌进去（保持若干轮，避免说「继续」就丢）。
    /// </summary>
    private const string ExtensionsPrompt =

        "\n\n【扩展制作规范 · 本软件支持两种扩展，都是 zip 包，根目录放清单】\n" +
        "A) .dslt 自定义工具（装好后你能直接调用它）\n" +
        "   manifest.json 必填：id（小写字母/数字/-/_）、name、entry（入口脚本相对路径）、runtime（python|cmd|powershell|node）；\n" +
        "   可选：version、author、description（description 会展示给用户，也进你的工具清单）。\n" +
        "   脚本约定：在包目录里运行；输入 = 第 1 个命令行参数，同时也放在环境变量 DEEPSLEEP_TOOL_INPUT；\n" +
        "   stdout 原样作为结果返回；退出码非 0 视为失败；超时 5 分钟；DEEPSLEEP_DATA 指向 data 目录。\n" +
        "   示例 manifest.json：{\"id\":\"sci-calc\",\"name\":\"科学计算器\",\"version\":\"1.0.0\",\"author\":\"me\",\"description\":\"算数学表达式\",\"entry\":\"main.py\",\"runtime\":\"python\"}\n" +
        "B) .dslp 皮肤（只改外观，不执行代码）\n" +
        "   theme.json：id、name、base（light|dark，只是建议搭配的模式，不会强制切主题）、vars（CSS 变量表）、css（可选，相对路径）、wallpaper（可选）、version、author、description。\n" +
        "   注意：亮/暗模式各有一套固有 CSS（ui/theme-dark.css 与 theme-light.css），皮肤只是叠加层，不会替换它们；不要写 defaultFor / builtin 字段（已废弃）。\n" +
        "   可覆盖变量：--bg --panel --side --text --muted --border --bubble --self --self-text --input --hover --sel --card --code --shadow --chipbg --chipon --ring --radius\n" +
        "   变量必须挂在 body.skin-on 作用域下才能压过主题类；css 会被原样注入页面，动效写 @keyframes 再挂到：.row .bubble .bubble.tool .bubble.thinking .avatar .row.self .composer\n" +
        "做法（必须按这三步，不要只把内容贴在聊天里）：\n" +
        "1) 「写入文件」把清单和脚本写到同一个目录（例如 D:\\ds\\my-tool\\manifest.json 与 main.py）\n" +
        "2) 「制作扩展」{\"类型\":\"tool\",\"目录\":\"D:\\ds\\my-tool\",\"输出\":\"D:\\ds\\my-tool.dslt\"}（皮肤类型写 skin，扩展名 .dslp）\n" +
        "3) 「安装扩展」{\"路径\":\"D:\\ds\\my-tool.dslt\"}，装完让用户重新开一个对话即可使用\n" +
        "完成后告诉用户：⚙ 设置 → 🧩 扩展 里能看到并启用；工具装好后你能直接按名字调用。\n" +
        "安全：工具是本机脚本，别写删除/上传私人文件之类的危险操作；皮肤只能改 CSS。\n";

    private const string ToolDecisionPrompt =
        "\n\n【工具决策阶段 · 先说一句话，再输出 JSON】\n" +
        "整体流程：用户输入 → 你分析意图 → 需要工具就输出 tool_call → 软件执行工具 → 结果回传给你 → 你继续决策，直到不再需要工具。\n" +
        "合法输出只有两种：\n" +
        "① 需要调用工具：**先用一行中文说明你接下来要做什么**（例如「我先读一下这个文件」「我来搜一下最新资料」——这句会**实时显示给用户**，让他知道你在干嘛），然后空一行，再输出工具 JSON：{\"tool_call\":{\"name\":\"工具名\",\"arguments\":{...}}}\n" +
        "② 不需要任何工具 / 已经拿到足够结果：{\"tool_call\":null}\n" +
        "要点：\n" +
        "· 那句说明要**简短**（一句话以内），详细分析留到最后的「完成」阶段；\n" +
        "· 需要动手的任务（读写文件、跑命令、跑脚本、搜索、抓网页、画图、看图、装技能）必须先走 ①，不许直接说「已完成」；\n" +
        "· 代码、命令、文件内容都放进 arguments 字段，字符串内换行写 \\n、双引号写 \\\"；\n" +
        "· 严禁在没收到工具结果前宣称「已运行 / 已完成」。";

    /// <summary>阶段三协议：工具结果已回传，这一轮只负责用自然语言写最终回复。</summary>
    private const string FinalAnswerPrompt =
        "\n\n【最终回复阶段】工具执行结果已经在上面的对话里了，这一轮请直接用自然语言回答用户：\n" +
        "不要输出 JSON、不要调用工具、不要用代码块包住整段回答；简洁、专业、说人话。\n" +
        "思考过程用中文；不要在回答里复述思考过程。\n" +
        "如果工具失败了，如实说明失败原因与下一步建议，不要假装成功。";

    private const string ChatSystemPrompt =
        "你是一个运行在 Windows 电脑上的 AI 助手（聊天模式）。\n" +
        "当前模式不能执行命令、不能读写本地文件。\n" +
        "请直接以中文回答用户的问题（思考过程也用中文），简洁、清楚、友好，不要输出 JSON 或提及工具。";

    /// <summary>chat 模式下这一轮带搜索/研究意图时的提示词：只放开「网络搜索 / 深度研究」两个只读工具。</summary>
    private const string ChatToolSystemPrompt =
        "你是一个运行在 Windows 电脑上的 AI 助手（聊天模式）。这一轮你有两个只读工具可用：\n" +
        "- 网络搜索：参数 {\"关键词\":\"搜索词\"}。抓取搜索引擎结果页（Bing → 搜狗 → 360 → DuckDuckGo），返回标题/链接/摘要，快；适合查最新消息、价格、事实核对。\n" +
        "- 深度研究：参数 {\"主题\":\"研究主题\",\"深度\":1-3}。多轮搜索 + 抓取多个网页正文 + 汇总结论，最后产出一份带来源的 Markdown 研究报告并保存到本地；适合「研究一下 / 调研 / 全面了解」类请求。\n" +
        "输出规则：\n" +
        "1. 需要工具时**只输出一行 JSON**：{\"tool_call\":{\"name\":\"网络搜索\",\"arguments\":{\"关键词\":\"...\"}}}；不要解释、不要用代码块包住 JSON。\n" +
        "2. 不需要工具（闲聊、常识、创作、翻译、写代码等）时只输出：{\"tool_call\":null}，随后软件会请你用自然语言回答。\n" +
        "3. 涉及最新消息、实时数据、价格、人物近况时必须先调工具，不许凭记忆编造。\n" +
        "4. 这一轮只输出 JSON 本身，不要写正文。";

    /// <summary>系统提示词 = 初始提示词（AGENT.md，放最前面）+ 各模式自己的提示词。</summary>
    /// <param name="phase">decision=工具决策阶段 / final=最终回复阶段 / 空=普通对话。
    /// 极速模式靠它决定要不要带阶段性约束（最终回复阶段绝不能再说「只输出 JSON」）。</param>
    private string SystemPromptForMode(string phase = "")
    {
        string prompt = BuildModePrompt(phase);
        string custom = (CustomPrompt ?? "").Trim();
        if (custom.Length > 0)
            prompt = "【初始提示词 · AGENT.md（所有对话共享，只能在 ⚙ 设置里修改，优先级最高）】\n" +
                     custom + "\n\n" + prompt;
        return prompt;
    }

    /// <summary>
    /// 极速模式只压缩「人设与行为约束」，**不压缩能力**。
    /// 工具参数、自定义工具清单、扩展规范、长期记忆统一由 <see cref="CommonTail"/> 追加。
    /// 早期版本在这里提前 return，把参数格式全砍了，模型不知道 arguments 该填什么，
    /// 干脆输出 {"tool_call":null} 用文字敷衍 —— 表现就是「极速模式不会调用工具」。
    /// 也不再自己规定输出格式：格式一律交给 ToolDecisionPrompt，避免两条规则打架。
    /// </summary>
    private static string FastModePrompt(string phase)
    {
        if (phase == "final")
            return "你是运行在 Windows 上的 AI 助手，用中文回答，简洁、准确、说人话。";

        return
            "你是运行在 Windows 上的 AI 助手（类似 Claude Code/Codex），用工具把事情真正做完，不要只给建议或复述做法。\n" +
            "需要动手的任务（读写文件、跑命令/脚本、搜索查资料、抓网页、画图、看图、操作别的软件界面、" +
            "安装或制作扩展、记住信息）**必须先调用对应工具**，拿到工具结果后才能给最终答复；" +
            "严禁在没收到工具结果前说「已完成 / 已生成 / 已保存」。\n" +
            "选工具：搜索/查价格/查最新消息→网络搜索（禁止用 curl 等命令代替）；系统调研/写报告→深度研究；" +
            "读搜索结果原文→抓取网页；写代码/落盘→写入文件 或 运行Python脚本；画图→生成图片；生成视频→生成视频；" +
            "看图→看图；点按钮/填表单/操作别的软件→操作电脑；做 .dslt 工具或 .dslp 皮肤→制作扩展 + 安装扩展；" +
            "用户安装的自定义工具按下面清单直接按名字调用。\n" +
            "失败/超时/停止时如实说明，禁止假装完成、乱调无关工具或用通用话术敷衍。\n" +
            "用户提供重要事实/偏好时用「记住」保存，用户要求删除某条记忆时用「删除记忆」。\n" +
            "极速模式不查阅知识库：关于 deepsleep 自身规格的问题，按当前提示词里已有的信息作答；需要完整规范时提示用户关闭 ⚡ 极速模式。";
    }

    private string BuildModePrompt(string phase)
    {
        string prompt = (FastMode && RunMode != "chat")
            ? FastModePrompt(phase)
            : RunMode switch
            {
                "chat" => _chatToolMode ? ChatToolSystemPrompt : ChatSystemPrompt,
                "boom" => SystemPrompt + "\n\n当前是 boom（爆破）模式：你可以直接执行任何命令和文件操作，无需请求用户确认。",
                _ => SystemPrompt + "\n\n当前是 work（工作）模式：执行「运行命令」前系统会自动请求用户确认，确认结果会以工具结果返回给你；其余工具可直接使用。",
            };
        return prompt + CommonTail(phase);
    }

    /// <summary>极速与普通模式共用的能力清单：工具参数、附加提示词、自定义工具、扩展规范、多模态、长期记忆。</summary>
    private string CommonTail(string phase)
    {
        // 极速模式的最终回复阶段已经不能调工具了，工具清单/扩展规范就不必再占 token；
        // 附加提示词与长期记忆要留着，否则回答会丢人设、丢事实。普通模式一律保持全量。
        bool slimFinal = FastMode && phase == "final";
        var sb = new StringBuilder();
        if (RunMode != "chat" && !slimFinal)
        {
            sb.Append("\n- 网络搜索：参数 {\"关键词\":\"搜索词\"}。由内置爬虫脚本直接抓取搜索结果页（Bing → 搜狗 → 360 → DuckDuckGo 依次尝试），返回标题/链接/摘要，无需 API Key。用户要求搜索、查价格、查资料、找最新信息时优先使用；若工具提示已关闭，请提醒用户打开 🌐 开关，不要用命令行代替。");
            sb.Append("\n- 深度研究：参数 {\"主题\":\"研究主题\",\"深度\":1-3}。多轮子查询搜索 + 抓取多篇网页正文 + 汇总成一份 Markdown 研究报告，自动保存到本地并返回路径；深度越大查得越广（1 快 / 2 标准 / 3 全面）。适合「研究一下 / 调研 / 全面了解 / 整理一份资料」。跑得比较久，开始前可先跟用户说一句预计要花点时间。");
            sb.Append("\n- 抓取网页：参数 {\"网址\":\"https://...\"}。用内置爬虫抓取网页正文（自动去标签、限长 8000 字）。搜索得到链接后需要看原文时使用；若网页抓不到正文，如实说明，不要假装成功。");
            sb.Append("\n- 打开文件：参数 {\"路径\":\"绝对路径\"}。用系统默认程序打开本地文件；" +
                      "可执行文件/脚本也允许打开（打开就是运行它，work 模式下会先请用户确认）。");
        }
        if (!string.IsNullOrWhiteSpace(ExtraPrompt))
            sb.Append("\n\n").Append(ExtraPrompt);
        if (!slimFinal)
        {
            // 用户装的 .dslt 自定义工具：模型得知道有这些工具、怎么调，否则永远用不上
            if (_customTools.Count > 0)
            {
                sb.Append("\n\n【自定义工具（用户安装的，和内置工具一样直接调）】\n");
                foreach (var t in _customTools) sb.AppendLine(t.PromptLine);
                sb.AppendLine("调用格式：{\"tool_call\":{\"name\":\"工具名\",\"arguments\":{\"输入\":\"要处理的内容\"}}}。" +
                              "工具在它自己的目录里运行，输入同时会通过环境变量 DEEPSLEEP_TOOL_INPUT 传入。");
            }
            // 扩展创作：用户要做工具/皮肤时把格式规范整段灌进去 ——
            // 模型不可能凭空知道 .dslt 里该写什么，不灌就只能瞎编字段。
            if (_extHoldTurns > 0)
                sb.Append(ExtensionsPrompt);
            else
                sb.Append("\n\n【扩展】本软件支持 .dslt 自定义工具与 .dslp 皮肤包；用户要做这类东西时，会收到完整格式规范。");

            // 知识库：**极速模式不挂**（多一次往返 + 长章节会拖慢，就不极速了），
            // 最终回复阶段也不挂（那时已不该再调工具），chat 纯聊天模式不挂。
            if (!FastMode && phase != "final" && RunMode != "chat" && Knowledge.Count > 0)
                sb.Append(KnowledgeBase.Index(Knowledge));
        }

        if (MultimodalMain)
            sb.Append("\n\n注意：主模型是多模态模型，用户上传的图片会作为多模态输入直接发给你。看到图片直接描述/分析，严禁调用「看图」工具。");
        string mem = Memory?.AllText() ?? "";
        if (!string.IsNullOrWhiteSpace(mem))
            sb.Append("\n\n【长期记忆】以下是你跨会话保存的关于用户/任务的事实与偏好（序号即「删除记忆」工具的序号）：\n" +
                      mem +
                      "\n\n规则：回答与这些记忆相关时直接使用；用户提供了新的重要事实/偏好时调用「记住」保存；用户说某条不再需要时调用「删除记忆」。");
        return sb.ToString();
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
        string sys = SystemPromptForMode("decision") + BuildSkillsPrompt(history, injectedSkills) + ToolDecisionPrompt;
        // 决策这一轮也会被 max_tokens 截断（模型一口气把整个文件内容塞进参数里就会）：
        // 截断后 JSON 只剩半截、工具根本执行不了，所以这里同样自动续写拼回完整调用。
        return await CallOnceAutoContinueAsync(sys, msgs, ct, null, emitReasoning: true);
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
        string sys = SystemPromptForMode("final") + BuildSkillsPrompt(history, injectedSkills) + FinalAnswerPrompt;
        return await CallOnceAutoContinueAsync(sys, msgs, ct, onDelta, emitReasoning: true);
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
        return await CallOnceAutoContinueAsync(SystemPromptForMode() + BuildSkillsPrompt(history, injectedSkills),
                                               msgs, ct, onDelta, emitReasoning: true);
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
            // ⚠ 只在用户**明确点名**这个技能时才注入完整说明。
            //   不再做关键词 / 词元 / 别名 / 二字重叠之类的「自动匹配」——那套经常误判，
            //   用户根本没提也被硬塞一大段技能说明，还动不动触发「补救启用」重试，很烦。
            bool hit = (injected != null && injected.Contains(s.Name)) ||
                       (!string.IsNullOrWhiteSpace(user) &&
                        user.Contains(s.Name, StringComparison.OrdinalIgnoreCase));
            sb.AppendLine($"- {s.Name}：{s.Description}");
            if (hit)
            {
                injected?.Add(s.Name);
                SkillUsed?.Invoke(_currentSid, s.Name, "点名启用");
                Log($"技能启用：{s.Name}");
                anyContent = true;
                sb.AppendLine($"\n【技能「{s.Name}」使用说明】\n{Truncate(s.Content, 4000)}");
                if (s.Files.Count > 0)
                    sb.AppendLine($"\n技能目录包含文件（需要更详细说明时用「读取文件」查看）：{string.Join("、", s.Files.Take(12))}");
            }
        }
        if (!anyContent)
            sb.AppendLine("\n当用户明确要求使用某个已安装技能时，按对应说明执行；没有点名时忽略本段。");
        return sb.ToString();
    }

    /// <summary>被输出长度上限截断时最多自动续写几次（用户不用自己敲「继续」）。</summary>
    private const int MaxContinuations = 4;

    private const string ContinueHint =
        "【续写】你上一条回复因为长度上限被截断了。请直接从断掉的地方接着往下写，" +
        "不要重复已经写过的内容、不要重新开头、不要加任何解释或前后缀。";

    /// <summary>
    /// 调一次 LLM；如果回复因为 max_tokens 被截断（finish_reason=length），
    /// 自动把已写内容回灌并让它接着写，直到写完或到次数上限。
    /// 以前被截断就停在那儿，用户得手动说「继续」——现在软件自己续。
    /// </summary>
    private async Task<string?> CallOnceAutoContinueAsync(string system,
        List<(string Role, string Content)> msgs, CancellationToken ct, Action<string>? onDelta,
        bool emitReasoning = false)
    {
        string? text = await CallOnceAsync(system, msgs, ct, onDelta, emitReasoning);
        for (int i = 0; i < MaxContinuations; i++)
        {
            if (string.IsNullOrEmpty(text) || ct.IsCancellationRequested || !_api.LastTruncated) break;
            Log($"回复被长度上限截断，自动续写第 {i + 1} 次（已 {text.Length} 字）");
            msgs.Add(("assistant", text));
            msgs.Add(("user", ContinueHint));
            string? more = await CallOnceAsync(system, msgs, ct, onDelta, emitReasoning);
            if (string.IsNullOrWhiteSpace(more)) { Log("续写没有返回内容，停止续写"); break; }
            more = TrimOverlap(text, more);
            if (more.Length == 0) { Log("续写内容与已写部分重复，停止续写"); break; }
            text += more;
        }
        return text;
    }

    /// <summary>
    /// 续写时模型有时会把上一段的结尾重复一遍，这里把重复的部分削掉再拼，避免回答里出现重复段落。
    /// 只削「已写内容的结尾 == 续写内容的开头」这种明确重叠（至少 20 字才认）。
    /// </summary>
    private static string TrimOverlap(string already, string more)
    {
        int max = Math.Min(already.Length, more.Length);
        for (int len = max; len >= 20; len--)
            if (already.AsSpan(already.Length - len).SequenceEqual(more.AsSpan(0, len)))
                return more[len..];
        return more;
    }

    /// <summary>单次 LLM 调用（不跑工具循环）：按后端选择 API / Ollama，429 自动退避重试。</summary>
    private async Task<string?> CallOnceAsync(string system,
        List<(string Role, string Content)> msgs, CancellationToken ct, Action<string>? onDelta,
        bool emitReasoning = false)
    {
        if (Backend == "local")
        {
            _api.LastTruncated = false;
            string user = msgs.Count > 0 ? msgs[^1].Content : "";
            string reply = LocalNormalReply(user);
            Log($"本地自训练 回复长度={reply.Length}");
            return reply;
        }
        if (Backend == "llm")
        {
            _api.LastTruncated = false;
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
                    // 思考内容也走流式：边生成边推给界面，而不是等整轮结束才一次性出现
                    reply = await _api.ChatStreamAsync(system, msgs, sink, ct: ct,
                        onReasoning: emitReasoning
                            ? t => ReasoningStreaming?.Invoke(_currentSid, t)
                            : null);
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
                if (!string.IsNullOrEmpty(reply))
                {
                    // 思考 / 推理内容单独抛给界面折叠展示（以前被当成正文拼进去，用户就看见一大段英文）
                    if (emitReasoning && !string.IsNullOrEmpty(_api.Reasoning))
                        ReasoningAdded?.Invoke(_currentSid, _api.Reasoning);
                    return reply;
                }
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

    /// <summary>兜底落盘：LLM 直接在回答里吐代码块时，把代码写入工作区文件。
    /// 返回一句「存到哪儿了」的提示（调用方附在正文后面），**不替换正文**——
    /// 以前是拿它当整条回复返回，用户就只看到"写入了文件 + 一段被截断的代码"，像是突然停了。</summary>
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
        history.Add(new AgentMessage
        {
            Role = "tool", Content = result, Meta = ToolWriteFile,
            Detail = "路径：" + path + "\n" + Truncate(code, MaxToolOutput),
        });
        TrimHistory(history);
        MessageAdded?.Invoke(sid, history[^1]);
        return $"已把上面的代码存成文件：{path}（{code.Length} 字符）。要改的话直接说文件名。";
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
        if (t.Contains("深度研究", StringComparison.Ordinal) || t.Contains("深入研究", StringComparison.Ordinal) ||
            t.Contains("调研", StringComparison.Ordinal) || t.StartsWith("研究一下", StringComparison.Ordinal) ||
            t.StartsWith("帮我研究", StringComparison.Ordinal))
        {
            string topic = Regex.Replace(t,
                @"^(深度研究|深入研究|研究一下|帮我研究|调研|帮我调研)\s*[:：]?\s*", "");
            if (topic.Length > 0)
            {
                tool = ToolResearch;
                args = Obj(("主题", topic));
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

    /// <summary>
    /// 把模型输出里的「工具调用 JSON / DSML 标记」剥掉，剩下的就是**调用工具前它说的那句话**。
    /// 纯 JSON 的决策轮会得到空字符串（那种本来也没人话可显示）。
    /// </summary>
    private static string ExtractToolProse(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return "";
        string s = reply;

        // ① 找到第一个能解析成工具调用的 {...}，把这一段整体删掉
        int idx = s.IndexOf('{');
        while (idx >= 0)
        {
            int end = FindJsonEnd(s, idx);
            if (end > idx && TryParseToolObject(s.Substring(idx, end - idx), out _, out _))
            {
                s = (s[..idx] + " " + s[end..]).Trim();
                break;
            }
            idx = s.IndexOf('{', idx + 1);
        }

        // ② 去掉模型自家标记（DSML / invoke / parameter）
        s = Regex.Replace(s, "<[^>]*DSML[^>]*>", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "</?invoke\\b[^>]*>", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "</?parameter\\b[^>]*>", " ", RegexOptions.IgnoreCase);
        // ③ 去掉残留的代码围栏与多余空白
        s = Regex.Replace(s, "```[a-zA-Z]*", " ").Replace("```", " ");
        s = Regex.Replace(s, @"[ \t]{2,}", " ");
        s = Regex.Replace(s, @"\n{3,}", "\n\n").Trim();

        // 只剩标点 / 空白 → 不算人话
        if (s.Length == 0) return "";
        foreach (char c in s)
            if (!char.IsPunctuation(c) && !char.IsWhiteSpace(c) && !char.IsSymbol(c)) return s;
        return "";
    }

    private static bool TryParseToolCall(string reply, out string tool, out JsonElement args)
    {        tool = "";
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

        // ④ 模型自家的工具标记（DeepSeek 系会吐 …DSML… invoke name="运行命令" …参数 JSON…）
        //    模型偶尔不按我们的 JSON 协议走，直接吐自家标记：以前这段会被原样显示成一堆尖括号，
        //    工具也不会执行——用户看到的"AI 说了一段奇怪的话就停了"就是它。这里直接解析成工具调用。
        var dm = Regex.Match(reply ?? "", "invoke\\s+name=\"([^\"]+)\"");
        if (dm.Success)
        {
            string markName = dm.Groups[1].Value.Trim();
            var pm = Regex.Match(reply ?? "", "parameter\\s+name=\"(?:arguments|args)\"[^>]*>([\\s\\S]*?)</");
            if (pm.Success)
            {
                try
                {
                    using var d2 = JsonDocument.Parse(pm.Groups[1].Value.Trim());
                    tool = markName;
                    args = d2.RootElement.Clone();
                    return true;
                }
                catch { /* 参数不是合法 JSON → 只认工具名，让工具回一句参数错误提示模型重试 */ }
            }
            tool = markName;
            return true;
        }

        // ⑤ 只抠出工具名（args 为空 → 工具返回参数错误，模型会据此修正重试）
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

    /// <summary>
    /// 工具调用格式规则：既用于系统提示，也用于「格式修正」重试。
    /// <para>
    /// 必须把**合法工具名逐个列出来** —— 小模型编不出名字时会自造
    /// <c>write_to_file</c> 这种训练里见过的英文名。虽然 <see cref="NormalizeToolName"/>
    /// 已经能兜住绝大多数别名，但直接告诉它正确名字，比事后纠正少绕一圈。
    /// </para>
    /// </summary>
    private string ToolFormatRule()
    {
        // 极速模式连「查知识库」这个名字都不报给模型，避免它白跑一次被拦的调用
        var names = FastMode ? ToolNames.Where(n => n != ToolKnowledge) : ToolNames;
        return "你的工具调用格式错误：只能输出合法 JSON，不要输出解释，不要包在 ``` 代码块里。\n" +
               "调用工具：{\"tool_call\":{\"name\":\"工具名\",\"arguments\":{...}}}\n" +
               "不需要工具：{\"tool_call\":null}\n" +
               "可用工具名（必须原样使用这几个中文名，不要用 write_to_file 等英文名）：" +
               string.Join("、", names) + "\n" +
               "参数键固定为 name + arguments；字符串里的换行写 \\n，双引号写 \\\"。\n" +
               "如果要写入的文件很长（超过约 3000 字），先写一部分，再继续调用同样的工具追加剩余内容，" +
               "不要试图一次输出完导致 JSON 被截断。";
    }

    /// <summary>本应用提供的工具名（格式修正提示里报给模型，避免它自己编名字）。</summary>
    public static readonly string[] ToolNames =    {
        ToolRunCommand, ToolPython, ToolWriteFile, ToolReadFile, ToolOpenFile,
        ToolSearch, ToolResearch, ToolFetch, ToolImage, ToolVideo, ToolVision,
        ToolRemember, ToolForget, ToolInstallExt, ToolMakeExt, ToolComputer,
        ToolSplit, ToolKnowledge,
    };

    /// <summary>
    /// 「拆分任务」的执行器（由内核注入）：参数是工具调用的原始 JSON，返回汇总结论文本。
    /// Agent 自己不持有子智能体，用回调注入是为了让内核独占编排逻辑。
    /// </summary>
    public Func<string, CancellationToken, Task<string>>? SplitRunner;

    /// <summary>
    /// 判断这段输出「像是要调工具」但没能解析成功。
    /// <para>
    /// 为什么不能只查 <c>"tool"</c>：小模型的失败姿势五花八门，最常见的是
    /// <c>{"tool_call":{"name":"write_to_file",...}}</c>（写的是 tool_call 而不是 tool），
    /// 或者干脆 <c>{"name":"write_to_file","arguments":{...}}</c>；
    /// 再常见的是 JSON 被输出长度上限截断、根本闭合不了。
    /// 这些以前全都判不出来 → 落到「当普通文本显示」→ 用户看到模型说了一大段
    /// 「我现在调用工具写入文件」，然后一个文件都没生成。
    /// </para>
    /// </summary>
    private static bool LooksLikeToolAttempt(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return false;
        string r = reply;

        if (r.Contains("\"tool\"", StringComparison.Ordinal)) return true;
        if (r.Contains("tool_call", StringComparison.OrdinalIgnoreCase)) return true;
        if (r.Contains("function_call", StringComparison.OrdinalIgnoreCase)) return true;
        if (r.Contains("invoke name=\"", StringComparison.Ordinal)) return true;
        if (r.Contains("DSML", StringComparison.Ordinal)) return true;
        if (r.Contains("<|tool", StringComparison.Ordinal)) return true;   // OpenAI 原生 function calling 标记

        // 裸的 {"name":"write_to_file","arguments":{…}}：有参数键 + 认识的工具名（含英文别名）
        bool hasArgsKey = r.Contains("\"arguments\"", StringComparison.OrdinalIgnoreCase) ||
                          r.Contains("\"args\"", StringComparison.OrdinalIgnoreCase) ||
                          r.Contains("\"parameters\"", StringComparison.OrdinalIgnoreCase);
        if (!hasArgsKey) return false;

        foreach (Match m in Regex.Matches(r, "\"name\"\\s*:\\s*\"([^\"]+)\""))
        {
            string raw = m.Groups[1].Value.Trim();
            if (ToolNames.Contains(raw)) return true;            // 本来就是我们定义的名字
            if (NormalizeToolName(raw) != raw) return true;      // 命中英文/中文别名表
        }
        return false;
    }

    /// <summary>
    /// 把模型可能输出的工具别名归一成正式工具名。
    /// <para>
    /// ⚠ 这条极其关键：小模型（尤其本地 9B）几乎不会照着中文工具名来，它们按训练时见过的
    /// 英文习惯输出 <c>write_to_file</c> / <c>run_command</c> / <c>read_file</c>。
    /// 以前这里只映射了搜索/研究两个别名，于是「工具名不认识」→ 工具不执行 →
    /// 用户看到模型说了一大段「我现在将调用工具写入文件」，然后什么都文件没生成。
    /// </para>
    /// </summary>
    internal static string NormalizeToolName(string tool)
    {
        string t = (tool ?? "").Trim();
        if (t.Length == 0) return t;

        // 先按「驼峰拆词 + 分隔符统一成下划线 + 全小写」做一次宽松匹配，
        // 兼容 writeToFile / write-to-file / Write To File / WRITE_FILE 各种写法
        string key = Regex.Replace(t, "([a-z0-9])([A-Z])", "$1_$2");
        key = Regex.Replace(key.ToLowerInvariant().Replace('-', '_').Replace(' ', '_'), @"_+", "_").Trim('_');

        string? mapped = key switch
        {
            // 写入文件
            "write_to_file" or "write_file" or "writefile" or "write" or "save_file" or "savefile"
                or "save" or "create_file" or "createfile" or "file_write" or "filewrite"
                or "output_file" or "写入" or "写文件" or "保存文件" or "创建文件" => ToolWriteFile,

            // 读取文件
            "read_file" or "readfile" or "read" or "open_and_read" or "cat" or "view_file"
                or "读取" or "读文件" or "查看文件" => ToolReadFile,

            // 打开文件（交给系统默认程序）
            "open_file" or "openfile" or "open" or "launch_file" or "打开" or "打开文件" => ToolOpenFile,

            // 运行命令
            "run_command" or "runcommand" or "run_cmd" or "execute_command" or "exec_command"
                or "shell" or "bash" or "cmd" or "terminal" or "run_shell" or "execute"
                or "运行" or "执行命令" or "命令行" => ToolRunCommand,

            // 运行 Python
            "run_python" or "runpython" or "python" or "execute_python" or "python_script"
                or "run_script" or "运行python" or "执行python" => ToolPython,

            // 搜索 / 研究
            "web_search" or "websearch" or "search" or "search_web" or "google" or "联网搜索"
                or "网络搜索" or "网页搜索" or "在线搜索" or "搜索" => ToolSearch,
            "deep_research" or "deepresearch" or "research" or "deep_dive" or "深度研究"
                or "深度调研" or "深入研究" or "研究报告" or "调研报告" => ToolResearch,

            // 抓网页
            "fetch_url" or "fetch" or "crawl" or "http_get" or "get_url" or "scrape"
                or "web_fetch" or "抓取网页" or "抓网页" or "读取网页" => ToolFetch,

            // 图片
            "generate_image" or "gen_image" or "text_to_image" or "image_gen" or "draw_image"
                or "create_image" or "image" or "生成图片" or "画图" or "作图" => ToolImage,
            "computer" or "computer_use" or "mouse" or "keyboard" or "操作电脑" or "控制鼠标" or "控制电脑" => ToolComputer,
            "install_ext" or "install_extension" or "安装扩展" or "装扩展" => ToolInstallExt,
            "make_ext" or "pack_ext" or "build_skin" or "制作扩展" or "打包扩展" or "做皮肤" => ToolMakeExt,
            "generate_video" or "gen_video" or "text_to_video" or "video_gen" or "create_video"
                or "make_video" or "video" or "生成视频" or "做视频" or "生成动画" => ToolVideo,
            "see_image" or "view_image" or "image_to_text" or "vision" or "ocr" or "describe_image"
                or "看图" or "识别图片" or "图片理解" => ToolVision,

            // 记忆
            "remember" or "memory_save" or "save_memory" or "memorize" or "记住" or "记下来" => ToolRemember,
            "forget" or "memory_delete" or "delete_memory" or "删除记忆" or "忘记" => ToolForget,

            // 拆分任务 / 子智能体
            "split_task" or "splittask" or "split" or "delegate" or "sub_agents" or "subagents"
                or "spawn_agents" or "parallel_tasks" or "orchestrate" or "dispatch"
                or "拆分任务" or "拆分" or "并行" or "子智能体" or "并行处理" => ToolSplit,

            _ => null,
        };
        if (mapped != null) return mapped;

        // 没命中别名表：原样返回，让工具层报「未知工具」，模型据此修正
        return t;
    }

    /// <summary>
    /// chat（纯聊天）模式下只放开「不改动电脑」的工具。
    /// 「拆分任务」也放开：它本身不动系统，子智能体会继承 chat 模式（同样只读），
    /// 所以纯聊天里也能用「拆开并行想」这件事，不会因此真的去改用户的电脑。
    /// </summary>
    private bool ToolAllowedInMode(string tool)
        => RunMode != "chat" || tool is ToolSearch or ToolResearch or ToolSplit;

    /// <summary>用户这句话是否在要「深度研究 / 调研 / 整份资料」。</summary>
    private static bool WantsResearch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Regex.IsMatch(text, @"研究|调研|深度了解|全面了解|资料汇总|整理一份|帮我查查|查一查");
    }

    /// <summary>用户这句话是否在要搜索 / 查资料 / 做研究（chat 模式下据此启用工具通道）。</summary>
    private static bool WantsSearchOrResearch(string? text)
        => !string.IsNullOrWhiteSpace(text) && (IsSearchIntent(text!) || WantsResearch(text));

    /// <summary>搜索一次：优先爬虫脚本，失败回退内核内置爬虫；都没结果返回 null。</summary>
    private async Task<List<SearchHit>?> SearchOnceAsync(string query, CancellationToken ct)
    {
        var viaScript = await SearchViaScriptAsync(query, ct);
        if (viaScript is { Count: > 0 }) return viaScript;
        var (_, hits) = await WebCrawler.SearchAsync(query, ct);
        return hits.Count > 0 ? hits : null;
    }

    // ------------------------------------------------------------------
    // 工具实现
    // ------------------------------------------------------------------

    private async Task<string> ExecuteToolAsync(string tool, JsonElement args, int sid,
                                                 CancellationToken ct)
    {
        tool = NormalizeToolName(tool);
        _lastToolFull = null;   // 每次工具调用重新记，免得上一个工具的输出串到下一张卡片
        try
        {
            return tool switch
            {
                ToolRunCommand => await RunCommandTool(args, sid, ct),
                ToolPython => await PythonTool(args, sid, ct),
                ToolImage => await ImageTool(args, sid, ct),
                ToolVideo => await VideoTool(args, sid, ct),
                ToolInstallExt => InstallExtTool(args),
                ToolMakeExt => MakeExtTool(args),
                ToolComputer => await ComputerTool(args, sid, ct),
                ToolKnowledge => KnowledgeTool(args),
                ToolVision => MultimodalMain
                    ? "图片已作为多模态输入直接发送给主模型，请直接描述图片内容，不要再调用「看图」工具。"
                    : await VisionTool(args, sid, ct),
                ToolSearch => await WebSearchTool(args, ct),
                ToolResearch => await ResearchToolAsync(args, sid, ct),
                ToolFetch => await FetchUrlTool(args, ct),
                ToolOpenFile => await OpenFileTool(args, sid),
                ToolReadFile => ReadFileTool(args),
                ToolWriteFile => WriteFileTool(args),
                ToolRemember => RememberTool(args),
                ToolForget => ForgetTool(args),
                ToolSplit => SplitRunner == null
                    ? "「拆分任务」当前不可用（未接入子智能体执行器）。"
                    : await SplitRunner(args.GetRawText(), ct),
                _ => await CustomOrDefaultAsync(tool, args, sid, ct),
            };
        }
        catch (Exception ex)
        {
            return $"工具「{tool}」执行失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 工具名对不上内置的，就看看是不是 .dslt 自定义工具；都不是才报「未知工具」。
    /// 自定义工具 = 用户/模型自己打包的脚本（Python / cmd / PowerShell / Node），
    /// 在包目录里运行，输入通过参数 + 环境变量 DEEPSLEEP_TOOL_INPUT 传入，stdout 就是结果。
    /// </summary>
    private async Task<string> CustomOrDefaultAsync(string tool, JsonElement args, int sid, CancellationToken ct)
    {
        var t = _customTools.FirstOrDefault(x =>
            string.Equals(x.Name, tool, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Id, tool, StringComparison.OrdinalIgnoreCase));
        if (t == null) return $"未知工具：{tool}";
        return await RunCustomToolAsync(t, args, sid, ct);
    }

    private async Task<string> RunCustomToolAsync(CustomTool t, JsonElement args, int sid, CancellationToken ct)
    {
        string entry = Path.Combine(t.Dir, t.Entry);
        if (!File.Exists(entry)) return $"自定义工具「{t.Name}」的入口脚本不见了：{entry}";

        string input = GetArg(args, "输入") ?? GetArg(args, "input") ?? GetArg(args, "参数") ?? "";

        if (RunMode != "boom" && ConfirmRequested != null)
        {
            bool ok = await ConfirmRequested(sid, $"运行自定义工具「{t.Name}」\n脚本：{t.Entry}" +
                (input.Length > 0 ? $"\n输入：{Preview(input, 200)}" : ""));
            if (!ok) return "用户拒绝运行该自定义工具。";
        }

        string exe;
        var argv = new List<string>();
        switch (t.Runtime)
        {
            case "cmd":
                exe = "cmd.exe";
                argv.Add("/c"); argv.Add(entry);
                break;
            case "powershell":
                exe = "powershell.exe";
                argv.Add("-NoProfile"); argv.Add("-ExecutionPolicy"); argv.Add("Bypass");
                argv.Add("-File"); argv.Add(entry);
                break;
            case "node":
                exe = "node";
                argv.Add(entry);
                break;
            default:
                string? py = await FindPython3Async();
                if (py == null) return "系统里找不到 Python 3，跑不了这个自定义工具（或者把它改成 cmd 版）。";
                exe = py;
                argv.Add(entry);
                break;
        }
        if (input.Length > 0) argv.Add(input);

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = t.Dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            foreach (var s in argv) psi.ArgumentList.Add(s);
            psi.Environment["DEEPSLEEP_TOOL_INPUT"] = input;
            psi.Environment["DEEPSLEEP_TOOL_ID"] = t.Id;

            using var proc = Process.Start(psi);
            if (proc == null) return $"自定义工具「{t.Name}」启动失败。";
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            string stdout = await outTask.ConfigureAwait(false) ?? "";
            string stderr = await errTask.ConfigureAwait(false) ?? "";

            _lastToolFull = stdout.Length > 0 ? stdout : stderr;
            string res = stdout.Trim();
            if (proc.ExitCode != 0)
                return $"自定义工具「{t.Name}」退出码 {proc.ExitCode}。\n{(string.IsNullOrWhiteSpace(res) ? stderr : res)}";
            return res.Length == 0 ? $"自定义工具「{t.Name}」运行完了，但没有输出。" : res;
        }
        catch (OperationCanceledException) { return "自定义工具执行超时（5 分钟）。"; }
        catch (Exception ex) { return $"自定义工具执行失败：{ex.Message}"; }
    }

    /// <summary>
    /// 查 deepsleep 知识库。极速模式直接拒绝：知识库检索会多一次往返并塞回长章节，
    /// 与「极速」的取舍冲突；用户真要完整规范就提示他关掉 ⚡。
    /// </summary>
    private string KnowledgeTool(JsonElement args)
    {
        if (FastMode)
            return "极速模式不查阅知识库（保持响应速度）。当前系统提示词里已经带了工具参数与关键规则，请直接据此作答；" +
                   "需要 deepsleep 的完整规格/格式规范时，请提示用户关闭 ⚡ 极速模式后再查。";
        string topic = GetArg(args, "主题") ?? GetArg(args, "topic") ??
                       GetArg(args, "课题") ?? GetArg(args, "关键词") ?? GetArg(args, "query") ?? "";
        var kb = KnowledgeBase.Load(DataDir ?? "");
        if (kb.Count == 0)
            return "知识库为空：安装目录 assets/knowledge/ 下没找到内置文档。可把 .md / .txt 放进 data/knowledge/ 自建。";
        Log($"查知识库：主题={topic} 条目={kb.Count}");
        return KnowledgeBase.Query(kb, topic);
    }

    /// <summary>安装扩展包（.dslt / .dslp），和界面里点安装走同一套逻辑。</summary>
    private string InstallExtTool(JsonElement args)
    {
        string? path = GetArg(args, "路径") ?? GetArg(args, "path");
        if (string.IsNullOrWhiteSpace(path)) return "参数错误：缺少「路径」。";
        if (!File.Exists(path)) return $"文件不存在：{path}";
        var (_, msg, _) = ExtensionStore.Install(DataDir ?? "", path);
        return msg;
    }

    /// <summary>把目录打成扩展包（模型造完文件后调用；手搓的人也能用）。</summary>
    private string MakeExtTool(JsonElement args)
    {
        string? dir = GetArg(args, "目录") ?? GetArg(args, "dir");
        string? outp = GetArg(args, "输出") ?? GetArg(args, "out");
        string kind = (GetArg(args, "类型") ?? GetArg(args, "kind") ?? "tool").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(outp))
            return "参数错误：需要「目录」和「输出」。";
        if (!Directory.Exists(dir)) return $"目录不存在：{dir}";
        string low = outp.ToLowerInvariant();
        if (!low.EndsWith(ExtensionStore.ToolExt) && !low.EndsWith(ExtensionStore.SkinExt))
            outp += (kind.StartsWith("skin") || kind.StartsWith("皮肤")) ? ExtensionStore.SkinExt : ExtensionStore.ToolExt;
        var (_, msg) = ExtensionStore.Pack(dir, outp);
        return msg;
    }

    /// <summary>
    /// 「操作电脑」：执行一步鼠标/键盘动作 → 立刻截图 → 用视觉模型读屏 → 把结果交回给主模型决策。
    /// 这样即使是纯文本的主模型也能"看见"屏幕（每步都有一份文字版的界面状态）。
    /// 坐标统一用 0~1 相对坐标：截图会被缩放，像素坐标跨分辨率/缩放不可靠。
    /// </summary>
    private async Task<string> ComputerTool(JsonElement args, int sid, CancellationToken ct)
    {
        string action = (GetArg(args, "动作") ?? GetArg(args, "action") ?? "截图").Trim();

        double Num(string cn, string en)
        {
            string? v = GetArg(args, cn) ?? GetArg(args, en);
            return double.TryParse(v, out double d) ? d : 0;
        }
        double x = Num("x", "x"), y = Num("y", "y");
        bool relative = true;
        string? relArg = GetArg(args, "relative") ?? GetArg(args, "相对");
        if (relArg != null && bool.TryParse(relArg, out bool rb)) relative = rb;
        string text = GetArg(args, "text") ?? GetArg(args, "文字") ?? GetArg(args, "文本") ?? "";
        string key = GetArg(args, "key") ?? GetArg(args, "按键") ?? "";
        int amount = (int)Num("amount", "数量");

        // 鼠标键盘是「能替用户点任何东西」的权限，work 模式下必须先问一句
        if (RunMode != "boom" && ConfirmRequested != null)
        {
            string desc = action switch
            {
                "输入" => $"输入文字「{(text.Length > 60 ? text[..60] + "…" : text)}」",
                "按键" => $"按 {key}",
                "点击" or "双击" or "右键" or "移动" => $"{action} ({x:0.###}, {y:0.###}){(relative ? " 相对" : " 像素")}",
                "滚动" => $"滚动 {amount} 格",
                "等待" => $"等待 {amount} 毫秒",
                _ => "截屏",
            };
            bool ok = await ConfirmRequested(sid, "操作你的电脑（鼠标/键盘）：" + desc);
            if (!ok) return "用户拒绝了这次电脑操作。";
        }

        var (done, msg) = ComputerUse.Do(action, x, y, relative, text, key, amount);
        if (!done) return msg;

        // 给界面一点反应时间，再截图（点了按钮立刻截往往是旧画面）
        try { await Task.Delay(action is "截图" ? 80 : 450, ct).ConfigureAwait(false); } catch { }

        var (path, w, h) = ComputerUse.Shot(DataDir ?? "");
        if (path.Length == 0)
            return $"{msg}\n截图失败（可能是当前会话不在交互式桌面上，比如远程/服务会话）。";

        _lastImagePath = path;

        string visual = "";
        if (_vision.Configured)
        {
            const string ask =
                "这是一张 Windows 屏幕截图。用简短中文回答，不要客套话：\n" +
                "① 现在是什么程序/界面（标题栏文字也读出来）；\n" +
                "② 主要可交互元素，每个都给出**相对百分比坐标**（把整张图当作 0~1，x 向右 y 向下），格式：名称(x,y)，最多 6 个；\n" +
                "③ 有没有弹窗、输入框、报错或加载中状态。";
            try
            {
                string? r = await _vision.AnalyzeAsync(path, ask, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(r)) visual = "\n\n屏幕内容（视觉模型解读）：\n" + r.Trim();
            }
            catch { }
        }
        else
        {
            visual = "\n\n（未配置图像理解 Key，只能给你截图路径，没法读屏；要我看图请到 ⚙ 设置 → 图像理解 填 Key）";
        }

        return $"{msg}。\n截图：{path}（图上 {w}×{h}，实际屏幕 {RemoteDesktop.ScreenWidth}×{RemoteDesktop.ScreenHeight}）" +
               visual +
               "\n\n下一步：需要点击就用 relative=true 传 0~1 的 x/y（视觉模型给的坐标就是这个口径）。";
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

        // 拦截"命令行抓网页"：搜索类需求应走「网络搜索 / 深度研究」工具，而不是 curl 等
        if (LooksLikeWebFetch(cmd))
            return "⚠️ 检测到你在用命令行抓取网页。网页搜索/查资料请改用「网络搜索」或「深度研究」工具；" +
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

        // Windows 走 PowerShell（先设 UTF-8 输出防中文乱码），Linux / macOS 走 bash
        var psi = Platform.ShellStartInfo(cmd, workDir);

        Process? proc = null;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 " + Platform.ShellName);
            // 按字节解（见 Platform.ReadProcessOutputAsync）：老式程序吐 GBK，硬按 UTF-8 解会出乱码
            var stdoutTask = Platform.ReadProcessOutputAsync(proc.StandardOutput);
            var stderrTask = Platform.ReadProcessOutputAsync(proc.StandardError);
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
            _lastToolFull = Truncate(fullResult, MaxToolDisplay);
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
            // 同上：Python 在中文 Windows 上也可能按 GBK 写管道
            var stdoutTask = Platform.ReadProcessOutputAsync(proc.StandardOutput);
            var stderrTask = Platform.ReadProcessOutputAsync(proc.StandardError);
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
            _lastToolFull = Truncate(string.IsNullOrWhiteSpace(result) ? "（脚本无输出）" : result, MaxToolDisplay);
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
        var candidates = Platform.IsWindows
            ? new List<string> { "py", "python3", "python" }
            : new List<string> { "python3", "python" };
        if (Platform.IsWindows)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var ver in new[] { "Python313", "Python312", "Python311", "Python310" })
            {
                string full = Path.Combine(local, "Programs", "Python", ver, "python.exe");
                if (File.Exists(full)) candidates.Add(full);
            }
        }
        else
        {
            foreach (var full in new[]
                     {
                         "/usr/bin/python3", "/usr/local/bin/python3", "/opt/homebrew/bin/python3",
                         "/usr/bin/python", "/opt/homebrew/bin/python",
                     })
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

    /// <summary>判断命令是否在"用命令行抓网页"（应改用网络搜索 / 深度研究工具）。</summary>
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

    /// <summary>生成视频：CogVideoX-Flash（异步任务，最长等 10 分钟）。</summary>
    private async Task<string> VideoTool(JsonElement args, int sid, CancellationToken ct)
    {
        string? prompt = GetArg(args, "提示词");
        if (string.IsNullOrWhiteSpace(prompt))
            return "参数错误：缺少「提示词」。";
        string? size = GetArg(args, "尺寸");

        if (RunMode == "work" && ConfirmRequested != null)
        {
            bool ok = await ConfirmRequested(sid, $"生成视频（约 1~2 分钟）\n提示词：{Preview(prompt, 200)}");
            if (!ok)
                return "用户拒绝生成视频。";
        }

        if (!_video.Configured)
            return "视频生成需要你自己的 API Key（内置免费 Key 不含视频额度的）。"
                 + "去 ⚙ 设置 → 🎬 视频 绑定：可以先用你自己的图像 Key（同属智谱时通用），"
                 + "也可以填别家兼容地址 + Key。";

        string? path = await _video.GenerateAsync(prompt, size, ct);
        if (path == null)
        {
            if (ct.IsCancellationRequested) return "视频生成已停止。";
            return "视频生成失败：" + (string.IsNullOrWhiteSpace(_video.LastError)
                ? "接口返回异常，请检查视频 API 地址/模型名/Key。"
                : _video.LastError);
        }

        _lastVideoPath = path;
        return $"视频已生成并保存到：{path}\n提示词：{prompt}";
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

    /// <summary>
    /// 深度研究：规划子查询 → 多轮搜索 → 抓取网页正文 → 让模型汇总成 Markdown 研究报告并保存到本地。
    /// 参数：主题（必填）、深度 1-3（可选，默认 2）、输出文件（可选绝对路径）。
    /// </summary>
    private async Task<string> ResearchToolAsync(JsonElement args, int sid, CancellationToken ct)
    {
        if (!WebSearchEnabled)
            return "深度研究已关闭（🌐 联网搜索开关关闭）。请提醒用户打开 🌐 开关。";
        string? topic = GetArg(args, "主题") ?? GetArg(args, "课题") ?? GetArg(args, "问题") ?? GetArg(args, "关键词");
        if (string.IsNullOrWhiteSpace(topic))
            return "参数错误：缺少「主题」。";
        topic = topic.Trim();
        int depth = 2;
        if (int.TryParse(GetArg(args, "深度") ?? GetArg(args, "depth"), out int d) ||
            TryGetIntArg(args, out d, "深度", "depth"))
            depth = Math.Clamp(d, 1, 3);
        string? outFile = GetArg(args, "输出文件") ?? GetArg(args, "文件");

        void Progress(string text) => ToolStarted?.Invoke(sid, ToolResearch + "·" + text);

        // ① 规划子查询
        Progress("正在规划研究步骤");
        var queries = await PlanSubQueriesAsync(topic, depth, ct);
        if (ct.IsCancellationRequested) return "深度研究已停止。";

        // ② 多轮搜索，跨查询去重
        var all = new List<SearchHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int cap = Math.Min(24, 6 * depth);
        for (int i = 0; i < queries.Count; i++)
        {
            if (ct.IsCancellationRequested) return "深度研究已停止。";
            Progress($"正在搜索 {i + 1}/{queries.Count}：{Clip(queries[i], 16)}");
            var hits = await SearchOnceAsync(queries[i], ct);
            if (hits == null) continue;
            foreach (var h in hits)
            {
                string key = string.IsNullOrWhiteSpace(h.Url) ? h.Title : h.Url;
                if (seen.Add(key)) all.Add(h);
            }
            if (all.Count >= cap) break;
        }
        if (all.Count == 0)
            return $"深度研究失败：围绕「{topic}」没有搜到任何网页（网络不可达或搜索被风控拦截）。请如实告知用户，不要编造结果。";

        // ③ 抓取正文（抓不到就跳过，最后用摘要兜底）
        int fetchCount = Math.Min(all.Count, depth >= 3 ? 6 : depth == 2 ? 4 : 3);
        var docs = new List<(SearchHit Hit, string Text)>();
        for (int i = 0; i < fetchCount; i++)
        {
            if (ct.IsCancellationRequested) return "深度研究已停止。";
            Progress($"正在阅读资料 {i + 1}/{fetchCount}：{Clip(all[i].Title, 16)}");
            try
            {
                string text = await WebCrawler.FetchTextAsync(all[i].Url, ct, 3000);
                if (text.Length >= 200) docs.Add((all[i], text));
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 单篇抓取失败不影响整体 */ }
        }

        // ④ 让模型汇总成报告
        Progress("正在撰写研究报告");
        string? report = await WriteResearchReportAsync(topic, queries, docs, all, ct);
        if (string.IsNullOrWhiteSpace(report)) report = FallbackReport(topic, queries, docs, all);

        // ⑤ 保存
        string dir = string.IsNullOrWhiteSpace(DataDir) ? Path.GetTempPath() : Path.Combine(DataDir, "research");
        string path = string.IsNullOrWhiteSpace(outFile)
            ? Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Slug(topic)}.md")
            : Path.GetFullPath(outFile);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, report, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            return $"深度研究完成，但报告写盘失败（{ex.Message}）。报告正文如下：\n\n{Truncate(report, MaxToolOutput)}";
        }
        return $"深度研究完成（主题：{topic}；深度 {depth}；搜索 {queries.Count} 轮，命中 {all.Count} 条，精读 {docs.Count} 篇）\n" +
               $"报告已保存：{path}\n\n" + Truncate(report, MaxToolOutput);
    }

    /// <summary>用模型把研究主题拆成几个不同角度的搜索查询词；模型不可用时退化为启发式查询。</summary>
    private async Task<List<string>> PlanSubQueriesAsync(string topic, int depth, CancellationToken ct)
    {
        int want = depth >= 3 ? 5 : depth == 2 ? 4 : 2;
        var fallback = new List<string>
        {
            topic, topic + " 最新进展", topic + " 数据 事实", topic + " 风险 争议", topic + " 对比 案例",
        };
        try
        {
            string sys = "你是研究助理，负责把研究主题拆成互不重复的中文搜索查询词。只输出查询词，每行一个，不要编号、不要解释。";
            string user = $"研究主题：{topic}\n请给出 {want} 个从不同角度切入的搜索查询词（可覆盖：最新进展、数据与事实、风险与争议、对比或案例）。";
            string? r = await CallOnceAsync(sys, new List<(string, string)> { ("user", user) }, ct, null);
            if (!string.IsNullOrWhiteSpace(r))
            {
                var list = new List<string>();
                foreach (var raw in r.Split('\n'))
                {
                    string q = Regex.Replace(raw.Trim(), @"^[\-\*\d\.、\)\s]+", "").Trim().Trim('"', '「', '」', '“', '”');
                    if (q.Length is >= 2 and <= 60 && !list.Contains(q)) list.Add(q);
                    if (list.Count >= want) break;
                }
                if (list.Count > 0)
                {
                    if (!list.Contains(topic)) list.Insert(0, topic);
                    return list.Take(want).ToList();
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 模型不可用 → 用启发式查询 */ }
        return fallback.Take(want).ToList();
    }

    /// <summary>把资料交给模型写成结构化研究报告；模型不可用返回 null（由 FallbackReport 兜底）。</summary>
    private async Task<string?> WriteResearchReportAsync(string topic, List<string> queries,
        List<(SearchHit Hit, string Text)> docs, List<SearchHit> all, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"研究主题：{topic}");
        sb.AppendLine("搜索轮次：" + string.Join(" / ", queries));
        sb.AppendLine();
        if (docs.Count > 0)
        {
            for (int i = 0; i < docs.Count; i++)
            {
                sb.AppendLine($"【资料 {i + 1}】{docs[i].Hit.Title}");
                sb.AppendLine($"链接：{docs[i].Hit.Url}");
                sb.AppendLine(docs[i].Text);
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("（网页正文抓取失败，只有搜索摘要）");
            for (int i = 0; i < Math.Min(10, all.Count); i++)
            {
                sb.AppendLine($"【资料 {i + 1}】{all[i].Title}｜{all[i].Url}");
                if (!string.IsNullOrWhiteSpace(all[i].Snippet)) sb.AppendLine(all[i].Snippet);
                sb.AppendLine();
            }
        }
        string sys = "你是资深研究员。只依据给定资料写中文研究报告，禁止编造资料里没有的事实；资料不足的地方明确写「资料不足」。";
        string user =
            $"请基于下面的资料，围绕「{topic}」写一份 Markdown 研究报告，结构固定为：\n" +
            "## 摘要\n## 关键发现（3-6 条，每条标注依据的资料编号）\n## 详细分析\n## 风险与不确定性\n## 结论与建议\n## 参考来源（编号 + 标题 + 链接）\n" +
            "要求：引用资料用 [1][2] 这样的编号；不要编造；总长 800-2000 字；只输出报告本身，不要额外解释。\n\n" +
            Truncate(sb.ToString(), 16000);
        return await CallOnceAsync(sys, new List<(string, string)> { ("user", user) }, ct, null);
    }

    /// <summary>模型汇总失败时的兜底报告：把抓到的资料直接整理成 Markdown。</summary>
    private static string FallbackReport(string topic, List<string> queries,
        List<(SearchHit Hit, string Text)> docs, List<SearchHit> all)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {topic} · 研究报告（原始资料汇总）");
        sb.AppendLine();
        sb.AppendLine($"> 模型汇总不可用，以下为工具直接整理的结果：命中 {all.Count} 条，精读 {docs.Count} 篇。");
        sb.AppendLine();
        sb.AppendLine("## 搜索轮次");
        foreach (var q in queries) sb.AppendLine($"- {q}");
        sb.AppendLine();
        sb.AppendLine("## 精读资料");
        if (docs.Count == 0) sb.AppendLine("（正文抓取失败，见下方搜索结果）");
        for (int i = 0; i < docs.Count; i++)
        {
            sb.AppendLine($"### [{i + 1}] {docs[i].Hit.Title}");
            sb.AppendLine(docs[i].Hit.Url);
            sb.AppendLine();
            sb.AppendLine(Truncate(docs[i].Text, 700));
            sb.AppendLine();
        }
        var read = new HashSet<string>(docs.Select(x => x.Hit.Url), StringComparer.OrdinalIgnoreCase);
        sb.AppendLine("## 其他搜索结果");
        int n = docs.Count;
        foreach (var h in all.Where(x => !read.Contains(x.Url)).Take(12))
        {
            n++;
            sb.AppendLine($"- [{n}] {h.Title}｜{h.Url}");
            if (!string.IsNullOrWhiteSpace(h.Snippet)) sb.AppendLine($"  {Truncate(h.Snippet, 160)}");
        }
        return sb.ToString();
    }

    /// <summary>把主题变成安全的文件名片段。</summary>
    private static string Slug(string topic)
    {
        string s = Regex.Replace(topic, @"[\\/:*?""<>|\s]+", "-").Trim('-');
        if (s.Length > 40) s = s[..40];
        return string.IsNullOrWhiteSpace(s) ? "research" : s;
    }

    /// <summary>短文本裁剪（用于进度提示）。</summary>
    private static string Clip(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s[..max] + "…";

    /// <summary>网络搜索：内置多引擎爬虫（Bing → 搜狗 → 360 → DuckDuckGo），无需 API Key。</summary>
    private async Task<string> WebSearchTool(JsonElement args, CancellationToken ct)
    {
        if (!WebSearchEnabled)
            return "网络搜索已关闭。请提醒用户打开 AI 助手页的 🌐 联网搜索开关。";
        string? q = GetArg(args, "关键词");
        if (string.IsNullOrWhiteSpace(q))
            return "参数错误：缺少「关键词」。";

        var hits = await SearchOnceAsync(q, ct) ?? new List<SearchHit>();
        string engine = hits.FirstOrDefault()?.Engine ?? "";
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

    /// <summary>写入文件给界面看的明细：路径 + 写进去的内容。</summary>
    private static string WriteFileDetail(JsonElement args)
    {
        string? path = GetArg(args, "路径");
        if (string.IsNullOrWhiteSpace(path)) return "";
        string content = GetArg(args, "内容") ?? "";
        return "路径：" + path + "\n" + Truncate(content, MaxToolOutput);
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

    /// <summary>读取数字型参数（模型有时把「深度」写成数字而不是字符串）。</summary>
    private static bool TryGetIntArg(JsonElement args, out int value, params string[] names)
    {
        value = 0;
        if (args.ValueKind != JsonValueKind.Object) return false;
        foreach (var n in names)
        {
            if (!args.TryGetProperty(n, out var el)) continue;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v))
            {
                value = v;
                return true;
            }
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int vs))
            {
                value = vs;
                return true;
            }
        }
        return false;
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

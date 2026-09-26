using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler.Core;

/// <summary>
/// deepsleep 内核：所有状态与逻辑都在这里，完全不含任何界面类型。
///
/// 前后端协议（与 HTML 界面之间只走 JSON 字符串）：
///   前端 → 内核：InvokeAsync("{\"id\":1,\"cmd\":\"send\",\"kind\":0,\"text\":\"你好\"}")
///   内核 → 前端：Push 事件（JsonSerializer 序列化后由外壳 PostWebMessageAsJson 交给 WebView2）
/// 事件 ev 取值：boot/convs/conv/add/delta/msgUpdate/msgRemove/status/toast/ask/theme/tab/
///   memory/skills/settings/update/updateLatest/updateProgress/updateStatus/insert/restarting/pet
/// </summary>
public sealed partial class Kernel
{
    /// <summary>界面文件通过这个虚拟主机名访问（外壳把 ui\ 目录映射到 app.local）。</summary>
    public const string UiHost = "app.local";

    /// <summary>data 目录的虚拟主机名（图片等本地文件用，避免 HTML 读不到磁盘）。</summary>
    public const string DataHost = "data.local";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>内核 → 界面的事件通道。字符串是完整 JSON，外壳只管转发。</summary>
    public event Action<string>? Push;

    private readonly Engine _engine = new();
    private readonly Agent _agent;
    private readonly Agent _clusterAgent;
    private AppConfig _config = new();
    private string _dataDir = "";
    private string _lastTime = "";
    private int _tab;                                  // 0 = AI 助手，1 = Agent 集群
    private readonly List<Conversation> _agentConvs = new();
    private readonly List<Conversation> _counterConvs = new();   // 以理服人（当前界面不显示，仅原样保留）
    private readonly List<Conversation> _clusterConvs = new();
    private Conversation _agentCur = null!;
    private Conversation _clusterCur = null!;
    private int _nextConvNo = 1;
    private int _nextAgentSid = 1;
    private int _nextCounterSid = 1;
    private int _nextClusterSid = 500001;
    private string _agentBackendBefore = "auto";
    private bool _llmOnline;
    private UpdateInfo? _pendingUpdate;
    private bool _updateRunning;

    private readonly object _gate = new();
    private readonly Dictionary<int, RunState> _agentStates = new();
    private readonly Dictionary<int, RunState> _clusterStates = new();
    private readonly Dictionary<int, CancellationTokenSource> _agentCts = new();
    private readonly Dictionary<int, CancellationTokenSource> _clusterCts = new();
    private readonly Dictionary<int, ChatItem> _streamItems = new();
    private readonly Dictionary<int, ChatItem> _agentThinking = new();
    private readonly Dictionary<int, ChatItem> _agentWorking = new();
    private readonly Dictionary<int, ChatItem> _clusterThinking = new();
    private readonly Dictionary<int, ChatItem> _clusterStreamItems = new();
    private readonly Dictionary<int, ClusterRun> _clusterRuns = new();
    private readonly Dictionary<string, TaskCompletionSource<AskResult>> _asks = new();
    private List<SkillInfo> _skills = new();
    private Timer? _saveTimer;
    private Timer? _llmTimer;
    private MemoryStore? _memory;

    private static readonly string[] ClusterColors =
    {
        "#7C4DFF", "#00B89F", "#F57C00", "#E91E63", "#009688",
        "#8E44AD", "#394B8E", "#C02828", "#2E7D32", "#6D4C41",
    };

    public Kernel()
    {
        _agent = new Agent(_engine);
        _clusterAgent = new Agent(_engine)
        {
            RunMode = "boom",
            DataDir = "",
            Streaming = true,
            MaxConcurrentCalls = 4,
            AutoRemember = false,
        };
    }

    public AppConfig Config => _config;
    public string DataDir => _dataDir;
    public int Tab => _tab;
    public Conversation CurrentAgentConv => _agentCur;
    public Conversation CurrentClusterConv => _clusterCur;

    // ------------------------------------------------------------------
    // 启动
    // ------------------------------------------------------------------

    /// <summary>初始化内核：加载配置、记忆、技能、历史对话，并开始后台探测。</summary>
    public void Init(string dataDir)
    {
        _dataDir = dataDir;
        Directory.CreateDirectory(_dataDir);
        _engine.SetModelDir(_dataDir);
        _config = AppConfig.Load(_dataDir);
        _engine.ApplyConfig(_config);

        _agent.DataDir = _dataDir;
        _memory = new MemoryStore(_dataDir);
        _memory.Load();
        _agent.Memory = _memory;
        _agent.ApplyConfig(_config);
        if (_config.UseOllama) _agent.Backend = "llm";
        _agent.MultimodalMain = _config.MultimodalMain;
        _agent.WebSearchEnabled = _config.WebSearch;
        _agent.MessageAdded += OnAgentMessage;
        _agent.DeltaAdded += OnAgentDelta;
        _agent.ToolStarted += OnAgentToolStarted;
        _agent.ConfirmRequested += ConfirmCommandAsync;
        _agent.SkillUsed += OnSkillUsed;

        _skills = SkillStore.Load(_dataDir);
        _agent.Skills = _skills;

        _clusterAgent.DataDir = _dataDir;
        _clusterAgent.Memory = _memory;
        _clusterAgent.ApplyConfig(_config);
        if (_config.UseOllama) _clusterAgent.Backend = "llm";
        _clusterAgent.MultimodalMain = _config.MultimodalMain;
        _clusterAgent.WebSearchEnabled = _config.WebSearch;
        _clusterAgent.MessageAdded += OnClusterMessage;
        _clusterAgent.DeltaAdded += OnClusterDelta;
        _clusterAgent.SkillUsed += OnSkillUsed;
        _clusterAgent.Skills = _skills;
        string workspace = Path.Combine(_dataDir, "cluster_workspace");
        Directory.CreateDirectory(workspace);
        _clusterAgent.ExtraPrompt = ClusterExtraPrompt(workspace);

        LoadConversations();
        RestoreRuntimeState();

        _agentCur.Items.Add(new ChatItem
        {
            IsSys = true,
            Text = $"deepsleep v{Updater.CurrentVersion} 已就绪。可以直接提任务，我会在需要时调用工具：运行命令、Python、读写文件、网络搜索、深度研究、看图、生成图片。",
            TimeStr = Now(),
            ShowTime = true,
        });
        _clusterCur.Items.Add(new ChatItem
        {
            IsSys = true,
            Text = "Agent 集群已就绪：一句话指挥，会自动拆解成多个 Agent 并行执行（命令 / Python / 读写文件 / 生成图片 / 看图 / 网络搜索 / 深度研究，boom 全自动免确认），最后指挥官汇总结果。",
            TimeStr = Now(),
            ShowTime = true,
        });

        _ = ProbeLlmAsync();
        _llmTimer = new Timer(_ => _ = ProbeLlmAsync(), null, 30000, 30000);
        _ = AutoCheckUpdateAsync();
    }

    /// <summary>界面加载完成后调用：把全部初始状态一次性推过去。</summary>
    public void Boot()
    {
        var cur = TabConv(_tab);
        Emit(new Dictionary<string, object?>
        {
            ["ev"] = "boot",
            ["version"] = Updater.CurrentVersion,
            ["dataDir"] = _dataDir,
            ["os"] = Environment.OSVersion.VersionString,
            ["tab"] = _tab,
            ["theme"] = _config.Theme,
            ["convs"] = ConvListJson(_tab),
            ["conv"] = ConvJson(cur),
            ["items"] = cur.Items.Select(ItemJson).ToList(),
            ["status"] = StatusJson(_tab),
            ["settings"] = SettingsJson(),
            ["prompts"] = PromptTemplates.Select(t => new { name = t.Name, prompt = t.Prompt }).ToList(),
            ["clusterTemplates"] = ClusterTemplates.Select(t => new
            {
                name = t.Name,
                members = t.Members.Select(m => new { role = m.Role, task = m.Task }).ToList(),
            }).ToList(),
            ["skills"] = SkillsJson(),
            ["pet"] = _config.PetEnabled,
            ["update"] = _pendingUpdate == null ? null : UpdateJson(_pendingUpdate),
        });
    }

    /// <summary>桌宠拖拽结束：记住位置（外壳直接调，避免多绕一层协议）。</summary>
    public void NotePetPosition(int x, int y)
    {
        _config.PetX = x;
        _config.PetY = y;
        _config.Save();
    }

    /// <summary>桌宠开关（外壳右键菜单「隐藏桌宠」调用）。</summary>
    public void SetPetVisible(bool on) => SetPetEnabled(on);

    /// <summary>关闭前保存（外壳在窗口关闭时调用）。</summary>
    public void Shutdown()
    {
        try { _engine.SaveModels(); } catch { }
        try { SaveAll(); } catch { }
        try { _llmTimer?.Dispose(); } catch { }
        try { _saveTimer?.Dispose(); } catch { }
    }

    // ------------------------------------------------------------------
    // 前后端协议
    // ------------------------------------------------------------------

    /// <summary>处理一条前端命令，返回 JSON 结果（{"id":1,"ok":true}）。</summary>
    public async Task<string> InvokeAsync(string json)
    {
        int id = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out int v)) id = v;
            string cmd = GetStr(root, "cmd") ?? "";
            await DispatchAsync(cmd, root).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { id, ok = true }, Json);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { id, ok = false, err = ex.Message }, Json);
        }
    }

    private async Task DispatchAsync(string cmd, JsonElement a)
    {
        switch (cmd)
        {
            case "boot": Boot(); break;
            case "tab": SetTab(GetInt(a, "tab", 0)); break;

            case "newConv": NewConversation(GetInt(a, "kind", TabKind)); break;
            case "selectConv": SelectConversation(GetInt(a, "kind", TabKind), GetInt(a, "sid", 0)); break;
            case "renameConv": RenameConversation(GetInt(a, "sid", 0), GetStr(a, "title") ?? ""); break;
            case "deleteConv": DeleteConversation(GetInt(a, "sid", 0)); break;
            case "pinConv": PinConversation(GetInt(a, "sid", 0), GetBool(a, "pinned")); break;
            case "moveConv": MoveConversation(GetInt(a, "sid", 0), GetInt(a, "dir", 0)); break;
            case "searchConv": SearchConversations(GetInt(a, "kind", TabKind), GetStr(a, "q") ?? ""); break;

            case "send": await SendAsync(GetInt(a, "kind", TabKind), GetStr(a, "text") ?? "",
                                         GetStr(a, "target") ?? "main").ConfigureAwait(false); break;
            case "stop": StopRun(GetInt(a, "kind", TabKind)); break;
            case "resume": await ResumeAsync(GetInt(a, "kind", TabKind)).ConfigureAwait(false); break;
            case "regenerate": await RegenerateAsync(GetInt(a, "kind", TabKind)).ConfigureAwait(false); break;
            case "clear": ClearConversation(GetInt(a, "kind", TabKind)); break;
            case "deleteMsg": DeleteMessage(GetInt(a, "kind", TabKind), GetStr(a, "item") ?? ""); break;

            case "setMode": SetAgentMode(GetStr(a, "mode") ?? "work"); break;
            case "setFast": SetFast(GetBool(a, "on")); break;
            case "setLocal": SetLocal(GetBool(a, "on")); break;
            case "setSearch": SetSearch(GetBool(a, "on")); break;
            case "setTheme": SetTheme(GetStr(a, "theme") ?? "light"); break;
            case "setAutoCheck": SetAutoCheck(GetBool(a, "on")); break;
            case "attach": AttachFile(GetInt(a, "kind", TabKind), GetStr(a, "path") ?? ""); break;
            case "openPath": OpenPath(GetStr(a, "path") ?? ""); break;

            case "memoryGet": EmitMemory(); break;
            case "memorySave": SaveMemory(GetStr(a, "text") ?? ""); break;
            case "memoryClear": ClearMemory(); break;

            case "skills": EmitSkills(); break;
            case "skillInstallUrl": await InstallSkillUrlAsync(GetStr(a, "url") ?? "").ConfigureAwait(false); break;
            case "skillInstallFolder": InstallSkillFolder(GetStr(a, "path") ?? ""); break;
            case "skillRemove": RemoveSkill(GetStr(a, "name") ?? ""); break;

            case "settingsGet": EmitSettings(); break;
            case "settingsSave": SaveSettings(a); break;
            case "installOllama": await InstallOllamaAsync().ConfigureAwait(false); break;
            case "setToken": SaveToken(GetStr(a, "token") ?? ""); break;

            case "answer": Answer(GetStr(a, "askId") ?? "", GetBool(a, "yes"), GetBool(a, "remember")); break;
            case "clusterTemplate": await RunClusterTemplateAsync(GetInt(a, "index", -1)).ConfigureAwait(false); break;

            case "checkUpdate": await CheckUpdateAsync(true).ConfigureAwait(false); break;
            case "runUpdate": await RunUpdateAsync().ConfigureAwait(false); break;

            case "openMain": Emit(new { ev = "openMain" }); break;
            case "petToggle": SetPetEnabled(GetBool(a, "on")); break;
            case "restarting": break;

            default: throw new InvalidOperationException("未知命令：" + cmd);
        }
    }

    // ------------------------------------------------------------------
    // 事件推送
    // ------------------------------------------------------------------

    private void Emit(object payload)
    {
        try { Push?.Invoke(JsonSerializer.Serialize(payload, Json)); } catch { }
    }

    private void Emit(Dictionary<string, object?> payload) => Emit((object)payload);

    private static string? GetStr(JsonElement a, string name)
        => a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement a, string name, int fallback)
        => a.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : fallback;

    private static double GetDbl(JsonElement a, string name, double fallback)
        => a.TryGetProperty(name, out var v) && v.TryGetDouble(out double n) ? n : fallback;

    private static bool GetBool(JsonElement a, string name)
    {
        if (!a.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.GetDouble() != 0,
            JsonValueKind.String => v.GetString() is "true" or "1" or "on" or "yes",
            _ => false,
        };
    }

    // ------------------------------------------------------------------
    // JSON 构造
    // ------------------------------------------------------------------

    private int TabKind => _tab == 1 ? 2 : 0;

    private Conversation TabConv(int tab) => tab == 1 ? _clusterCur : _agentCur;

    private static Dictionary<string, object?> ConvJson(Conversation c) => new()
    {
        ["sid"] = c.Sid,
        ["kind"] = c.Kind,
        ["title"] = c.Title,
        ["pinned"] = c.IsPinned,
        ["created"] = c.Created,
        ["count"] = c.Items.Count,
    };

    private List<Dictionary<string, object?>> ConvListJson(int tab)
    {
        var list = tab == 1 ? _clusterConvs : _agentConvs;
        var cur = tab == 1 ? _clusterCur : _agentCur;
        var pinned = list.Where(c => c.IsPinned).Select(ConvJson);
        var rest = list.Where(c => !c.IsPinned).Select(ConvJson);
        return pinned.Concat(rest).Select(d => { d["current"] = (int)(d["sid"] ?? 0) == cur.Sid; return d; }).ToList();
    }

    private Dictionary<string, object?> ItemJson(ChatItem it) => new()
    {
        ["id"] = it.Id,
        ["sessionId"] = it.SessionId,
        ["self"] = it.IsSelf,
        ["sys"] = it.IsSys,
        ["text"] = it.Text,
        ["meta"] = it.Meta,
        ["time"] = it.TimeStr,
        ["showTime"] = it.ShowTime,
        ["avatar"] = it.AvatarText,
        ["selfAvatar"] = it.SelfAvatarText,
        ["color"] = it.AvatarColor,
        ["thinking"] = it.Thinking,
        ["opacity"] = it.ThinkingOpacity,
        ["image"] = ImageUrl(it.ImagePath),
        ["attName"] = it.AttachmentName,
        ["attSize"] = it.AttachmentSize,
        ["speaker"] = it.Speaker,
        ["accepted"] = it.Accepted,
    };

    /// <summary>把本地绝对路径换成界面能访问的虚拟主机 URL（data 目录外的文件先搬进 data）。</summary>
    public string? ImageUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (!File.Exists(path)) return null;
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(_dataDir) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                string dir = Path.Combine(_dataDir, "uploads");
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, Path.GetFileName(full));
                File.Copy(full, dest, true);
                full = dest;
            }
            string rel = full[root.Length..].Replace('\\', '/');
            return $"https://{DataHost}/" + rel;
        }
        catch { return null; }
    }

    private List<object> SkillsJson() => _skills.Select(s => (object)new
    {
        name = s.Name,
        description = s.Description,
        path = s.Path,
        files = s.Files.Count,
    }).ToList();

    private Dictionary<string, object?> StatusJson(int tab)
    {
        int kind = tab == 1 ? 2 : 0;
        var conv = tab == 1 ? _clusterCur : _agentCur;
        var st = kind == 0 ? GetAgentState(conv.Sid) : GetClusterState(conv.Sid);
        return new Dictionary<string, object?>
        {
            ["kind"] = kind,
            ["conv"] = conv.Sid,
            ["state"] = StateName(st),
            ["busy"] = kind == 0 ? AgentBusy(conv.Sid) : ClusterBusy(conv.Sid),
            ["sendLabel"] = SendLabel(st),
            ["canRegenerate"] = st == RunState.Idle,
            ["status"] = kind == 0 ? AgentStatusText() : ClusterStatusText(),
        };
    }

    private static string StateName(RunState s) => s switch
    {
        RunState.Running => "running",
        RunState.Stopped => "stopped",
        _ => "idle",
    };

    private static string SendLabel(RunState s) => s switch
    {
        RunState.Running => "停止",
        RunState.Stopped => "恢复",
        _ => "发送",
    };

    private Dictionary<string, object?> SettingsJson() => new()
    {
        ["apiUrl"] = _config.ApiUrl,
        ["apiModel"] = _config.ApiModel,
        ["apiKey"] = _config.ApiKey,
        ["apiFormat"] = _config.ApiFormat,
        ["ollamaModel"] = _config.OllamaModel,
        ["ollamaModelStrong"] = _config.OllamaModelStrong,
        ["ollamaUrl"] = _config.OllamaUrl,
        ["useOllama"] = _config.UseOllama,
        ["imageApiKey"] = _config.ImageApiKey,
        ["imageModel"] = _config.ImageModel,
        ["visionApiKey"] = _config.VisionApiKey,
        ["visionModel"] = _config.VisionModel,
        ["multimodalMain"] = _config.MultimodalMain,
        ["updateUrl"] = _config.UpdateUrl,
        ["autoCheckUpdate"] = _config.AutoCheckUpdate,
        ["updateSource"] = _config.UpdateSource,
        ["petEnabled"] = _config.PetEnabled,
        ["theme"] = _config.Theme,
        ["mode"] = _agent.RunMode,
        ["fast"] = _agent.FastMode,
        ["search"] = _agent.WebSearchEnabled,
        ["local"] = _agent.Backend == "local" || _clusterAgent.Backend == "local",
        ["llmOnline"] = _llmOnline,
        ["presets"] = FreePresets.Select(p => new { name = p.Name, url = p.Url, model = p.Model }).ToList(),
        ["freeKeyHint"] = FreeKeyHint,
        ["tokenSaved"] = Updater.HasToken,
    };

    // ------------------------------------------------------------------
    // 小工具
    // ------------------------------------------------------------------

    private string Now() => DateTime.Now.ToString("HH:mm");

    private bool ShouldShowTime()
    {
        string now = Now();
        if (now != _lastTime) { _lastTime = now; return true; }
        return false;
    }

    private static string Cap(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private static string Clip(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private static string WorkerAvatar(string name)
    {
        string t = name.Trim();
        return t.Length == 0 ? "A" : t.Length <= 2 ? t : t[..2];
    }

    private void Toast(string text, string level = "info")
        => Emit(new { ev = "toast", level, text });

    // ------------------------------------------------------------------
    // 防抖保存
    // ------------------------------------------------------------------

    private void ScheduleSave()
    {
        lock (_gate)
        {
            _saveTimer ??= new Timer(_ => SaveAllSafe(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(1000, Timeout.Infinite);
        }
    }

    private void SaveAllSafe()
    {
        try { SaveAll(); } catch { }
    }

    private void SaveAll()
    {
        if (_agentCur == null) return;
        SaveConversations();
        SaveRuntimeState();
    }
}
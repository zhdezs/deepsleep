using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace TrollWrangler;

public sealed partial class MainWindow : Window
{
    private readonly Engine _engine = new();
    private readonly ApiClient _counterLlm = new();
    private readonly Agent _agent;
    private AppConfig _config = new();
    private string _dataDir = "";
    private string _lastTime = "";
    private readonly List<Conversation> _agentConvs = new();
    private readonly List<Conversation> _counterConvs = new();
    private readonly List<Conversation> _clusterConvs = new();
    private Conversation _agentCur = null!;
    private Conversation _counterCur = null!;
    private Conversation _clusterCur = null!;
    private int _nextConvId = 1;
    private int _nextAgentSid = 1;
    private int _nextCounterSid = 1;
    private int _nextClusterSid = 500001;
    private DispatcherTimer? _saveTimer;
    private DispatcherTimer? _clusterThinkingTimer;
    private int _clusterThinkingDots;
    private UpdateInfo? _pendingUpdate;
    private readonly Agent _clusterAgent;

    /// <summary>集群分工模板：一键创建多个不同角色的成员。</summary>
    private static readonly (string Name, (string Role, string Task)[] Members)[] ClusterTemplates =
    {
        ("开发团队", new[]
        {
            ("产品经理", "梳理需求，输出功能清单与验收标准。"),
            ("前端开发", "实现前端页面/组件，代码写到工作目录并说明如何运行。"),
            ("后端开发", "设计并实现接口与数据逻辑，提供代码与调用说明。"),
            ("测试工程师", "编写并运行测试，报告缺陷与修复建议。"),
            ("UI 设计", "用「生成图片」工具产出界面配图/图标，并给出布局建议。"),
        }),
        ("调研团队", new[]
        {
            ("资料调研", "开启联网搜索，收集相关资料并整理要点。"),
            ("数据分析", "写 Python 脚本处理/统计可获得的数据并给出结论。"),
            ("报告撰写", "把调研与数据结果写成结构化报告。"),
            ("质量校对", "检查报告的事实与错漏，给出修订意见。"),
        }),
        ("内容团队", new[]
        {
            ("文案", "撰写主体文案，风格贴合主题。"),
            ("配图", "用「生成图片」工具制作配图并保存。"),
            ("排版", "把文案与配图整理成可发布格式。"),
            ("审核", "检查内容合规性与质量，给出修改建议。"),
        }),
    };

    /// <summary>一次集群执行的状态：指令文本、子任务成员、汇总会话 id。</summary>
    private sealed class ClusterRun
    {
        public string UserText = "";
        public List<ClusterAgent> Workers = new();
        public int SummarySid;
    }

    private readonly Dictionary<int, ClusterRun> _clusterRuns = new();
    private readonly Dictionary<int, ChatItem> _clusterStreamItems = new();
    private readonly Dictionary<int, CancellationTokenSource> _clusterCts = new();
    private readonly Dictionary<int, AgentRunState> _clusterStates = new();

    public ObservableCollection<ChatItem> AgentItems => _agentCur.Items;

    public MainWindow()
    {
        InitializeComponent();
        Title = "deepsleep · AI 助手";
        // 毛玻璃背景（Win11 亚克力；失败时退回 Mica）
        try { SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop(); }
        catch { try { SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop(); } catch { } }
        RootGrid.SizeChanged += OnRootSizeChanged;

        // 设置窗口尺寸
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(1500, 960));

        // 模型持久化目录：exe 同级的 data 目录（越用越强，重启继续累积）
        var baseDir = AppContext.BaseDirectory;
        _dataDir = System.IO.Path.Combine(baseDir, "data");
        System.IO.Directory.CreateDirectory(_dataDir);
        _engine.SetModelDir(_dataDir);

        // 加载配置（API key / 模型名），应用到引擎
        _config = AppConfig.Load(_dataDir);
        _engine.ApplyConfig(_config);
        _counterLlm.Url = _config.CounterApiUrl;
        _counterLlm.ApiKey = _config.CounterApiKey;
        _counterLlm.Model = _config.CounterModel;
        // 应用主题（浅色/深色）
        bool dark = _config.Theme == "dark";
        RootGrid.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        if (ThemeToggle != null)
        {
            ThemeToggle.IsChecked = dark;
            ThemeToggle.Content = dark ? "☀️ 浅色" : "🌙 深色";
        }

        // AI 助手（以理服人引擎保留为自训练后端，暂不开放）
        _agent = new Agent(_engine);
        _agent.DataDir = _dataDir;
        _agent.Memory = new MemoryStore(_dataDir);
        _agent.Memory.Load();
        _agent.ApplyConfig(_config);
        if (_config.UseOllama) { _agent.Backend = "llm"; }
        _agent.MultimodalMain = _config.MultimodalMain;
        _agent.MessageAdded += OnAgentMessage;
        _agent.DeltaAdded += OnAgentDelta;
        _agent.ToolStarted += OnAgentToolStarted;
        _agent.ConfirmRequested += ConfirmCommandAsync;
        _agent.SkillUsed += OnSkillUsed;
        _skills = SkillStore.Load(_dataDir);
        _agent.Skills = _skills;

        // Agent 集群：独立实例，固定 boom 模式（并行任务免确认）；真·流式输出，LLM 并发限流防 429
        _clusterAgent = new Agent(_engine)
        {
            RunMode = "boom",
            DataDir = _dataDir,
            Streaming = true,
            MaxConcurrentCalls = 4,
            AutoRemember = false,
        };
        _clusterAgent.Memory = new MemoryStore(_dataDir);
        _clusterAgent.Memory.Load();
        _clusterAgent.ApplyConfig(_config);
        if (_config.UseOllama) { _clusterAgent.Backend = "llm"; }
        _clusterAgent.MultimodalMain = _config.MultimodalMain;
        _clusterAgent.MessageAdded += OnClusterMessage;
        _clusterAgent.DeltaAdded += OnClusterDelta;
        _clusterAgent.SkillUsed += OnSkillUsed;
        _clusterAgent.Skills = _skills;
        string workspace = Path.Combine(_dataDir, "cluster_workspace");
        Directory.CreateDirectory(workspace);
        _clusterAgent.ExtraPrompt =
            "你处于 Agent 集群中，拥有 boom 全自动权限：执行命令、运行 Python 脚本、读写文件都无需任何确认。" +
            "请把代码/脚本/文档/图片等产物保存到工作区：" + workspace +
            "（如需子目录可自行创建），并在结果里给出产物的完整本地路径。";

        // 恢复/初始化对话历史（持久化到 data\conversations.json）
        LoadConversations();
        RestoreRuntimeState();   // 恢复断点：被关闭/关机前未跑完的任务可继续
        AgentList.ItemsSource = _agentCur.Items;
        ClusterList.ItemsSource = _clusterCur.Items;
        RefreshConvList();
        UpdateAgentStatus();
        UpdateClusterStatus();
        UpdateAgentSendButton(_agentCur.Sid);
        UpdateClusterSendButton(_clusterCur.Sid);
        ScrollToBottomDeferred();
        AgentItems.Add(new ChatItem
        {
            IsSys = true,
            Text = $"deepsleep v{Updater.CurrentVersion} 已就绪。可以直接提任务，我会在需要时调用工具：运行命令、Python、读写文件、联网搜索、看图、生成图片。",
            TimeStr = Now(),
            ShowTime = true,
        });
        _clusterCur.Items.Add(new ChatItem
        {
            IsSys = true,
            Text = "Agent 集群已就绪：一句话指挥，会自动拆解成多个 Agent 并行执行（命令 / Python / 读写文件 / 生成图片 / 看图 / 联网搜索，boom 全自动免确认），最后指挥官汇总结果。",
            TimeStr = Now(),
            ShowTime = true,
        });
        // 关闭时保存模型与对话历史
        Closed += (_, _) =>
        {
            _engine.SaveModels();
            SaveConversations();
        };

        // 后台探测本地大模型（Ollama）是否在线
        _ = ProbeLlmAsync();
        var llmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        llmTimer.Tick += (_, _) => _ = ProbeLlmAsync();
        llmTimer.Start();

        _ = AutoCheckUpdateAsync();   // OTA：启动时静默检查新版本

        // 桌宠：透明分层窗口，只有鲸鱼本体显示在桌面上（右键可隐藏/退出）
        try
        {
            string petRaw = Path.Combine(AppContext.BaseDirectory, "pet.raw");
            if (_config.PetEnabled && File.Exists(petRaw))
            {
                _pet = new DesktopPet(petRaw, () =>
                {
                    try { AppWindow.Show(); Activate(); }
                    catch { /* 打不开就忽略 */ }
                });
            }
        }
        catch { _pet = null; }
    }

    // ------------------------------------------------------------------
    // OTA 自动更新
    // ------------------------------------------------------------------

    /// <summary>启动时静默检查更新，有新版则提示到左下角按钮。</summary>
    private async Task AutoCheckUpdateAsync()
    {
        if (!_config.AutoCheckUpdate || string.IsNullOrWhiteSpace(_config.UpdateUrl)) return;
        var info = await Updater.CheckAsync(_config.UpdateUrl);
        if (info == null) return;
        _pendingUpdate = info;
        UpdateBtn.Content = $"⬆ 有新版 v{info.Version}";
        UpdateStatusText.Text = $"发现新版本 v{info.Version}（当前 v{Updater.CurrentVersion}），点上方的按钮升级。";
        UpdateStatusText.Visibility = Visibility.Visible;
    }

    private async void UpdateClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_config.UpdateUrl))
        {
            var tip = new ContentDialog
            {
                Title = "未配置更新源",
                Content = new TextBlock
                {
                    Text = "请到 ⚙ 设置里填写「更新源」：填 GitHub 仓库（owner/repo），例如 lichenghan/deepsleep。" +
                           "\n\n也可以填 update.json 的完整 URL 或本地路径；把安装包发到 GitHub Releases 后会自动读取最新版。",
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "知道了",
                XamlRoot = RootGrid.XamlRoot,
            };
            await tip.ShowAsync();
            return;
        }

        UpdateBtn.IsEnabled = false;
        try
        {
            UpdateInfo? info = _pendingUpdate ?? await Updater.CheckAsync(_config.UpdateUrl);
            if (info == null)
            {
                UpdateBtn.Content = "⬆ 检查更新";
                UpdateStatusText.Text = $"已是最新版本（v{Updater.CurrentVersion}）。";
                UpdateStatusText.Visibility = Visibility.Visible;
                return;
            }
            _pendingUpdate = info;
            string notes = string.IsNullOrWhiteSpace(info.Notes) ? "（无更新说明）" : info.Notes;
            var confirm = new ContentDialog
            {
                Title = $"发现新版本 v{info.Version}",
                Content = new TextBlock
                {
                    Text = $"当前版本：v{Updater.CurrentVersion}\n\n更新内容：\n{notes}\n\n" +
                           "升级会下载安装包，退出后静默覆盖安装并自动重启。用户数据（API Key、对话历史、记忆、自训练模型）不受影响。",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "立即升级",
                CloseButtonText = "稍后",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            await RunUpdateAsync(info);
        }
        finally
        {
            UpdateBtn.IsEnabled = true;
        }
    }

    private async Task RunUpdateAsync(UpdateInfo info)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Width = 400, Height = 14 };
        var txt = new TextBlock { Text = "正在下载更新包…", Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel();
        panel.Children.Add(txt);
        panel.Children.Add(bar);

        var cts = new CancellationTokenSource();
        var dlg = new ContentDialog
        {
            Title = "正在升级",
            Content = panel,
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };
        dlg.CloseButtonClick += (_, _) => cts.Cancel();
        _ = dlg.ShowAsync();

        bool launching = false;
        try
        {
            var progress = new Progress<double>(p =>
            {
                bar.Value = p;
                txt.Text = $"正在下载更新包…{p:0}%";
            });
            string installer = await Updater.DownloadAsync(info, progress, cts.Token);
            txt.Text = "下载完成，正在启动升级程序…";
            string exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "deepsleep.exe");
            string installDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
            Updater.ApplyAndRestart(installer, installDir, exePath);
            launching = true;
        }
        catch (OperationCanceledException)
        {
            txt.Text = "已取消升级。";
        }
        catch (Exception ex)
        {
            txt.Text = "升级失败：" + ex.Message;
        }
        if (!launching) await Task.Delay(1800);
        dlg.Hide();
        if (launching) Close();   // 关闭程序，让升级脚本覆盖文件并重启
    }

    private async Task ProbeLlmAsync()
    {
        _llmOnline = await _engine.PingLlmAsync();
        if (!_llmOnline)
        {
            string? found = await AutoDetectOllamaAsync();
            if (found != null)
            {
                _config.OllamaUrl = found;
                _config.Save();
                _engine.ApplyConfig(_config);
                _agent.ApplyConfig(_config);
                _clusterAgent.ApplyConfig(_config);
                _llmOnline = await _engine.PingLlmAsync();
            }
        }
        UpdateAgentStatus();
        UpdateClusterStatus();
    }

    private static readonly int[] OllamaPorts = { 11434, 11435, 11436, 1234, 8080, 8081, 8000, 5000 };

    private async Task<string?> AutoDetectOllamaAsync()
    {
        foreach (string host in new[] { "http://127.0.0.1", "http://localhost" })
            foreach (int port in OllamaPorts)
            {
                string baseUrl = $"{host}:{port}";
                if (string.Equals(baseUrl, _config.OllamaUrl, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(1.2) };
                    using var resp = await hc.GetAsync(baseUrl + "/api/tags");
                    if (resp.IsSuccessStatusCode) return baseUrl;
                }
                catch { /* 该端口无服务，继续扫描 */ }
            }
        return null;
    }

    private bool _llmOnline = false;
    private double _bubbleMaxWidth = 480;
    private static readonly Brush AiBrush = new SolidColorBrush(Color.FromArgb(255, 74, 144, 217));

    /// <summary>免费大模型预设（官方免费档、OpenAI 兼容；选一个自动填地址和模型名）。</summary>
    private static readonly (string Name, string Url, string Model)[] FreePresets =
    {
        ("智谱 GLM-4.7-Flash（永久免费 · 国内直连）",
            "https://open.bigmodel.cn/api/paas/v4/chat/completions", "glm-4.7-flash"),
        ("硅基流动 SiliconFlow（注册送 2000 万 token）",
            "https://api.siliconflow.cn/v1/chat/completions", "Qwen/Qwen3-8B"),
        ("Groq 免费额度（Llama 3.3 70B）",
            "https://api.groq.com/openai/v1/chat/completions", "llama-3.3-70b-versatile"),
        ("Gemini 2.5 Flash 免费档",
            "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "gemini-2.5-flash"),
        ("OpenRouter 免费模型（30+）",
            "https://openrouter.ai/api/v1/chat/completions", "qwen/qwen3-coder:free"),
    };

    /// <summary>气泡最大宽度随窗口大小自适应（智能缩放）。</summary>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_counterCur == null) return;
        double usable = Math.Max(280, e.NewSize.Width - 240 - 28);   // 侧边栏 240 + 主区左右 padding
        _bubbleMaxWidth = Math.Clamp(usable * 0.72, 200, 560);
        ChatItem.DefaultBubbleMaxWidth = _bubbleMaxWidth;
        foreach (var it in AgentItems) it.BubbleMaxWidth = _bubbleMaxWidth;
    }

    private string Now()
    {
        var t = DateTime.Now;
        return $"{t:HH:mm}";
    }

    private bool ShouldShowTime()
    {
        string now = Now();
        if (now != _lastTime)
        {
            _lastTime = now;
            return true;
        }
        return false;
    }

    private void ScrollToEnd()
    {
        AgentScroll.UpdateLayout();
        AgentScroll.ChangeView(null, AgentScroll.ScrollableHeight, null);
        UpdateAgentScrollBottomBtn();
    }

    private void AgentScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => UpdateAgentScrollBottomBtn();

    private void AgentScrollBottomClick(object sender, RoutedEventArgs e)
    {
        ScrollToEnd();
        DispatcherQueue.TryEnqueue(() => ScrollToEnd());
    }

    private void UpdateAgentScrollBottomBtn()
    {
        if (AgentScrollBottomBtn == null) return;
        bool nearBottom = AgentScroll.ScrollableHeight <= 0 ||
                          AgentScroll.VerticalOffset >= AgentScroll.ScrollableHeight - 40;
        AgentScrollBottomBtn.Visibility = nearBottom ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClusterScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => UpdateClusterScrollBottomBtn();

    private void ClusterScrollBottomClick(object sender, RoutedEventArgs e)
    {
        ScrollToEndCluster();
        DispatcherQueue.TryEnqueue(() => ScrollToEndCluster());
    }

    private void UpdateClusterScrollBottomBtn()
    {
        if (ClusterScrollBottomBtn == null) return;
        bool nearBottom = ClusterScroll.ScrollableHeight <= 0 ||
                          ClusterScroll.VerticalOffset >= ClusterScroll.ScrollableHeight - 40;
        ClusterScrollBottomBtn.Visibility = nearBottom ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>打开/切换会话后延迟滚动到最底部（等布局完成后跳转）。</summary>
    private void ScrollToBottomDeferred()
    {
        ScrollToEnd();
        ScrollToEndCluster();
        DispatcherQueue.TryEnqueue(() =>
        {
            ScrollToEnd();
            ScrollToEndCluster();
        });
    }

    /// <summary>打字机效果：按固定节奏逐段填充消息文字，会话被清空时自动停止。</summary>
    private async Task TypewriteAsync(ChatItem item, string full, ObservableCollection<ChatItem> list)
    {
        if (string.IsNullOrEmpty(full)) return;
        int step = full.Length > 400 ? 4 : 2;   // 长文加快，短文逐字
        int shown = 0;
        int tick = 0;
        while (shown < full.Length)
        {
            if (!list.Contains(item)) return;   // 会话已清空/消息被移除则停止
            shown = Math.Min(full.Length, shown + step);
            item.Text = full[..shown];
            tick++;
            if (tick % 5 == 0)
            {
                AgentScroll.UpdateLayout();
                AgentScroll.ChangeView(null, AgentScroll.ScrollableHeight, null);
            }
            await Task.Delay(20);
        }
        ScheduleSave();   // 打字结束，把完整文本写入磁盘
    }

    // ------------------------------------------------------------------
    // AI 助手
    // ------------------------------------------------------------------

    private void OnAgentMessage(int sid, AgentMessage m)
    {
        Conversation? conv = _agentConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv == null) return;
        // 若上一轮流式残留了气泡（如工具调用前说了半句话），先清掉再展示正式消息
        if (_streamItems.TryGetValue(sid, out var streamedItem) && conv.Items.Contains(streamedItem))
            conv.Items.Remove(streamedItem);
        _streamItems.Remove(sid);
        if (m.Role == "tool") RemoveAgentWorking(sid);   // 工具执行完，撤掉「正在…」状态气泡
        bool isUser = m.Role == "user";
        bool isTool = m.Role == "tool";
        bool isToolCallNotice = !isUser && !isTool && !string.IsNullOrEmpty(m.Meta);
        bool looksLikeToolJson = !isUser && !isTool && !isToolCallNotice &&
                                 m.Content.Contains("\"tool\"", StringComparison.Ordinal);
        bool typewrite = !isUser && !isTool && !isToolCallNotice && !looksLikeToolJson &&
                         !string.IsNullOrEmpty(m.Content);

        // 看图识别结果：作为 AI 气泡展示（带图片 + 识别文字），而不是系统消息
        bool isVisionResult = isTool && m.Meta == Agent.ToolVision;
        string? visionImagePath = null;
        string visionText = m.Content;
        if (isVisionResult)
        {
            foreach (var ln in m.Content.Split('\n'))
                if (ln.StartsWith("图片：", StringComparison.Ordinal))
                    visionImagePath = ln["图片：".Length..].Trim();
            int mark = m.Content.IndexOf("识别结果：", StringComparison.Ordinal);
            string result = mark >= 0 ? m.Content[(mark + "识别结果：".Length)..].Trim() : m.Content;
            visionText = string.IsNullOrWhiteSpace(result) ? m.Content : result;
        }

        var item = new ChatItem
        {
            IsSys = isTool && !isVisionResult,
            IsSelf = isUser,
            CanAccept = false,
            Text = typewrite ? "" : (isTool && !isVisionResult
                ? $"【工具「{m.Meta}」调用结果】\n{m.Content}"
                : isVisionResult ? visionText
                : isToolCallNotice ? m.Meta
                : looksLikeToolJson ? "（AI 的工具调用 JSON 未解析成功，已忽略，正在继续…）"
                : m.Content),
            Meta = isTool ? (isVisionResult ? "看图 · 识别结果" : "工具") : (isUser ? "你" : "AI"),
            AvatarText = "AI",
            SelfAvatarText = "你",
            AvatarBrush = AiBrush,
            Image = isVisionResult ? LoadImage(visionImagePath) : LoadImage(m.ImagePath),
            SessionId = sid,
            TimeStr = Now(),
            ShowTime = ShouldShowTime(),
        };
        conv.Items.Add(item);
        ScrollToEnd();
        // 工具结果回灌后模型仍要继续决策 → 在最下面重新显示「思考中」
        if (isTool && GetAgentState(sid) == AgentRunState.Running) AddAgentThinking(sid);
        if (m.Role == "user") MaybeAutoTitle(conv, m.Content);
        ScheduleSave();
        if (typewrite)
            _ = TypewriteAsync(item, m.Content, conv.Items);
    }

    private static ImageSource? LoadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try { return new BitmapImage(new Uri(path)); }
        catch { return null; }
    }

    private async void AgentSendClick(object sender, RoutedEventArgs e) => await AgentControlAsync();

    private async void AgentInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            StopAgent(_agentCur.Sid);
            return;
        }
        if (e.Key == VirtualKey.Shift) { _agentShiftDown = true; return; }
        if (e.Key != VirtualKey.Enter) return;
        bool ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                     & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        if (_agentShiftDown && !ctrl) return;   // Shift+回车（无 Ctrl）→ 换行
        e.Handled = true;              // 回车 → 发送/恢复，阻止输入框插入换行
        if (GetAgentState(_agentCur.Sid) == AgentRunState.Running) return;   // 运行中回车不打断
        await AgentControlAsync();
    }

    private void AgentInputKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Shift) _agentShiftDown = false;
    }

    private bool _agentShiftDown = false;

    private enum AgentRunState { Idle, Running, Stopped }

    private readonly Dictionary<int, AgentRunState> _agentStates = new();
    private readonly Dictionary<int, CancellationTokenSource> _agentCts = new();
    private readonly Dictionary<int, ChatItem> _streamItems = new();
    private readonly Dictionary<int, ChatItem> _agentThinking = new();
    private readonly Dictionary<int, ChatItem> _agentWorking = new();
    private readonly Dictionary<int, ChatItem> _clusterThinking = new();
    private DispatcherTimer? _agentThinkingTimer;
    private int _agentThinkingDots;
    private List<SkillInfo> _skills = new();

    /// <summary>Prompt 模板库（快捷指令）。</summary>
    private static readonly (string Name, string Prompt)[] PromptTemplates =
    {
        ("翻译", "请把下面的内容翻译成中文，保留语气：\n\n"),
        ("总结要点", "请用要点总结下面的内容：\n\n"),
        ("代码审查", "请审查下面这段代码，找出 bug/隐患/可读性问题并给出改进建议：\n\n"),
        ("生成代码", "请根据下面的需求生成完整可运行的代码：\n\n"),
        ("写周报", "请把下面的工作内容整理成周报格式：\n\n"),
    };

    private DesktopPet? _pet;

    private AgentRunState GetAgentState(int sid)
        => _agentStates.TryGetValue(sid, out var s) ? s : AgentRunState.Idle;

    private void SetAgentState(int sid, AgentRunState state)
    {
        _agentStates[sid] = state;
        UpdateAgentSendButton(sid);
        UpdateAgentBusy(sid);
    }

    private void UpdateAgentBusy(int sid)
    {
        if (AgentBusyText == null || sid != _agentCur.Sid) return;
        AgentBusyText.Text = GetAgentState(sid) switch
        {
            AgentRunState.Running => "● 思考中…",
            AgentRunState.Stopped => "⏸ 已停止（可点恢复）",
            _ => "",
        };
    }

    /// <summary>
    /// 录入 GitHub 令牌：多层加密后存到 data\github.token ——
    /// ① 随机主密钥用 Windows DPAPI(当前用户) 封装 ② AES-256-GCM 加密令牌本身
    /// ③ 用「机器 GUID + 用户 SID + 机器名」做 GCM 附加认证（换机器/换账号直接认证失败）
    /// ④ 文件 ACL 只留当前账号。令牌不进 config.json，也不写日志。
    /// </summary>
    private async Task AskTokenAsync()
    {
        var note = new TextBlock
        {
            Text = "令牌会用四层保护写进 data\\github.token：\n" +
                   "① Windows DPAPI 封装密钥　② AES-256-GCM 加密令牌\n" +
                   "③ 机器 GUID + 用户 SID 做附加认证（复制到别的电脑解不开）\n" +
                   "④ 文件权限只留当前 Windows 账号\n" +
                   "不会写进 config.json，也不会出现在日志或界面文字里。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
        var box = new PasswordBox
        {
            PlaceholderText = "粘贴 GitHub 令牌（ghp_… 或 github_pat_…）",
            Margin = new Thickness(0, 12, 0, 0),
        };
        var panel = new StackPanel();
        panel.Children.Add(note);
        panel.Children.Add(box);
        var dlg = new ContentDialog
        {
            Title = "录入 GitHub 令牌",
            Content = panel,
            PrimaryButtonText = "加密保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        string tok = box.Password.Trim();
        if (string.IsNullOrWhiteSpace(tok)) return;
        try
        {
            TokenVault.Save(tok, Updater.TokenPath);
            string read = Updater.LoadToken();
            bool ok = string.Equals(read, tok, StringComparison.Ordinal);
            await ShowTipAsync(ok ? "已加密保存" : "保存了但读回不一致",
                (ok ? "令牌已多层加密保存：" : "警告：读回校验不一致，请重试。\n") + Updater.TokenPath);
        }
        catch (Exception ex)
        {
            await ShowTipAsync("保存失败", ex.Message);
        }
        finally
        {
            box.Password = "";
        }
    }

    /// <summary>弹出独立的 GitHub 令牌录入窗口（脚本用 DPAPI 加密写盘，这里只负责拉起它）。</summary>
    private void RunTokenScript()
    {
        try
        {
            string script = Path.Combine(AppContext.BaseDirectory, "tools", "set-github-token.ps1");
            if (!File.Exists(script))   // 兼容旧安装（脚本曾经放在根目录）
                script = Path.Combine(AppContext.BaseDirectory, "set-github-token.ps1");
            if (!File.Exists(script))
            {
                _ = ShowTipAsync("找不到 set-github-token.ps1", "请把它放到程序目录（和 deepsleep.exe 同级）后重试。");
                return;
            }
            string dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Target \"{dir}\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            });
            Updater.LoadToken();   // 录入完立刻重读
        }
        catch (Exception ex)
        {
            _ = ShowTipAsync("启动令牌录入失败", ex.Message);
        }
    }

    private async Task ShowTipAsync(string title, string text)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "知道了",
            XamlRoot = RootGrid.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    private void CopyMessageClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChatItem item)
            CopyToClipboard(item.Text);
    }

    /// <summary>真·流式输出：增量文本实时追加到 AI 气泡。</summary>
    private void OnAgentDelta(int sid, string delta)
    {
        // 流式回调来自后台线程，必须切回 UI 线程再操作界面集合
        DispatcherQueue.TryEnqueue(() =>
        {
            var conv = _agentConvs.FirstOrDefault(c => c.Sid == sid);
            if (conv == null) return;
            if (_agentThinking.TryGetValue(sid, out var th) && conv.Items.Contains(th))
            {
                conv.Items.Remove(th);
                _agentThinking.Remove(sid);
            }
            if (!_streamItems.TryGetValue(sid, out var item) || !conv.Items.Contains(item))
            {
                item = new ChatItem
                {
                    IsSelf = false,
                    CanAccept = false,
                    Text = "",
                    Meta = "AI",
                    AvatarText = "AI",
                    SelfAvatarText = "你",
                    AvatarBrush = AiBrush,
                    SessionId = sid,
                    TimeStr = Now(),
                    ShowTime = ShouldShowTime(),
                };
                _streamItems[sid] = item;
                // 当成普通气泡：追加到最下面（上面可能已有工具调用/结果气泡）
                conv.Items.Add(item);
            }
            item.Text += delta;
            ScrollToEnd();
        });
    }

    private void DeleteMessageClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatItem item) return;
        // 删除用户消息时同步从 AI 会话上下文移除，避免上下文与显示不一致
        if (item.IsSelf && _agentConvs.Any(c => c.Sid == item.SessionId))
            _agent.RemoveMessage(item.SessionId, "user", item.Text);
        else if (item.IsSelf && _clusterConvs.Any(c => c.Sid == item.SessionId))
            _clusterAgent.RemoveMessage(item.SessionId, "user", item.Text);
        var conv = _agentConvs.FirstOrDefault(c => c.Sid == item.SessionId)
                   ?? _counterConvs.FirstOrDefault(c => c.Sid == item.SessionId)
                   ?? _clusterConvs.FirstOrDefault(c => c.Sid == item.SessionId);
        conv?.Items.Remove(item);
        ScheduleSave();
    }

    /// <summary>发送 / 停止 / 恢复 三态按钮主逻辑（按会话独立，互不阻塞）。</summary>
    private async Task AgentControlAsync()
    {
        int sid = _agentCur.Sid;
        switch (GetAgentState(sid))
        {
            case AgentRunState.Running:
                StopAgent(sid);
                return;
            case AgentRunState.Stopped:
                // 中止后如果输入框里发了新消息：当作全新一轮，不并入旧任务的恢复
                string newMsg = AgentInput.Text?.Trim() ?? "";
                if (newMsg.Length > 0)
                {
                    SetAgentState(sid, AgentRunState.Idle);
                    _agent.TruncateToLastUser(sid);   // 清掉被中止轮的残留上下文
                    await RunAgentAsync(sid, newMsg);
                    return;
                }
                await ResumeAgentAsync(sid);
                return;
            default:
                string text = AgentInput.Text?.Trim() ?? "";
                if (text.Length == 0) return;
                await RunAgentAsync(sid, text);
                return;
        }
    }

    private void StopAgent(int sid)
    {
        if (_agentCts.TryGetValue(sid, out var cts)) cts.Cancel();
    }

    private async Task RunAgentAsync(int sid, string text)
    {
        SetAgentState(sid, AgentRunState.Running);
        AddAgentThinking(sid);
        var cts = new CancellationTokenSource();
        _agentCts[sid] = cts;
        try
        {
            await _agent.RunAsync(sid, text, cts.Token);
            if (_agent.LastRunStopped)
            {
                SetAgentState(sid, AgentRunState.Stopped);
                AddAgentSys(sid, "⏸ 已停止，可点「恢复」从断点继续。");
            }
            else
            {
                SetAgentState(sid, AgentRunState.Idle);
            }
        }
        catch (OperationCanceledException)
        {
            SetAgentState(sid, AgentRunState.Stopped);
            AddAgentSys(sid, "⏸ 已停止，可点「恢复」从断点继续。");
        }
        catch (Exception ex)
        {
            AddAgentSys(sid, "AI 助手出错：" + ex.Message);
            SetAgentState(sid, AgentRunState.Idle);
        }
        finally
        {
            _agentCts.Remove(sid);
            _streamItems.Remove(sid);
            RemoveAgentThinking(sid);
            RemoveAgentWorking(sid);
            AgentInput.Text = "";
            UpdateAgentSendButton(sid);
            ScheduleSave();
        }
    }

    private async Task ResumeAgentAsync(int sid)
    {
        SetAgentState(sid, AgentRunState.Running);
        AddAgentThinking(sid);
        var cts = new CancellationTokenSource();
        _agentCts[sid] = cts;
        try
        {
            await _agent.ResumeAsync(sid, cts.Token);
            if (_agent.LastRunStopped)
            {
                SetAgentState(sid, AgentRunState.Stopped);
            }
            else
            {
                SetAgentState(sid, AgentRunState.Idle);
                AddAgentSys(sid, "✅ 已从断点完成。");
            }
        }
        catch (OperationCanceledException)
        {
            SetAgentState(sid, AgentRunState.Stopped);
        }
        catch (Exception ex)
        {
            AddAgentSys(sid, "AI 助手出错：" + ex.Message);
            SetAgentState(sid, AgentRunState.Idle);
        }
        finally
        {
            _agentCts.Remove(sid);
            _streamItems.Remove(sid);
            RemoveAgentThinking(sid);
            RemoveAgentWorking(sid);
            UpdateAgentSendButton(sid);
            ScheduleSave();
        }
    }

    private void AddAgentSys(int sid, string text)
    {
        var conv = _agentConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv == null) return;
        conv.Items.Add(new ChatItem { IsSys = true, Text = text, TimeStr = Now(), ShowTime = ShouldShowTime() });
        ScrollToEnd();
    }

    private void AddAgentThinking(int sid)
    {
        var conv = _agentConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv == null) return;
        // 当成普通气泡：按时间顺序追加到最下面；重入时先撤掉旧的，保证只有一个且始终在最下面
        if (_agentThinking.TryGetValue(sid, out var prev) && conv.Items.Contains(prev))
            conv.Items.Remove(prev);
        var item = new ChatItem
        {
            IsSelf = false,
            IsThinking = true,
            CanAccept = false,
            Text = "思考中",
            Meta = "AI",
            AvatarText = "AI",
            SelfAvatarText = "你",
            AvatarBrush = AiBrush,
            SessionId = sid,
            TimeStr = Now(),
            ShowTime = false,
        };
        _agentThinking[sid] = item;
        conv.Items.Add(item);
        StartAgentThinkingAnimation();
        ScrollToEnd();
    }

    /// <summary>AI 助手独立的「思考中」省略号动画。</summary>
    private void StartAgentThinkingAnimation()
    {
        _agentThinkingTimer?.Stop();
        _agentThinkingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _agentThinkingTimer.Tick += (s, e) =>
        {
            if (_agentThinking.Count == 0) { _agentThinkingTimer?.Stop(); return; }
            _agentThinkingDots = (_agentThinkingDots + 1) % 4;
            string dots = new('·', _agentThinkingDots);
            double op = _agentThinkingDots switch { 0 => 1.0, 1 => 0.55, 2 => 0.85, _ => 0.45 };
            foreach (var th in _agentThinking.Values)
            {
                th.Text = "思考中" + dots;
                th.ThinkingOpacity = op;
            }
        };
        _agentThinkingTimer.Start();
    }

    private void RemoveAgentThinking(int sid)
    {
        if (!_agentThinking.TryGetValue(sid, out var item)) return;
        var conv = _agentConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv != null && conv.Items.Contains(item)) conv.Items.Remove(item);
        _agentThinking.Remove(sid);
        if (_agentThinking.Count == 0)
        {
            _agentThinkingTimer?.Stop();
            _agentThinkingTimer = null;
            _agentThinkingDots = 0;
        }
    }

    /// <summary>工具开始执行时，在用户提问下方显示「正在…」状态气泡（与思考中同款视觉效果）。</summary>
    private void OnAgentToolStarted(int sid, string tool)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var conv = _agentConvs.FirstOrDefault(c => c.Sid == sid);
            if (conv == null) return;
            RemoveAgentWorking(sid);
            string label = tool switch
            {
                Agent.ToolRunCommand => "正在运行命令",
                Agent.ToolPython => "正在执行 Python 脚本",
                Agent.ToolImage => "正在生成图片",
                Agent.ToolVision => "正在识别图片",
                Agent.ToolSearch => "正在联网搜索",
                Agent.ToolFetch => "正在抓取网页",
                Agent.ToolOpenFile => "正在打开文件",
                Agent.ToolReadFile => "正在读取文件",
                Agent.ToolWriteFile => "正在写入文件",
                Agent.ToolRemember => "正在记录记忆",
                Agent.ToolForget => "正在更新记忆",
                _ => $"正在执行「{tool}」",
            };
            var item = new ChatItem
            {
                IsSelf = false,
                IsThinking = true,
                CanAccept = false,
                Text = label,
                Meta = "工具",
                AvatarText = "AI",
                SelfAvatarText = "你",
                AvatarBrush = AiBrush,
                SessionId = sid,
                TimeStr = Now(),
                ShowTime = false,
            };
            _agentWorking[sid] = item;
            conv.Items.Add(item);
            ScrollToEnd();
        });
    }

    private void RemoveAgentWorking(int sid)
    {
        if (!_agentWorking.TryGetValue(sid, out var item)) return;
        var conv = _agentConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv != null && conv.Items.Contains(item)) conv.Items.Remove(item);
        _agentWorking.Remove(sid);
    }

    /// <summary>发送按钮按当前会话状态显示：发送 / 停止 / 恢复。</summary>
    private void UpdateAgentSendButton(int sid)
    {
        if (AgentSendBtn == null || sid != _agentCur.Sid) return;
        AgentSendBtn.Content = GetAgentState(sid) switch
        {
            AgentRunState.Running => "停止",
            AgentRunState.Stopped => "恢复",
            _ => "发送",
        };
        if (RegenerateBtn != null)
            RegenerateBtn.IsEnabled = GetAgentState(sid) == AgentRunState.Idle && sid == _agentCur.Sid;
    }

    private void ThemeToggleClick(object sender, RoutedEventArgs e)
    {
        bool dark = ThemeToggle.IsChecked == true;
        RootGrid.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        ThemeToggle.Content = dark ? "☀️ 浅色" : "🌙 深色";
        _config.Theme = dark ? "dark" : "light";
        _config.Save();
    }

    private void FastModeClick(object sender, RoutedEventArgs e)
    {
        bool on = FastModeToggle.IsChecked == true;
        _agent.FastMode = on;
        _clusterAgent.FastMode = on;
        UpdateAgentStatus();
    }

    private string _agentBackendBefore = "auto";

    /// <summary>自训练开关：点亮后 AI 助手与集群走本地自训练模型（零依赖），再点恢复原后端。</summary>
    private void AgentLocalToggleClick(object sender, RoutedEventArgs e)
    {
        bool on = AgentLocalToggle.IsChecked == true;
        if (on)
        {
            _agentBackendBefore = _agent.Backend;
            _agent.Backend = "local";
            _clusterAgent.Backend = "local";
        }
        else
        {
            _agent.Backend = _agentBackendBefore;
            _clusterAgent.Backend = _agentBackendBefore;
        }
        UpdateAgentStatus();
        UpdateClusterStatus();
    }

    private async void SkillsClick(object sender, RoutedEventArgs e) => await ShowSkillsDialogAsync();

    private async Task ShowSkillsDialogAsync()
    {
        var list = new ListView { MinHeight = 220 };
        void Reload()
        {
            _skills = SkillStore.Load(_dataDir);
            _agent.Skills = _skills;
            _clusterAgent.Skills = _skills;
            list.ItemsSource = _skills.Select(s => $"{s.Name}　-　{s.Description}").ToList();
        }
        Reload();

        var urlBox = new TextBox { Header = "从 URL 安装（SKILL.md 直链）", PlaceholderText = "https://.../SKILL.md" };
        var installUrl = new Button { Content = "⬇️ 从 URL 安装", HorizontalAlignment = HorizontalAlignment.Stretch };
        installUrl.Click += async (_, _) =>
        {
            string u = urlBox.Text.Trim();
            if (u.Length == 0) return;
            try
            {
                await SkillStore.InstallUrl(_dataDir, u);
                urlBox.Text = "";
                Reload();
            }
            catch (Exception ex)
            {
                var err = new ContentDialog
                {
                    Title = "安装失败",
                    Content = ex.Message,
                    CloseButtonText = "确定",
                    XamlRoot = RootGrid.XamlRoot,
                };
                _ = err.ShowAsync();
            }
        };

        var installDir = new Button { Content = "📁 从本地文件夹安装（含子目录/资源）", HorizontalAlignment = HorizontalAlignment.Stretch };
        installDir.Click += async (_, _) =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            StorageFolder? f = await picker.PickSingleFolderAsync();
            if (f == null) return;
            SkillStore.InstallFolder(_dataDir, f.Path);
            Reload();
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "已安装技能（AI 识别技能名后会自动套用说明）：" });
        panel.Children.Add(list);
        panel.Children.Add(urlBox);
        panel.Children.Add(installUrl);
        panel.Children.Add(installDir);

        var dlg = new ContentDialog
        {
            Title = "技能管理",
            Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            CloseButtonText = "关闭",
            XamlRoot = RootGrid.XamlRoot,
        };
        await dlg.ShowAsync();
        _skills = SkillStore.Load(_dataDir);
        _agent.Skills = _skills;
        _clusterAgent.Skills = _skills;
    }

    private void PromptTemplateMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        foreach (var t in PromptTemplates)
        {
            var item = new MenuFlyoutItem { Text = t.Name };
            item.Click += (_, _) =>
            {
                AgentInput.Text = t.Prompt;
                AgentInput.Focus(FocusState.Programmatic);
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(sender as FrameworkElement);
    }

    /// <summary>重新生成：截掉最后一条回复，用同一个问题重跑。</summary>
    private void RegenerateClick(object sender, RoutedEventArgs e)
    {
        int sid = _agentCur.Sid;
        if (GetAgentState(sid) != AgentRunState.Idle) return;
        var conv = _agentCur;
        int lastUser = -1;
        for (int i = conv.Items.Count - 1; i >= 0; i--)
            if (conv.Items[i].IsSelf && !conv.Items[i].IsSys) { lastUser = i; break; }
        if (lastUser < 0) return;
        while (conv.Items.Count > lastUser + 1)
            conv.Items.RemoveAt(conv.Items.Count - 1);
        _ = RegenerateAgentAsync(sid);
    }

    private async Task RegenerateAgentAsync(int sid)
    {
        SetAgentState(sid, AgentRunState.Running);
        var cts = new CancellationTokenSource();
        _agentCts[sid] = cts;
        try
        {
            await _agent.RerunLastAsync(sid, cts.Token);
            if (_agent.LastRunStopped) SetAgentState(sid, AgentRunState.Stopped);
            else SetAgentState(sid, AgentRunState.Idle);
        }
        catch (OperationCanceledException)
        {
            SetAgentState(sid, AgentRunState.Stopped);
        }
        catch (Exception ex)
        {
            AddAgentSys(sid, "重新生成出错：" + ex.Message);
            SetAgentState(sid, AgentRunState.Idle);
        }
        finally
        {
            _agentCts.Remove(sid);
            _streamItems.Remove(sid);
            UpdateAgentSendButton(sid);
            ScheduleSave();
        }
    }

    private void AgentClearClick(object sender, RoutedEventArgs e)
    {
        _agent.ResetSession(_agentCur.Sid);
        _agentCur.Items.Clear();
    }

    // ------------------------------------------------------------------
    // 对话管理（左侧对话栏）
    // ------------------------------------------------------------------

    /// <summary>创建会话：kind=0 AI 助手 / 2 Agent 集群。</summary>
    private Conversation CreateConversation(int kind)
    {
        var conv = new Conversation
        {
            Title = $"对话 {_nextConvId++}",
            Sid = kind switch { 0 => _nextAgentSid++, 1 => _nextCounterSid++, _ => _nextClusterSid++ },
            Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        switch (kind)
        {
            case 0:
                _agent.NewSession(conv.Sid);
                _agentConvs.Add(conv);
                break;
            case 1:
                _engine.NewSession(conv.Sid);
                _counterConvs.Add(conv);
                break;
            default:
                _clusterAgent.NewSession(conv.Sid);
                _clusterConvs.Add(conv);
                break;
        }
        return conv;
    }

    private void NewConversationClick(object sender, RoutedEventArgs e)
    {
        int kind = MainPivot.SelectedIndex == 0 ? 0 : 2;
        Conversation conv = CreateConversation(kind);
        if (kind == 0)
        {
            _agentCur = conv;
            AgentList.ItemsSource = conv.Items;
        }
        else
        {
            _clusterCur = conv;
            ClusterList.ItemsSource = conv.Items;
        }
        _lastTime = "";
        conv.Items.Add(new ChatItem
        {
            IsSys = true,
            Text = "新对话已创建。",
            TimeStr = Now(),
            ShowTime = true,
        });
        RefreshConvList();
        ScrollToBottomDeferred();
        ScheduleSave();
        if (kind == 0) UpdateAgentSendButton(_agentCur.Sid);
        if (kind == 2) UpdateClusterSendButton(_clusterCur.Sid);
    }

    /// <summary>刷新左侧对话列表（按当前标签页显示对应会话）。</summary>
    private void RefreshConvList()
    {
        if (ConvList == null || _agentCur == null) return;   // InitializeComponent 阶段
        int kind = MainPivot.SelectedIndex == 0 ? 0 : 2;
        var full = kind == 0 ? _agentConvs : _clusterConvs;
        string q = ConvSearchBox?.Text?.Trim() ?? "";
        List<Conversation> items = string.IsNullOrEmpty(q)
            ? full.Where(c => c.IsPinned).Concat(full.Where(c => !c.IsPinned)).ToList()
            : full.Where(c => c.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
                  .OrderByDescending(c => c.IsPinned)
                  .ThenBy(c => full.IndexOf(c))
                  .ToList();
        ConvList.ItemsSource = items;
        var current = kind == 0 ? _agentCur : _clusterCur;
        if (string.IsNullOrEmpty(q) || items.Contains(current))
            ConvList.SelectedItem = current;
    }

    private void ConvSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => RefreshConvList();

    private void Pivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RefreshConvList();

    private void ConvList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConvList.SelectedItem is not Conversation conv) return;
        int kind = MainPivot.SelectedIndex == 0 ? 0 : 2;
        if (kind == 0 && conv != _agentCur)
        {
            _agentCur = conv;
            AgentList.ItemsSource = conv.Items;
        }
        else if (kind == 2 && conv != _clusterCur)
        {
            _clusterCur = conv;
            ClusterList.ItemsSource = conv.Items;
        }
        _lastTime = "";
        ScrollToBottomDeferred();
        if (kind == 0) UpdateAgentSendButton(_agentCur.Sid);
        if (kind == 2) UpdateClusterSendButton(_clusterCur.Sid);
    }

    // ------------------------------------------------------------------
    // 对话持久化 / 重命名 / 删除
    // ------------------------------------------------------------------

    private void LoadConversations()
    {
        var (agentDtos, counterDtos, clusterDtos) = ConversationStore.Load(_dataDir);
        foreach (var dto in agentDtos) _agentConvs.Add(RestoreConversation(dto, 0));
        foreach (var dto in counterDtos) _counterConvs.Add(RestoreConversation(dto, 1));
        foreach (var dto in clusterDtos) _clusterConvs.Add(RestoreConversation(dto, 2));
        if (_agentConvs.Count == 0) CreateConversation(0);
        if (_counterConvs.Count == 0) CreateConversation(1);
        if (_clusterConvs.Count == 0) CreateConversation(2);
        _agentCur = _agentConvs[^1];
        _counterCur = _counterConvs[^1];
        _clusterCur = _clusterConvs[^1];
        _nextAgentSid = _agentConvs.Max(c => c.Sid) + 1;
        _nextCounterSid = _counterConvs.Max(c => c.Sid) + 1;
        _nextClusterSid = _clusterConvs.Max(c => c.Sid) + 1;
        int maxN = 0;
        foreach (var c in _agentConvs.Concat(_counterConvs).Concat(_clusterConvs))
            if (int.TryParse(c.Title.Replace("对话 ", ""), out int n)) maxN = Math.Max(maxN, n);
        _nextConvId = maxN + 1;
    }

    private Conversation RestoreConversation(ConversationDto dto, int kind)
    {
        var conv = new Conversation
        {
            Title = string.IsNullOrWhiteSpace(dto.Title) ? $"对话 {_nextConvId++}" : dto.Title,
            Sid = dto.Sid,
            Created = dto.Created,
            AutoApprove = dto.AutoApprove,
            IsPinned = dto.IsPinned,
        };
        if (kind == 0) _agent.NewSession(conv.Sid);
        else if (kind == 1) _engine.NewSession(conv.Sid);
        else _clusterAgent.NewSession(conv.Sid);
        foreach (var m in dto.Messages)
        {
            conv.Items.Add(new ChatItem
            {
                Text = m.Text,
                Meta = m.Meta,
                IsSelf = m.IsSelf,
                IsSys = m.IsSys,
                Accepted = m.Accepted,
                CanAccept = kind == 1,
                TimeStr = m.TimeStr,
                ShowTime = m.ShowTime,
                SessionId = conv.Sid,
                AvatarText = kind switch { 0 => "AI", 1 => "侠", _ => "集" },
                SelfAvatarText = kind switch { 0 => "你", 1 => "理", _ => "你" },
                AvatarBrush = kind switch { 0 => AiBrush, 1 => new SolidColorBrush(Color.FromArgb(255, 0xB0, 0xB0, 0xB0)), _ => new SolidColorBrush(Color.FromArgb(255, 0x7C, 0x4D, 0xFF)) },
            });
        }
        return conv;
    }

    private void SaveConversations()
    {
        if (_counterCur == null) return;
        var agent = _agentConvs.Select(ToDto).ToList();
        var counter = _counterConvs.Select(ToDto).ToList();
        var cluster = _clusterConvs.Select(ToDto).ToList();
        ConversationStore.Save(_dataDir, agent, counter, cluster);
        SaveRuntimeState();
    }

    /// <summary>持久化运行时状态：Agent 会话上下文 + 各会话运行状态 + 集群运行记录。</summary>
    private void SaveRuntimeState()
    {
        if (_counterCur == null || _agent == null) return;
        var st = new RuntimeState();
        foreach (var kv in _agent.SnapshotSessions())
        {
            if (kv.Key >= 500000) continue;   // 集群 worker 会话不在此列
            st.AgentSessions.Add(new AgentSessionDto
            {
                Sid = kv.Key,
                Messages = kv.Value.Select(m => new AgentMsgDto
                {
                    Role = m.Role,
                    Content = Cap(m.Content, 4000),
                    Meta = m.Meta,
                    ImagePath = m.ImagePath,
                }).ToList(),
            });
        }
        foreach (var kv in _agentStates)
            if (kv.Value != AgentRunState.Idle)
                st.AgentStates[kv.Key] = kv.Value == AgentRunState.Running ? "running" : "stopped";
        foreach (var kv in _clusterRuns)
            st.ClusterRuns.Add(new ClusterRunDto
            {
                Sid = kv.Key,
                UserText = kv.Value.UserText,
                SummarySid = kv.Value.SummarySid,
                Workers = kv.Value.Workers.Select(w => new ClusterWorkerDto
                {
                    Sid = w.Sid,
                    Name = w.Name,
                    Task = w.Task,
                    Status = w.Status,
                    LogText = Cap(w.LogText, 2000),
                }).ToList(),
            });
        foreach (var kv in _clusterStates)
            if (kv.Value != AgentRunState.Idle)
                st.ClusterStates[kv.Key] = kv.Value == AgentRunState.Running ? "running" : "stopped";
        RuntimeStore.Save(_dataDir, st);
    }

    /// <summary>启动时恢复断点：会话上下文回填 Agent；运行中被杀的会话标记为「可恢复」。</summary>
    private void RestoreRuntimeState()
    {
        var st = RuntimeStore.Load(_dataDir);
        if (st == null) return;
        foreach (var s in st.AgentSessions)
            _agent.RestoreSession(s.Sid, s.Messages.Select(m => new AgentMessage
            {
                Role = m.Role,
                Content = m.Content,
                Meta = m.Meta,
                ImagePath = m.ImagePath,
            }));
        foreach (var kv in st.AgentStates)
        {
            _agentStates[kv.Key] = AgentRunState.Stopped;
            _agent.AppendHistory(kv.Key, new AgentMessage
            {
                Role = "tool",
                Meta = "中断标记",
                Content = "（上次运行被中断，未完成，可点恢复继续）",
            });
            AddAgentSys(kv.Key, "⏸ 上次运行被中断，可点「恢复」从断点继续。");
        }
        foreach (var r in st.ClusterRuns)
        {
            _clusterRuns[r.Sid] = new ClusterRun
            {
                UserText = r.UserText,
                SummarySid = r.SummarySid,
                Workers = r.Workers.Select(w => new ClusterAgent
                {
                    Sid = w.Sid,
                    Name = w.Name,
                    Task = w.Task,
                    Status = w.Status,
                    LogText = w.LogText,
                }).ToList(),
            };
            _clusterStates[r.Sid] = AgentRunState.Stopped;
            AddClusterSys(r.Sid, "⏸ 上次集群任务被中断，可点「恢复」继续执行。");
            foreach (var w in _clusterRuns[r.Sid].Workers)
                if (w.Sid >= _nextClusterSid) _nextClusterSid = w.Sid + 1;
            if (r.SummarySid >= _nextClusterSid) _nextClusterSid = r.SummarySid + 1;
        }
        foreach (var kv in st.ClusterStates)
            _clusterStates[kv.Key] = AgentRunState.Stopped;
        if (st.AgentStates.Count > 0 || st.ClusterRuns.Count > 0)
            ScheduleSave();
    }

    private static string Cap(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private static ConversationDto ToDto(Conversation c) => new()
    {
        Title = c.Title,
        Sid = c.Sid,
        Created = c.Created,
        AutoApprove = c.AutoApprove,
        IsPinned = c.IsPinned,
        Messages = c.Items.Select(m => new ChatMessageDto
        {
            Text = m.Text,
            Meta = m.Meta,
            IsSelf = m.IsSelf,
            IsSys = m.IsSys,
            Accepted = m.Accepted,
            TimeStr = m.TimeStr,
            ShowTime = m.ShowTime,
        }).ToList(),
    };

    /// <summary>防抖保存：改动后 1 秒内多次变化只写盘一次。</summary>
    private void ScheduleSave()
    {
        _saveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Stop();
        _saveTimer.Tick -= SaveTick;
        _saveTimer.Tick += SaveTick;
        _saveTimer.Start();
    }

    private void SaveTick(object? sender, object e)
    {
        _saveTimer?.Stop();
        SaveConversations();
    }

    /// <summary>新对话发送首条消息后，用内容自动命名（自定义名称不受影响）。</summary>
    private void MaybeAutoTitle(Conversation conv, string text)
    {
        if (!conv.Title.StartsWith("对话 ", StringComparison.Ordinal)) return;
        string t = MarkdownRenderer.ToPlainText(text).Trim();
        conv.Title = t.Length <= 16 ? t : t[..16] + "…";
    }

    private async Task RenameConversationAsync(Conversation conv)
    {
        var box = new TextBox { Text = conv.Title, MinWidth = 260 };
        var dlg = new ContentDialog
        {
            Title = "重命名对话",
            Content = box,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        string t = box.Text.Trim();
        if (t.Length == 0) return;
        conv.Title = t;
        ScheduleSave();
    }

    private async Task DeleteConversationAsync(Conversation conv)
    {
        int kind = _agentConvs.Contains(conv) ? 0 : _counterConvs.Contains(conv) ? 1 : 2;
        var list = kind switch { 0 => _agentConvs, 1 => _counterConvs, _ => _clusterConvs };
        var dlg = new ContentDialog
        {
            Title = "删除对话",
            Content = $"确认删除「{conv.Title}」？删除后不可恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        list.Remove(conv);
        if (kind == 0) { StopAgent(conv.Sid); _agent.ResetSession(conv.Sid); }
        else if (kind == 2)
        {
            StopCluster(conv.Sid);
            _clusterAgent.ResetSession(conv.Sid);
            if (_clusterRuns.Remove(conv.Sid, out var run))
            {
                foreach (var w in run.Workers)
                {
                    _clusterAgent.ResetSession(w.Sid);
                    _clusterStreamItems.Remove(w.Sid);
                }
                if (run.SummarySid != 0) _clusterStreamItems.Remove(run.SummarySid);
            }
            _clusterStates.Remove(conv.Sid);
            _clusterCts.Remove(conv.Sid);
        }
        if (list.Count == 0) CreateConversation(kind);   // 至少保留一个会话
        if (kind == 0)
        {
            _agentCur = _agentConvs[^1];
            AgentList.ItemsSource = _agentCur.Items;
        }
        else
        {
            _clusterCur = _clusterConvs[^1];
            ClusterList.ItemsSource = _clusterCur.Items;
        }
        _lastTime = "";
        RefreshConvList();
        ScrollToBottomDeferred();
        ScheduleSave();
    }

    private void ConvList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Conversation conv)
            _ = RenameConversationAsync(conv);
    }

    private void ConvList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Conversation conv) return;
        var menu = new MenuFlyout();
        var pin = new MenuFlyoutItem { Text = conv.IsPinned ? "取消置顶" : "置顶" };
        pin.Click += (_, _) => { conv.IsPinned = !conv.IsPinned; RefreshConvList(); ScheduleSave(); };
        var up = new MenuFlyoutItem { Text = "上移" };
        up.Click += (_, _) => MoveConversation(conv, -1);
        var down = new MenuFlyoutItem { Text = "下移" };
        down.Click += (_, _) => MoveConversation(conv, 1);
        var rename = new MenuFlyoutItem { Text = "重命名" };
        rename.Click += (_, _) => _ = RenameConversationAsync(conv);
        var del = new MenuFlyoutItem { Text = "删除" };
        del.Click += (_, _) => _ = DeleteConversationAsync(conv);
        menu.Items.Add(pin);
        menu.Items.Add(up);
        menu.Items.Add(down);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(rename);
        menu.Items.Add(del);
        menu.ShowAt(ConvList, e.GetPosition(ConvList));
    }

    private void MoveConversation(Conversation conv, int dir)
    {
        var list = _agentConvs.Contains(conv) ? _agentConvs : _counterConvs.Contains(conv) ? _counterConvs : _clusterConvs;
        int idx = list.IndexOf(conv);
        int target = idx + dir;
        if (idx < 0 || target < 0 || target >= list.Count) return;
        list.RemoveAt(idx);
        list.Insert(target, conv);
        RefreshConvList();
        ScheduleSave();
    }

    // ------------------------------------------------------------------
    // 上传文件 / 联网搜索
    // ------------------------------------------------------------------

    private async void UploadClick(object sender, RoutedEventArgs e)
    {
        int kind = MainPivot.SelectedIndex switch { 0 => 0, 1 => 1, _ => 2 };
        if (kind == 1) return;
        var conv = kind == 0 ? _agentCur : _clusterCur;
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(this));

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file == null) return;

        string dir = Path.Combine(_dataDir, "uploads");
        Directory.CreateDirectory(dir);
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(dir);
        await file.CopyAsync(folder, file.Name, NameCollisionOption.ReplaceExisting);
        string dest = Path.Combine(dir, file.Name);
        ulong size = (await file.GetBasicPropertiesAsync()).Size;
        long kb = (long)((size + 1023) / 1024);
        bool isImage = file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                       file.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                       file.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                       file.Name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                       file.Name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
                       file.Name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                       file.Name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase);

        var item = new ChatItem
        {
            IsSelf = true,
            CanAccept = false,
            Text = $"📎 上传文件：{dest}（{kb} KB）",
            Meta = "上传",
            Image = isImage ? LoadImage(dest) : null,
            AttachmentName = file.Name,
            AttachmentSize = $"{kb} KB",
            AvatarText = "AI",
            SelfAvatarText = "你",
            AvatarBrush = AiBrush,
            SessionId = conv.Sid,
            TimeStr = Now(),
            ShowTime = ShouldShowTime(),
        };
        conv.Items.Add(item);
        if (kind == 0) ScrollToEnd();
        else ScrollToEndCluster();
        string ctx = $"用户上传了文件：{dest}（{kb} KB）" +
                     (isImage ? "。这是图片，可用「看图」工具查看其内容。" : "。需要时用「读取文件」读取，或写代码处理它。");
        if (kind == 0) _agent.InjectContext(conv.Sid, ctx);
        else _clusterAgent.InjectContext(conv.Sid, ctx);
        ScheduleSave();
    }

    private void SearchToggleClick(object sender, RoutedEventArgs e)
    {
        bool on = SearchToggle.IsChecked == true;
        _agent.WebSearchEnabled = on;
        _clusterAgent.WebSearchEnabled = on;
        if (ClusterSearchToggle != null && ClusterSearchToggle.IsChecked != on)
            ClusterSearchToggle.IsChecked = on;
        UpdateAgentStatus();
        UpdateClusterStatus();
    }

    /// <summary>长期记忆：查看/编辑跨会话记忆（AI 助手与集群共用）。</summary>
    private async void MemoryClick(object sender, RoutedEventArgs e)
    {
        var box = new TextBox
        {
            Text = _agent.Memory?.AllText() ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 220,
            MaxHeight = 360,
            FontSize = 13,
            PlaceholderText = "每行一条记忆，如：\n用户是产品经理，偏好简洁的答复\n项目约定：代码写注释\n重要事实：周三有周会",
        };
        var dlg = new ContentDialog
        {
            Title = "🧠 长期记忆（跨会话生效）",
            Content = new ScrollViewer { Content = box, MaxHeight = 400 },
            PrimaryButtonText = "保存",
            SecondaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        ContentDialogResult res = await dlg.ShowAsync();
        if (res == ContentDialogResult.Primary)
        {
            _agent.Memory?.ReplaceAll(box.Text);
            if (_agent.Memory != null) _clusterAgent.Memory = _agent.Memory;
            AddAgentSys(_agentCur.Sid, $"🧠 已保存 {_agent.Memory.Entries.Count} 条长期记忆。");
        }
        else if (res == ContentDialogResult.Secondary)
        {
            _agent.Memory?.Clear();
            if (_agent.Memory != null) _clusterAgent.Memory = _agent.Memory;
            AddAgentSys(_agentCur.Sid, "🧠 长期记忆已清空。");
        }
    }

    // ------------------------------------------------------------------
    // Agent 集群：对话式 UI，一句话自动拆解 → 多 Agent 并行 → 指挥官汇总
    // ------------------------------------------------------------------

    private const string ClusterSummaryPrompt =
        "你是 Agent 集群总指挥。下面是各 Agent 的任务与输出，请汇总成结构化总结：关键结论、完成情况、产物路径、遗留问题、下一步建议。只输出中文总结，不要 JSON。";

    private static readonly Brush[] ClusterBrushes =
    {
        new SolidColorBrush(Color.FromArgb(255, 0x7C, 0x4D, 0xFF)),
        new SolidColorBrush(Color.FromArgb(255, 0x00, 0xB8, 0x9F)),
        new SolidColorBrush(Color.FromArgb(255, 0xF5, 0x7C, 0x00)),
        new SolidColorBrush(Color.FromArgb(255, 0xE9, 0x1E, 0x63)),
        new SolidColorBrush(Color.FromArgb(255, 0x00, 0x96, 0x88)),
        new SolidColorBrush(Color.FromArgb(255, 0x8E, 0x44, 0xAD)),
        new SolidColorBrush(Color.FromArgb(255, 0x39, 0x4B, 0x8E)),
        new SolidColorBrush(Color.FromArgb(255, 0xC0, 0x28, 0x28)),
        new SolidColorBrush(Color.FromArgb(255, 0x2E, 0x7D, 0x32)),
        new SolidColorBrush(Color.FromArgb(255, 0x6D, 0x4C, 0x41)),
    };

    private AgentRunState GetClusterState(int sid)
        => _clusterStates.TryGetValue(sid, out var s) ? s : AgentRunState.Idle;

    private void SetClusterState(int sid, AgentRunState state)
    {
        _clusterStates[sid] = state;
        UpdateClusterSendButton(sid);
        UpdateClusterBusy(sid);
    }

    private void UpdateClusterBusy(int sid)
    {
        if (ClusterBusyText == null || sid != _clusterCur.Sid) return;
        string running = "";
        if (_clusterRuns.TryGetValue(sid, out var run))
        {
            int n = run.Workers.Count(w => w.Status == "运行中");
            running = n > 0 ? $"{n}/{run.Workers.Count} 个 Agent 并行中" : "";
        }
        ClusterBusyText.Text = GetClusterState(sid) switch
        {
            AgentRunState.Running => running.Length > 0 ? "● " + running : "● 执行中…",
            AgentRunState.Stopped => "⏸ 已停止（可点恢复）",
            _ => "",
        };
    }

    private void UpdateClusterSendButton(int sid)
    {
        if (ClusterSendBtn == null || sid != _clusterCur.Sid) return;
        ClusterSendBtn.Content = GetClusterState(sid) switch
        {
            AgentRunState.Running => "停止",
            AgentRunState.Stopped => "恢复",
            _ => "发送",
        };
        if (ClusterRegenerateBtn != null)
            ClusterRegenerateBtn.IsEnabled = GetClusterState(sid) == AgentRunState.Idle && sid == _clusterCur.Sid;
    }

    private void UpdateClusterStatus()
    {
        if (_clusterAgent == null || ClusterStatusText == null) return;
        string backend = _clusterAgent.Backend switch
        {
            "api" => $"外部 API（{_config.ApiModel}）",
            "llm" => $"本地 Ollama（{_config.OllamaModel}）",
            "local" => "纯本地（自训练）",
            _ => !string.IsNullOrWhiteSpace(_config.ApiKey) ? $"自动 · API（{_config.ApiModel}）" : "自动 · 本地 Ollama",
        };
        int running = 0, total = 0;
        foreach (var c in _clusterConvs)
            if (_clusterRuns.TryGetValue(c.Sid, out var r))
            {
                total += r.Workers.Count;
                running += r.Workers.Count(w => w.Status == "运行中");
            }
        string lair = total > 0 ? $"　并行：{running}/{total}" : "";
        ClusterStatusText.Text =
            $"模式：boom · 全自动　后端：{backend}　联网：{(_clusterAgent.WebSearchEnabled ? "开" : "关")}" +
            $"　🧠 记忆开{(_clusterAgent.FastMode ? "　⚡ 极速" : "")}　工具：命令/Python/读写/抓取/图片/看图{lair}";
    }

    private void StopCluster(int sid)
    {
        if (_clusterCts.TryGetValue(sid, out var cts)) cts.Cancel();
    }

    private async void ClusterSendClick(object sender, RoutedEventArgs e) => await ClusterControlAsync();

    private async void ClusterInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { e.Handled = true; StopCluster(_clusterCur.Sid); return; }
        if (e.Key == VirtualKey.Shift) { _clusterShiftDown = true; return; }
        if (e.Key != VirtualKey.Enter) return;
        bool ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                     & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        if (_clusterShiftDown && !ctrl) return;
        e.Handled = true;
        if (GetClusterState(_clusterCur.Sid) == AgentRunState.Running) return;
        await ClusterControlAsync();
    }

    private void ClusterInputKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Shift) _clusterShiftDown = false;
    }

    private bool _clusterShiftDown = false;

    private async Task ClusterControlAsync()
    {
        int sid = _clusterCur.Sid;
        switch (GetClusterState(sid))
        {
            case AgentRunState.Running: StopCluster(sid); return;
            case AgentRunState.Stopped:
                // 中止后如果输入框里发了新消息：当作全新一轮，不并入旧任务的恢复
                string newClusterMsg = ClusterInput.Text?.Trim() ?? "";
                if (newClusterMsg.Length > 0)
                {
                    SetClusterState(sid, AgentRunState.Idle);
                    await RunClusterAsync(sid, newClusterMsg);
                    return;
                }
                await ResumeClusterAsync(sid);
                return;
            default:
                string text = ClusterInput.Text?.Trim() ?? "";
                if (text.Length == 0) return;
                await RunClusterAsync(sid, text);
                return;
        }
    }

    private void ClusterFastToggleClick(object sender, RoutedEventArgs e)
    {
        _clusterAgent.FastMode = ClusterFastToggle.IsChecked == true;
        UpdateClusterStatus();
    }

    private void ClusterSearchToggleClick(object sender, RoutedEventArgs e)
    {
        bool on = ClusterSearchToggle.IsChecked == true;
        _clusterAgent.WebSearchEnabled = on;
        _agent.WebSearchEnabled = on;
        if (SearchToggle != null && SearchToggle.IsChecked != on) SearchToggle.IsChecked = on;
        UpdateAgentStatus();
        UpdateClusterStatus();
    }

    private void ClusterUploadClick(object sender, RoutedEventArgs e) => UploadClick(sender, e);

    private async Task RunClusterAsync(int sid, string text, bool addUserBubble = true, bool reusePlan = false)
    {
        List<(string role, string task)>? preset = null;
        if (reusePlan && _clusterRuns.TryGetValue(sid, out var prev) && prev.Workers.Count > 0)
            preset = prev.Workers.Select(w => (w.Name, w.Task)).ToList();
        await RunClusterCoreAsync(sid, text, addUserBubble, preset);
    }

    private async Task RunClusterWithPlanAsync(int sid, string text, List<(string role, string task)> plan)
        => await RunClusterCoreAsync(sid, text, true, plan);

    private async Task RunClusterCoreAsync(int sid, string text, bool addUserBubble,
        List<(string role, string task)>? preset)
    {
        SetClusterState(sid, AgentRunState.Running);
        AddClusterThinking(sid);
        var cts = new CancellationTokenSource();
        _clusterCts[sid] = cts;
        var run = new ClusterRun { UserText = text };
        _clusterRuns[sid] = run;
        var conv = _clusterConvs.First(c => c.Sid == sid);
        try
        {
            if (addUserBubble)
            {
                conv.Items.Add(new ChatItem
                {
                    IsSelf = true, CanAccept = false, Text = text, Meta = "集群指令",
                    AvatarText = "AI", SelfAvatarText = "你", AvatarBrush = AiBrush,
                    SessionId = sid, TimeStr = Now(), ShowTime = ShouldShowTime(),
                });
                MaybeAutoTitle(conv, text);
                ScrollToEndCluster();
            }

            List<(string role, string task)> plan;
            if (preset != null && preset.Count >= 2) plan = preset;
            else plan = await PlanAndRetryAsync(text, cts.Token);

            foreach (var (role, task) in plan.Take(10))
            {
                var w = new ClusterAgent { Name = role, Task = task, Sid = _nextClusterSid++ };
                _clusterAgent.NewSession(w.Sid);
                run.Workers.Add(w);
            }
            AddClusterSys(sid, $"🧠 指挥官已拆解为 {run.Workers.Count} 个子任务，并行执行中：{string.Join("、", run.Workers.Select(w => w.Name))}");
            UpdateClusterStatus();

            await Task.WhenAll(run.Workers.Select(w => RunClusterWorkerAsync(run, w, cts.Token)));
            if (cts.IsCancellationRequested)
            {
                SetClusterState(sid, AgentRunState.Stopped);
                AddClusterSys(sid, "⏸ 集群已停止，可点「恢复」重新执行。");
                return;
            }

            int done = run.Workers.Count(w => w.Status == "完成");
            int failed = run.Workers.Count(w => w.Status == "失败");
            AddClusterSys(sid, $"📊 并行执行完成：{done} 完成 / {failed} 失败，指挥官汇总中…");
            run.SummarySid = _nextClusterSid++;
            _clusterAgent.NewSession(run.SummarySid);
            var sb = new StringBuilder();
            foreach (var w in run.Workers)
            {
                sb.AppendLine();
                sb.AppendLine($"=== {w.Name}（{w.Status}）===");
                sb.AppendLine($"任务：{w.Task}");
                sb.AppendLine(Clip(w.LogText, 1200));
            }
            string summary = await _clusterAgent.SummarizeAsync(ClusterSummaryPrompt, sb.ToString(), cts.Token,
                d => OnClusterDelta(run.SummarySid, d));
            if (cts.IsCancellationRequested)
            {
                SetClusterState(sid, AgentRunState.Stopped);
                AddClusterSys(sid, "⏸ 汇总已停止，可点「恢复」继续。");
                return;
            }
            if (!string.IsNullOrWhiteSpace(summary) && !_clusterStreamItems.ContainsKey(run.SummarySid))
                AddClusterBubble(run, run.SummarySid, summary, "指挥官", "指挥官汇总", true);
            else if (string.IsNullOrWhiteSpace(summary) && !_clusterStreamItems.ContainsKey(run.SummarySid))
                AddClusterSys(sid, "⚠️ 指挥官汇总失败（后端不可用或已限流）。");
            AddClusterSys(sid, "✅ 集群任务全部完成。");
            SetClusterState(sid, AgentRunState.Idle);
        }
        catch (OperationCanceledException)
        {
            SetClusterState(sid, AgentRunState.Stopped);
            AddClusterSys(sid, "⏸ 集群已停止，可点「恢复」重新执行。");
        }
        catch (Exception ex)
        {
            AddClusterSys(sid, "集群出错：" + ex.Message);
            SetClusterState(sid, AgentRunState.Idle);
        }
        finally
        {
            _clusterCts.Remove(sid);
            ClusterInput.Text = "";
            RemoveClusterThinking(sid);
            UpdateClusterSendButton(sid);
            ScheduleSave();
        }
    }

    private async Task<List<(string role, string task)>> PlanAndRetryAsync(string instruction, CancellationToken ct)
    {
        string planText = "";
        var plan = new List<(string, string)>();
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            AddClusterSys(_clusterCur.Sid, $"🧠 指挥官拆解中（第 {attempt} 次尝试）…");
            string feedback = attempt == 1 ? "" :
                "上次输出无效：必须输出严格 JSON 数组 [{\"角色\":...,\"任务\":...}]，至少 3 个子任务。上次输出：" + Clip(planText, 500);
            planText = await _clusterAgent.PlanAsync(instruction, feedback);
            plan = TryParsePlan(planText);
            if (plan.Count >= 3) break;
        }
        if (plan.Count < 2)
        {
            string brief = Clip(instruction, 80);
            AddClusterSys(_clusterCur.Sid,
                "⚠️ 指挥官拆分无效，已按「实现/测试/素材」保底拆成 3 个成员继续执行。\n\n指挥官输出：\n" + Clip(planText, 500));
            plan = new List<(string, string)>
            {
                ("实现", $"负责「{brief}」的具体实现，可写代码/脚本完成，产物保存到工作区。"),
                ("测试校验", $"对「{brief}」的产物进行测试与校验，报告问题与修复建议。"),
                ("素材与文档", $"为「{brief}」用「生成图片」准备图片素材（如需），并撰写说明文档。"),
            };
        }
        return plan.Take(10).ToList();
    }

    private async Task RunClusterWorkerAsync(ClusterRun run, ClusterAgent w, CancellationToken ct)
    {
        w.Status = "运行中";
        UpdateClusterBusy(ConvOfRun(run)?.Sid ?? -1);
        try
        {
            string result = await _clusterAgent.RunAsync(w.Sid, "你的任务：" + w.Task, ct);
            if (ct.IsCancellationRequested) { w.Status = "已停止"; return; }
            w.LogText = string.IsNullOrWhiteSpace(result) ? "（无输出）" : result.Trim();
            w.Status = (result.Contains("后端不可用", StringComparison.Ordinal) ||
                        result.Contains("出错", StringComparison.Ordinal) ||
                        result.Contains("失败", StringComparison.Ordinal)) ? "失败" : "完成";
            if (!string.IsNullOrWhiteSpace(w.LogText) && !_clusterStreamItems.ContainsKey(w.Sid))
                AddClusterBubble(run, w.Sid, w.LogText, WorkerAvatar(w.Name), $"Agent「{w.Name}」", false);
        }
        catch (OperationCanceledException) { w.Status = "已停止"; }
        catch (Exception ex) { w.Status = "失败"; w.LogText = "[错误] " + ex.Message; }
        finally
        {
            UpdateClusterBusy(ConvOfRun(run)?.Sid ?? -1);
            UpdateClusterStatus();
            ScheduleSave();
        }
    }

    private void OnClusterMessage(int sid, AgentMessage m)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var w = FindRunBySid(sid)?.Workers.FirstOrDefault(x => x.Sid == sid);
            if (w == null || m.Role == "user") return;
            if (m.Role == "tool")
            {
                string t = m.Content.Length > 300 ? m.Content[..300] + "…" : m.Content;
                w.LogText += $"[工具「{m.Meta}」] {t}\n";
                if (w.LogText.Length > 8000) w.LogText = w.LogText[^8000..];
            }
        });
    }

    private void OnClusterDelta(int sid, string delta)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var run = FindRunBySid(sid);
            var conv = run == null ? null : ConvOfRun(run);
            if (run == null || conv == null) return;
            if (_clusterThinking.TryGetValue(conv.Sid, out var th) && conv.Items.Contains(th))
            {
                conv.Items.Remove(th);
                _clusterThinking.Remove(conv.Sid);
            }
            if (!_clusterStreamItems.TryGetValue(sid, out var item) || !conv.Items.Contains(item))
            {
                bool isSummary = run.SummarySid == sid;
                string name = isSummary ? "指挥官" : run.Workers.FirstOrDefault(w => w.Sid == sid)?.Name ?? "Agent";
                item = MakeClusterBubble(run, sid, name, isSummary);
                _clusterStreamItems[sid] = item;
                conv.Items.Add(item);
            }
            item.Text += delta;
            ScrollToEndCluster();
        });
    }

    /// <summary>技能调用链：技能被自动匹配/补救启用时，在对应会话里显示一条系统提示。</summary>
    private void OnSkillUsed(int sid, string name)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var conv = _agentConvs.FirstOrDefault(c => c.Sid == sid)
                       ?? _clusterConvs.FirstOrDefault(c => c.Sid == sid);
            if (conv == null) return;
            conv.Items.Add(new ChatItem
            {
                IsSys = true,
                Text = $"🧩 技能调用链：自动匹配并启用「{name}」",
                TimeStr = Now(),
                ShowTime = ShouldShowTime(),
            });
            if (_agentConvs.Contains(conv)) ScrollToEnd();
            else ScrollToEndCluster();
            ScheduleSave();
        });
    }

    private ClusterRun? FindRunBySid(int sid)
    {
        foreach (var c in _clusterConvs)
            if (_clusterRuns.TryGetValue(c.Sid, out var r) &&
                (r.SummarySid == sid || r.Workers.Any(w => w.Sid == sid))) return r;
        return null;
    }

    private Conversation? ConvOfRun(ClusterRun run)
    {
        foreach (var c in _clusterConvs)
            if (_clusterRuns.TryGetValue(c.Sid, out var r) && r == run) return c;
        return null;
    }

    private ChatItem MakeClusterBubble(ClusterRun run, int sid, string name, bool isSummary)
    {
        int idx = run.Workers.FindIndex(w => w.Sid == sid);
        return new ChatItem
        {
            IsSelf = false, CanAccept = false, Text = "",
            Meta = isSummary ? "指挥官汇总" : $"Agent「{name}」",
            AvatarText = isSummary ? "指挥" : WorkerAvatar(name),
            SelfAvatarText = "你",
            AvatarBrush = isSummary ? new SolidColorBrush(Color.FromArgb(255, 0xF0, 0x9F, 0x1F))
                                    : ClusterBrushes[Math.Max(0, idx % ClusterBrushes.Length)],
            SessionId = sid, TimeStr = Now(), ShowTime = ShouldShowTime(),
        };
    }

    private void AddClusterBubble(ClusterRun run, int sid, string text, string name, string meta, bool isSummary)
    {
        var conv = ConvOfRun(run);
        if (conv == null) return;
        var item = MakeClusterBubble(run, sid, name, isSummary);
        item.Meta = meta;
        item.Text = text;
        conv.Items.Add(item);
        _clusterStreamItems[sid] = item;
        ScrollToEndCluster();
    }

    private void AddClusterSys(int sid, string text)
    {
        var conv = _clusterConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv == null) return;
        conv.Items.Add(new ChatItem { IsSys = true, Text = text, TimeStr = Now(), ShowTime = ShouldShowTime() });
        ScrollToEndCluster();
    }

    private void AddClusterThinking(int sid)
    {
        var conv = _clusterConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv == null) return;
        var item = new ChatItem
        {
            IsSelf = false,
            IsThinking = true,
            CanAccept = false,
            Text = "思考中",
            Meta = "AI",
            AvatarText = "AI",
            SelfAvatarText = "你",
            AvatarBrush = AiBrush,
            SessionId = sid,
            TimeStr = Now(),
            ShowTime = false,
        };
        _clusterThinking[sid] = item;
        conv.Items.Add(item);
        StartClusterThinkingAnimation();
        ScrollToEndCluster();
    }

    private void StartClusterThinkingAnimation()
    {
        _clusterThinkingTimer?.Stop();
        _clusterThinkingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _clusterThinkingTimer.Tick += (s, e) =>
        {
            if (_clusterThinking.Count == 0) { _clusterThinkingTimer?.Stop(); return; }
            _clusterThinkingDots = (_clusterThinkingDots + 1) % 4;
            string dots = new('·', _clusterThinkingDots);
            double op = _clusterThinkingDots switch { 0 => 1.0, 1 => 0.55, 2 => 0.85, _ => 0.45 };
            foreach (var th in _clusterThinking.Values)
            {
                th.Text = "思考中" + dots;
                th.ThinkingOpacity = op;
            }
        };
        _clusterThinkingTimer.Start();
    }

    private void RemoveClusterThinking(int sid)
    {
        if (!_clusterThinking.TryGetValue(sid, out var item)) return;
        var conv = _clusterConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv != null && conv.Items.Contains(item)) conv.Items.Remove(item);
        _clusterThinking.Remove(sid);
        if (_clusterThinking.Count == 0)
        {
            _clusterThinkingTimer?.Stop();
            _clusterThinkingTimer = null;
            _clusterThinkingDots = 0;
        }
    }

    private void ScrollToEndCluster()
    {
        if (ClusterScroll == null) return;
        ClusterScroll.UpdateLayout();
        ClusterScroll.ChangeView(null, ClusterScroll.ScrollableHeight, null);
        UpdateClusterScrollBottomBtn();
    }

    private static string WorkerAvatar(string name)
    {
        string t = name.Trim();
        return t.Length == 0 ? "A" : t.Length <= 2 ? t : t[..2];
    }

    private async Task ResumeClusterAsync(int sid)
    {
        if (!_clusterRuns.TryGetValue(sid, out var run) || string.IsNullOrWhiteSpace(run.UserText))
        {
            AddClusterSys(sid, "没有可恢复的集群任务。");
            SetClusterState(sid, AgentRunState.Idle);
            return;
        }
        TruncateAfterLastUserMessage(sid, run.UserText);
        foreach (var w in run.Workers) { _clusterAgent.ResetSession(w.Sid); _clusterStreamItems.Remove(w.Sid); }
        if (run.SummarySid != 0) _clusterStreamItems.Remove(run.SummarySid);
        AddClusterSys(sid, "🔄 已从断点重新执行集群任务…");
        await RunClusterAsync(sid, run.UserText, false, true);
    }

    private async void ClusterRegenerateClick(object sender, RoutedEventArgs e)
    {
        int sid = _clusterCur.Sid;
        if (!_clusterRuns.TryGetValue(sid, out var run) || string.IsNullOrWhiteSpace(run.UserText)) return;
        TruncateAfterLastUserMessage(sid, run.UserText);
        foreach (var w in run.Workers) { _clusterAgent.ResetSession(w.Sid); _clusterStreamItems.Remove(w.Sid); }
        if (run.SummarySid != 0) _clusterStreamItems.Remove(run.SummarySid);
        AddClusterSys(sid, "↻ 重新生成集群结果…");
        await RunClusterAsync(sid, run.UserText, false);
    }

    private void TruncateAfterLastUserMessage(int sid, string userText)
    {
        var conv = _clusterConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv == null) return;
        int last = -1;
        for (int i = conv.Items.Count - 1; i >= 0; i--)
            if (conv.Items[i].IsSelf && !conv.Items[i].IsSys && conv.Items[i].Text == userText) { last = i; break; }
        while (conv.Items.Count > last + 1) conv.Items.RemoveAt(conv.Items.Count - 1);
    }

    private void ClusterClearClick(object sender, RoutedEventArgs e)
    {
        int sid = _clusterCur.Sid;
        StopCluster(sid);
        if (_clusterRuns.TryGetValue(sid, out var run))
        {
            foreach (var w in run.Workers) _clusterAgent.ResetSession(w.Sid);
            foreach (var w in run.Workers) _clusterStreamItems.Remove(w.Sid);
            if (run.SummarySid != 0) _clusterStreamItems.Remove(run.SummarySid);
        }
        _clusterAgent.ResetSession(sid);
        _clusterRuns.Remove(sid);
        _clusterCur.Items.Clear();
        SetClusterState(sid, AgentRunState.Idle);
        ScheduleSave();
    }

    private void ClusterTemplateMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        foreach (var t in ClusterTemplates)
        {
            var item = new MenuFlyoutItem { Text = t.Name };
            item.Click += (_, _) => ApplyClusterTemplate(t);
            menu.Items.Add(item);
        }
        menu.ShowAt(sender as FrameworkElement);
    }

    private async void ApplyClusterTemplate((string Name, (string Role, string Task)[] Members) t)
    {
        int sid = _clusterCur.Sid;
        if (GetClusterState(sid) == AgentRunState.Running) return;
        string text = $"使用分工模板「{t.Name}」：\n" + string.Join("\n", t.Members.Select(m => $"{m.Role}：{m.Task}"));
        await RunClusterWithPlanAsync(sid, text, t.Members.Select(m => (m.Role, m.Task)).ToList());
    }

    private static string Clip(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    /// <summary>从指挥官输出中解析 [{"角色":...,"任务":...}] 计划。</summary>
    private static List<(string role, string task)> TryParsePlan(string text)
    {
        var list = new List<(string, string)>();
        // 指挥官可能先写一段话再输出 JSON 数组：遍历所有 [ 位置，取第一个能解析且含任务的数组
        int idx = text.IndexOf('[');
        while (idx >= 0)
        {
            int end = FindJsonArrayEnd(text, idx);
            if (end > idx)
            {
                try
                {
                    var candidate = new List<(string, string)>();
                    using var doc = System.Text.Json.JsonDocument.Parse(text[idx..end]);
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        string role = GetJsonStr(el, "角色") ?? GetJsonStr(el, "role") ?? GetJsonStr(el, "名称") ?? $"Agent {candidate.Count + 1}";
                        string task = GetJsonStr(el, "任务") ?? GetJsonStr(el, "task") ?? "";
                        if (!string.IsNullOrWhiteSpace(task))
                            candidate.Add((role, task));
                    }
                    if (candidate.Count > 0)
                    {
                        list = candidate;
                        break;
                    }
                }
                catch { /* 尝试下一个 [ */ }
            }
            idx = text.IndexOf('[', idx + 1);
        }
        return list;
    }

    private static int FindJsonArrayEnd(string s, int start)
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
            else if (c == '[') depth++;
            else if (c == ']') { depth--; if (depth == 0) return i + 1; }
        }
        return -1;
    }

    private static string? GetJsonStr(System.Text.Json.JsonElement el, string name)
    {
        if (el.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (el.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
            return v.GetString();
        return null;
    }

    /// <summary>AI 助手三模式：chat=纯聊天 / work=命令需确认 / boom=全自动。</summary>
    private void AgentModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_agent == null) return;   // InitializeComponent 阶段
        _agent.RunMode = (sender as ComboBox)?.SelectedIndex switch
        {
            0 => "chat",
            2 => "boom",
            _ => "work",
        };
        UpdateAgentStatus();
    }

    /// <summary>work 模式执行命令/脚本前的确认对话框；勾选后可对本对话永久提权（不再询问）。</summary>
    private async Task<bool> ConfirmCommandAsync(int sid, string detail)
    {
        var conv = _agentConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv?.AutoApprove == true) return true;   // 已提权：本对话内直接放行

        var chk = new CheckBox
        {
            Content = "本次对话内始终允许执行命令/脚本（提高权限）",
            IsChecked = false,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = detail,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(chk);

        var dlg = new ContentDialog
        {
            Title = "允许执行命令？",
            Content = panel,
            PrimaryButtonText = "允许",
            CloseButtonText = "拒绝",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        bool ok = await dlg.ShowAsync() == ContentDialogResult.Primary;
        if (ok && chk.IsChecked == true && conv != null)
        {
            conv.AutoApprove = true;
            ScheduleSave();
        }
        return ok;
    }

    private async Task InstallOllamaAsync()
    {
        AddAgentSys(_agentCur.Sid, "⬇️ 开始一键安装 Ollama + 大模型…");
        try
        {
            if (!IsOllamaInstalled())
            {
                string setup = Path.Combine(Path.GetTempPath(), "OllamaSetup.exe");
                AddAgentSys(_agentCur.Sid, "⬇️ 正在下载 Ollama 安装包（约 200MB）…");
                using (var hc = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
                {
                    byte[] bytes = await hc.GetByteArrayAsync("https://ollama.com/download/OllamaSetup.exe");
                    await File.WriteAllBytesAsync(setup, bytes);
                }
                AddAgentSys(_agentCur.Sid, "⬇️ 安装包下载完成，正在静默安装…");
                Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true });
                for (int i = 0; i < 90; i++)
                {
                    await Task.Delay(2000);
                    if (IsOllamaInstalled()) break;
                }
            }
            if (!IsOllamaInstalled())
            {
                AddAgentSys(_agentCur.Sid, "❌ Ollama 安装失败，请手动到 ollama.com/download 安装后重试。");
                return;
            }
            AddAgentSys(_agentCur.Sid, "✅ Ollama 已就绪。正在下载大模型 qwen3:4b 与 qwen2.5:1.5b（首次下载需几分钟）…");
            await RunInstallCmdAsync("ollama pull qwen3:4b");
            await RunInstallCmdAsync("ollama pull qwen2.5:1.5b");
            _config.OllamaModel = "qwen2.5:1.5b";
            _config.OllamaModelStrong = "qwen3:4b";
            _config.UseOllama = true;
            _config.Save();
            _agent.Backend = "llm";
            _clusterAgent.Backend = "llm";
            _agent.ApplyConfig(_config);
            _clusterAgent.ApplyConfig(_config);
            UpdateAgentStatus();
            UpdateClusterStatus();
            AddAgentSys(_agentCur.Sid, "🎉 Ollama + 大模型安装完成，已切换到本地模型（不花 API 额度）。");
        }
        catch (Exception ex)
        {
            AddAgentSys(_agentCur.Sid, "❌ 一键安装失败：" + ex.Message);
        }
    }

    private static bool IsOllamaInstalled()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ollama", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task RunInstallCmdAsync(string cmd)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + cmd)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (p == null) return;
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(15));
    }

    private void UpdateAgentStatus()
    {
        if (_agent == null) return;   // InitializeComponent 阶段尚未初始化
        string backend = _agent.Backend switch
        {
            "api" => $"外部 API（{_config.ApiModel}）",
            "llm" => $"本地 Ollama（{_config.OllamaModel}）",
            "local" => "纯本地",
            _ => !string.IsNullOrWhiteSpace(_config.ApiKey)
                ? $"自动 · API（{_config.ApiModel}）"
                : "自动 · 本地 Ollama",
        };
        string runMode = _agent.RunMode switch
        {
            "chat" => "chat · 纯聊天（工具禁用）",
            "boom" => "boom · 全自动（命令免确认）",
            _ => "work · 命令需确认",
        };
        string tools = _agent.RunMode == "chat"
            ? "工具：无"
            : "工具：命令 / Python / 生成图片 / 看图 / 抓取网页 / 联网搜索 / 读写文件";
        string search = _agent.WebSearchEnabled ? "🌐 联网开" : "联网关";
        string fast = _agent.FastMode ? "⚡ 极速" : "";
        AgentStatusText.Text = $"模式：{runMode}　后端：{backend}　{search}　🧠 记忆开　{fast}　{tools}";
    }

    /// <summary>把回怼内容写入剪贴板（WinUI 3 剪贴板 API）。</summary>
    private static void CopyToClipboard(string text)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch
        {
            // 剪贴板被占用时静默失败，不影响主流程
        }
    }

    private async void SettingsClick(object sender, RoutedEventArgs e)
    {
        // 设置面板：可改 Ollama 模型、API 地址/模型/key
        var panel = new StackPanel { Spacing = 8 };

        var lblApi = new TextBlock { Text = "外部 API（OpenAI 兼容，如 DeepSeek）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        panel.Children.Add(lblApi);

        var apiUrl = new TextBox { Header = "API 地址", Text = _config.ApiUrl, PlaceholderText = "https://api.deepseek.com/chat/completions" };
        var apiModel = new TextBox { Header = "API 模型名", Text = _config.ApiModel, PlaceholderText = "deepseek-v4-flash" };
        var apiKey = new PasswordBox { Header = "API Key", Password = _config.ApiKey, PlaceholderText = "sk-..." };

        var presets = new ComboBox
        {
            Header = "免费模型预设（选中后自动填地址和模型名，只需再填自己的免费 Key）",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        presets.Items.Add("（手动配置）");
        foreach (var p in FreePresets) presets.Items.Add(p.Name);
        presets.SelectedIndex = 0;
        presets.SelectionChanged += (_, _) =>
        {
            int idx = presets.SelectedIndex - 1;
            if (idx < 0) return;
            apiUrl.Text = FreePresets[idx].Url;
            apiModel.Text = FreePresets[idx].Model;
        };

        var hint = new TextBlock
        {
            Text = "免费 Key 获取：智谱 open.bigmodel.cn（微信登录+实名，永久免费）｜硅基流动 siliconflow.cn（注册送 2000 万 token）｜Groq console.groq.com｜Gemini aistudio.google.com｜OpenRouter openrouter.ai",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 0x9A, 0x9A, 0x9A)),
            TextWrapping = TextWrapping.Wrap,
        };

        panel.Children.Add(presets);
        panel.Children.Add(hint);
        panel.Children.Add(apiUrl);
        panel.Children.Add(apiModel);
        panel.Children.Add(apiKey);

        var apiFormat = new ComboBox
        {
            Header = "API 协议格式（不同厂商接口格式不同，选错会报错）",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        apiFormat.Items.Add("OpenAI 兼容（DeepSeek / 智谱 / 硅基流动 / Groq / OpenRouter）");
        apiFormat.Items.Add("Anthropic Claude（/v1/messages）");
        apiFormat.Items.Add("Google Gemini（generateContent）");
        apiFormat.Items.Add("OpenAI Responses API（/v1/responses，GPT-5 等新模型专用）");
        bool suppressFormat = true;
        apiFormat.SelectionChanged += (_, _) =>
        {
            if (suppressFormat) return;
            apiUrl.Text = apiFormat.SelectedIndex switch
            {
                1 => "https://api.anthropic.com/v1/messages",
                2 => "https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent",
                3 => "https://api.openai.com/v1/responses",
                _ => "https://api.deepseek.com/chat/completions",
            };
        };
        apiFormat.SelectedIndex = _config.ApiFormat switch { "anthropic" => 1, "gemini" => 2, "responses" => 3, _ => 0 };
        suppressFormat = false;
        panel.Children.Add(apiFormat);

        var lblLocal = new TextBlock { Text = "本地大模型（Ollama）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(lblLocal);

        var ollamaModel = new TextBox { Header = "快模型名（简单攻击用）", Text = _config.OllamaModel, PlaceholderText = "qwen2.5:1.5b" };
        var ollamaModelStrong = new TextBox { Header = "强模型名（复杂攻击用）", Text = _config.OllamaModelStrong, PlaceholderText = "qwen2.5:1.5b" };
        var ollamaUrl = new TextBox { Header = "服务地址", Text = _config.OllamaUrl, PlaceholderText = "http://127.0.0.1:11434" };
        panel.Children.Add(ollamaModel);
        panel.Children.Add(ollamaModelStrong);
        panel.Children.Add(ollamaUrl);

        var useOllama = new CheckBox
        {
            Content = "启用本地 Ollama 模型（不花 API 额度，适合长任务/集群）",
            IsChecked = _config.UseOllama,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var installBtn = new Button
        {
            Content = "⬇️ 一键安装 Ollama + 大模型（qwen3:4b / qwen2.5:1.5b）",
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        panel.Children.Add(useOllama);
        panel.Children.Add(installBtn);

        var lblImg = new TextBlock { Text = "图像生成（CogView-3-Flash）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(lblImg);
        var imgKey = new PasswordBox { Header = "图像 API Key", Password = _config.ImageApiKey, PlaceholderText = "智谱图像 Key（id.secret）" };
        var imgModel = new TextBox { Header = "图像模型名", Text = _config.ImageModel, PlaceholderText = "cogview-3-flash" };
        panel.Children.Add(imgKey);
        panel.Children.Add(imgModel);

        var lblVision = new TextBlock { Text = "图像理解（GLM-4.6V-Flash 多模态）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(lblVision);
        var visionKey = new PasswordBox { Header = "图像理解 API Key", Password = _config.VisionApiKey, PlaceholderText = "智谱多模态 Key" };
        var visionModel = new TextBox { Header = "图像理解模型名", Text = _config.VisionModel, PlaceholderText = "glm-4.6v-flash" };
        panel.Children.Add(visionKey);
        panel.Children.Add(visionModel);

        var multiModal = new CheckBox
        {
            Content = "主模型是多模态（图片直接发给主模型，不再调用「看图」工具）",
            IsChecked = _config.MultimodalMain,
            Margin = new Thickness(0, 6, 0, 0),
        };
        panel.Children.Add(multiModal);

        var lblUpdate = new TextBlock { Text = "OTA 自动更新", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(lblUpdate);
        var updateUrl = new TextBox
        {
            Header = "更新源（GitHub 仓库 owner/repo，或 update.json 的 URL）",
            Text = _config.UpdateUrl,
            PlaceholderText = "例如 lichenghan/deepsleep",
        };
        var autoUpdate = new CheckBox
        {
            Content = "启动时自动检查更新（发现新版会在左下角提示）",
            IsChecked = _config.AutoCheckUpdate,
            Margin = new Thickness(0, 6, 0, 0),
        };
        panel.Children.Add(updateUrl);
        panel.Children.Add(autoUpdate);
        var tokenBtn = new Button
        {
            Content = "🔑 录入 GitHub 令牌（多层加密保存）",
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        tokenBtn.Click += async (_, _) => await AskTokenAsync();
        var tokenHint = new TextBlock
        {
            Text = "私有仓库、或者想提高 GitHub API 限额时录入令牌。令牌用 Windows DPAPI 加密后存在 data\\github.token，" +
                   "不会写进配置文件，也不会被日志或界面显示，只有本机当前 Windows 账号能解密。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
            Margin = new Thickness(0, 4, 0, 0),
        };
        panel.Children.Add(tokenBtn);
        panel.Children.Add(tokenHint);

        var dlg = new ContentDialog
        {
            Title = "模型设置",
            Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        installBtn.Click += (_, _) => { dlg.Hide(); _ = InstallOllamaAsync(); };

        var res = await dlg.ShowAsync();
        if (res != ContentDialogResult.Primary) return;

        _config.ApiUrl = apiUrl.Text.Trim();
        _config.ApiModel = apiModel.Text.Trim();
        _config.ApiKey = apiKey.Password.Trim();
        _config.ApiFormat = apiFormat.SelectedIndex switch { 1 => "anthropic", 2 => "gemini", 3 => "responses", _ => "openai" };
        _config.OllamaModel = ollamaModel.Text.Trim();
        _config.OllamaModelStrong = ollamaModelStrong.Text.Trim();
        _config.OllamaUrl = ollamaUrl.Text.Trim();
        _config.ImageApiKey = imgKey.Password.Trim();
        _config.ImageModel = imgModel.Text.Trim();
        _config.VisionApiKey = visionKey.Password.Trim();
        _config.VisionModel = visionModel.Text.Trim();
        _config.MultimodalMain = multiModal.IsChecked == true;
        _config.UpdateUrl = updateUrl.Text.Trim();
        _config.AutoCheckUpdate = autoUpdate.IsChecked == true;
        _config.UseOllama = useOllama.IsChecked == true;

        _config.Save();
        _engine.ApplyConfig(_config);
        _agent.ApplyConfig(_config);
        _clusterAgent.ApplyConfig(_config);
        string backend = _config.UseOllama ? "llm" : "auto";
        _agent.Backend = backend;
        _clusterAgent.Backend = backend;
        _agent.MultimodalMain = _config.MultimodalMain;
        _clusterAgent.MultimodalMain = _config.MultimodalMain;
        UpdateAgentStatus();
        UpdateClusterStatus();

    }
}

/// <summary>一个对话会话：左侧对话栏的条目，含独立的引擎会话与消息列表。</summary>
public sealed class Conversation : INotifyPropertyChanged
{
    private string _title = "";

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        }
    }

    public int Sid { get; set; }
    public string Created { get; set; } = "";
    /// <summary>是否置顶（对话栏排序用）。</summary>
    public bool IsPinned { get; set; }
    /// <summary>本对话内"始终允许执行命令/脚本"（提高权限，随对话持久化）。</summary>
    public bool AutoApprove { get; set; }
    public ObservableCollection<ChatItem> Items { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Agent 集群成员：名称、任务、状态、运行日志。</summary>
public sealed class ClusterAgent : INotifyPropertyChanged
{
    private string _status = "空闲";
    private string _logText = "";

    public int Sid { get; set; }
    public string Name { get; set; } = "";
    public string Task { get; set; } = "";

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    public string LogText
    {
        get => _logText;
        set
        {
            if (_logText == value) return;
            _logText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LogText)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

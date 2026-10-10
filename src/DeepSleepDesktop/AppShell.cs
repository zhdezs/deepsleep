using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Photino.NET;
using TrollWrangler;
using TrollWrangler.Core;
using TrollWrangler.CoreHost;

namespace DeepSleep.Desktop;

/// <summary>
/// 桌面版外壳（Linux / macOS）—— 从 Windows 桌面版（src\MainWindow.xaml.cs）改来：
///   · 一个主窗口装 ui\index.html（HTML/CSS/JS 界面），与界面之间只走 JSON 消息；
///   · 自带内核服务 CoreServer（与内核版 deepsleep-core 同一份实现），设置里能开公网隧道；
///   · 窗口类命令（显示 / 隐藏 / 退出 / 打开链接 / 选文件）由外壳真的执行（ShellOverride）；
///   · 桌宠 + 桌宠聊天浮窗是独立窗口，共用同一个内核，所以和主界面对话连贯。
/// 与 Windows 版唯一的不同是窗口后端：这里是 Photino（Linux = WebKitGTK，macOS = WKWebView）。
/// </summary>
internal sealed class AppShell : IDisposable
{
    private readonly Kernel _kernel = new();
    private readonly string _rootDir;
    private readonly string _dataDir;
    private PhotinoWindow? _win;
    private PhotinoWindow? _chat;
    private PetWindow? _pet;
    private readonly ManualResetEventSlim _winReady = new(false);
    private bool _petScheduled;
    private volatile bool _closing;
    private bool _tunnelOn;

    public AppShell(string rootDir, string dataDir)
    {
        _rootDir = rootDir;
        _dataDir = dataDir;
    }

    public int Run(string[] args)
    {
        Log("桌面版启动：" + Platform.DisplayName + " / " + Platform.Rid + "，数据目录 " + _dataDir);
        try
        {
            _kernel.Push += OnKernelPush;
            _kernel.Init(_dataDir);
        }
        catch (Exception ex) { Log("内核初始化失败：" + ex); }

        StartCoreServer();

        // 内核模式：不建窗口，只把服务跑起来（浏览器 / 网页版连上来用）
        if (args.Contains("--headless")) return RunHeadless(args);

        // 建窗口逐项兜底：Photino 各平台支持的设置项并不完全一致，macOS 上某个 setter
        // 不被支持就会抛异常。以前这整条链一个 try 都没有，一抛就直接结束进程 ——
        // 而从访达 / 启动台点开是没有终端的，用户看到的就是"双击没反应"，
        // 什么信息都提供不出来。现在哪一项失败就记一笔、跳过去继续。
        PhotinoWindow win;
        try
        {
            var w = new PhotinoWindow();
            void Set(string what, Action act)
            {
                try { act(); }
                catch (Exception ex) { Log("窗口设置「" + what + "」没生效，已跳过：" + ex.Message); }
            }
            Set("标题", () => w.SetTitle("deepsleep · AI 助手"));
            Set("尺寸模式", () => w.SetUseOsDefaultSize(false));
            Set("尺寸", () => w.SetSize(1500, 960));
            Set("最小尺寸", () => w.SetMinSize(860, 560));
            Set("居中", () => w.Center());
            Set("可缩放", () => w.SetResizable(true));
            Set("右键菜单", () => w.SetContextMenuEnabled(true));
            Set("剪贴板权限", () => w.SetJavascriptClipboardAccessEnabled(true));
            Set("关闭 Web 安全限制", () => w.SetWebSecurityEnabled(false));
            Set("日志级别", () => w.SetLogVerbosity(0));
            win = w;
        }
        catch (Exception ex)
        {
            // 连窗口对象都建不出来（缺桌面环境 / 缺系统 WebView 组件），
            // 把原因写进日志再退出，别让它变成一次无声的崩溃。
            Log("建窗口失败，界面起不来：" + ex);
            try
            {
                Console.Error.WriteLine("deepsleep：建窗口失败 —— " + ex.Message);
                Console.Error.WriteLine("  详情已写入：" + Path.Combine(_dataDir, "shell.log"));
            }
            catch { }
            Shutdown();
            return 3;
        }
        _win = win;
        try { win.SetIconFile(Path.Combine(_rootDir, "pet.png")); } catch { }
        try { win.RegisterWebMessageReceivedHandler((_, message) => OnMessage(message)); }
        catch (Exception ex) { Log("注册界面通道失败：" + ex.Message); }
        // 主窗口原生窗口建好才算就绪：桌宠只能在它之后、且在 UI 线程上建
        try { win.WindowCreatedHandler += (_, _) => _winReady.Set(); }
        catch (Exception ex) { Log("注册窗口就绪回调失败：" + ex.Message); }
        try { win.Load(new Uri(UiPath(), UriKind.Absolute)); }
        catch (Exception ex) { Log("加载界面失败：" + ex.Message); }

        ApplyPet();
        win.WaitForClose();
        Shutdown();
        return 0;
    }


    /// <summary>
    /// 内核模式（--headless）：与 Windows 内核版 deepsleep-core 等价 —— 不建窗口，只跑内核服务，
    /// 浏览器打开 http://127.0.0.1:&lt;端口&gt;/web/core/#t=&lt;令牌&gt; 就是完整界面；
    /// 加 --tunnel 还能生成公网配对链接（超远程提问）。
    /// </summary>
    private int RunHeadless(string[] args)
    {
        Console.WriteLine("deepsleep 内核模式（无窗口） · " + Platform.DisplayName + " / " + Platform.Rid);
        Console.WriteLine("  本机打开：http://127.0.0.1:" + CoreServer.Port + "/web/core/#t=" + CoreServer.Token);
        if (args.Contains("--tunnel"))
        {
            Console.WriteLine("  正在建立公网隧道…");
            try { CoreServer.StartTunnel(true, ArgOf(args, "--tunnel-cmd"), ArgOf(args, "--tunnel-cf")); }
            catch (Exception ex) { Console.WriteLine("  隧道启动失败：" + ex.Message); }
            string u = CoreServer.TunnelUrl;
            Console.WriteLine(u.Length > 0
                ? "  公网配对链接：" + u + "/web/core/#t=" + CoreServer.Token
                : "  公网地址还没就绪，稍后重试或看日志。");
        }
        Console.WriteLine("  按 Ctrl+C 退出。");
        var done = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _2) => done.Set();
        done.Wait();
        Shutdown();
        return 0;
    }

    private static string? ArgOf(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private string UiPath() => Path.Combine(_rootDir, "ui", "index.html");

    // ------------------------------------------------------------------
    // 界面通道（对齐 Windows 版：WebMessage → HandleAsync → 回一条 JSON）
    // ------------------------------------------------------------------

    private void OnMessage(string json)
    {
        _ = Task.Run(async () =>
        {
            string res;
            try { res = await HandleAsync(json); }
            catch (Exception ex)
            {
                Log("处理界面消息失败：" + ex);
                res = """{"ok":false,"err":""" + JsonSerializer.Serialize(ex.Message) + "}";
            }
            SendTo(_win, res);
        });
    }

    /// <summary>外壳自己处理的命令先拦下来，其余原样交给内核。</summary>
    private async Task<string> HandleAsync(string json)
    {
        string cmd = "";
        int id = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            cmd = doc.RootElement.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
            id = doc.RootElement.TryGetProperty("id", out var i) && i.TryGetInt32(out int n) ? n : 0;
        }
        catch { }

        switch (cmd)
        {
            // 界面加载完自己报个到（Photino 没有 NavigationCompleted，用它替代 Windows 版的 push）
            case "shellReady":
                PostToUi("""{"ev":"uiReady"}""");
                return WithId(CoreHostInfoJson(), id);
            case "superlinkStatus":
                return WithId(SuperLink.StatusJson(), id);
            case "superlinkHost":
                SuperLink.StartHost();
                return WithId(SuperLink.StatusJson(), id);
            case "superlinkJoin":
            {
                string code = "";
                try
                {
                    using var d2 = JsonDocument.Parse(json);
                    code = d2.RootElement.TryGetProperty("code", out var c2) ? c2.GetString() ?? "" : "";
                }
                catch { }
                SuperLink.Connect(code);
                return WithId(SuperLink.StatusJson(), id);
            }
            case "superlinkStop":
                SuperLink.Stop();
                return WithId(SuperLink.StatusJson(), id);
            case "coreStatus":
                return WithId(CoreHostInfoJson(), id);
            case "coreTunnel":
            {
                bool on = false;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    on = doc.RootElement.TryGetProperty("on", out var o) && o.GetBoolean();
                }
                catch { }
                _tunnelOn = on;
                if (on) _ = Task.Run(() => { try { CoreServer.StartTunnel(true, null, null); } catch (Exception ex) { Log("隧道启动失败：" + ex.Message); } });
                else CoreServer.StopTunnel();
                return CoreHostInfoJson();
            }
            case "pickFile":
            {
                int kind = 0;
                try { using var doc = JsonDocument.Parse(json); kind = doc.RootElement.TryGetProperty("kind", out var k) && k.TryGetInt32(out int kk) ? kk : 0; } catch { }
                string? path = PickFile();
                if (path != null) await _kernel.InvokeAsync(JsonSerializer.Serialize(new { cmd = "attach", kind, path }));
                return Ok(id);
            }
            case "pickFolder":
            {
                string? path = PickFolder();
                if (path != null) await _kernel.InvokeAsync(JsonSerializer.Serialize(new { cmd = "skillInstallFolder", path }));
                return Ok(id);
            }
            case "showWindow":
                ShowMain();
                return Ok(id);
            case "hideWindow":
                HideMain();
                return Ok(id);
            case "openUrl":
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    string url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Platform.OpenWithShell(url, false);
                }
                catch { }
                return Ok(id);
            }
            case "quitApp":
                _ = Task.Run(() => { Thread.Sleep(120); Quit(); });
                return Ok(id);
            default:
                return await _kernel.InvokeAsync(json);
        }
    }

    // ------------------------------------------------------------------
    // 选文件 / 窗口
    // ------------------------------------------------------------------

    private string? PickFile()
    {
        try
        {
            var files = _win?.ShowOpenFile("选择要附加的文件", "", false);
            return files?.FirstOrDefault();
        }
        catch (Exception ex) { Log("选择文件失败：" + ex.Message); return null; }
    }

    private string? PickFolder()
    {
        try
        {
            var dirs = _win?.ShowOpenFolder("选择文件夹", "", false);
            return dirs?.FirstOrDefault();
        }
        catch (Exception ex) { Log("选择文件夹失败：" + ex.Message); return null; }
    }

    private void ShowMain()
    {
        try
        {
            var w = _win;
            if (w == null) return;
            w.SetMinimized(false);
            w.SetTopMost(true);
            w.SetTopMost(false);
        }
        catch { }
    }

    private void HideMain()
    {
        try { _win?.SetMinimized(true); } catch { }
    }

    private void Quit()
    {
        _closing = true;
        try { _chat?.Close(); } catch { }
        try { _pet?.Dispose(); } catch { }
        _pet = null;
        try { _win?.Close(); } catch { }
        try { Environment.Exit(0); } catch { }
    }

    // ------------------------------------------------------------------
    // 内核服务（HTTP + 内网穿透）：和内核版跑的是同一份 CoreServer
    // ------------------------------------------------------------------

    private void StartCoreServer()
    {
        try
        {
            CoreServer.ShellOverride = OnShellCommandAsync;
            CoreServer.TunnelReady += () => PostToUi(CoreHostInfoJson());
            CoreServer.Log = line => PostToUi(JsonSerializer.Serialize(new { ev = "coreLog", text = line }));
            string token = LoadCoreToken();
            int port = PickFreePort(8756);
            bool up = CoreServer.Start(new CoreServerOptions
            {
                Kernel = _kernel,
                Port = port,
                Token = token,
                RootDir = _rootDir,
                DataDir = _dataDir,
                Bind = IPAddress.Loopback,
                Tunnel = false,
            });
            Log(up ? ("内核服务已启动：端口 " + CoreServer.Port) : "内核服务没起来（端口被占？）");
        }
        catch (Exception ex) { Log("内核服务启动失败：" + ex.Message); }
    }

    /// <summary>桌宠 / 网页端发来的窗口类命令（内核版没有外壳，这些只有桌面版能真做）。</summary>
    private Task<string?> OnShellCommandAsync(string cmd, int id, string? url)
    {
        switch (cmd)
        {
            case "showWindow":
                ShowMain();
                return Task.FromResult<string?>("""{"id":""" + id + ""","ok":true}""");
            case "hideWindow":
                HideMain();
                return Task.FromResult<string?>("""{"id":""" + id + ""","ok":true}""");
            case "quitApp":
                _ = Task.Run(() => { Thread.Sleep(120); Quit(); });
                return Task.FromResult<string?>("""{"id":""" + id + ""","ok":true}""");
            default:
                return Task.FromResult<string?>(null);
        }
    }

    /// <summary>配对信息：端口 / 令牌 / 局域网链接 / 公网配对链接（界面「超远程提问」用）。</summary>
    private string CoreHostInfoJson()
    {
        string token = CoreServer.Token;
        string tunnel = CoreServer.TunnelUrl;
        return JsonSerializer.Serialize(new
        {
            ev = "hostInfo",
            ok = true,
            desktop = true,
            corePort = CoreServer.Port,
            coreToken = token,
            coreRunning = CoreServer.Running,
            tunnelUrl = tunnel,
            pairLink = tunnel.Length > 0 ? tunnel + "/web/core/#t=" + token : "",
            lanLink = "http://" + LanIp() + ":" + CoreServer.Port + "/web/core/#t=" + token,
        });
    }

    private string LoadCoreToken()
    {
        string file = Path.Combine(_dataDir, "core-token.txt");
        try
        {
            if (File.Exists(file))
            {
                string t = File.ReadAllText(file).Trim();
                if (t.Length >= 16) return t;
            }
            string nt = Guid.NewGuid().ToString("N");
            File.WriteAllText(file, nt, new System.Text.UTF8Encoding(false));
            try { Platform.LockDownFile(file); } catch { }
            return nt;
        }
        catch { return Guid.NewGuid().ToString("N"); }
    }

    private static int PickFreePort(int start)
    {
        for (int p = start; p < start + 20 && p < 65536; p++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, p);
                l.Start();
                l.Stop();
                return p;
            }
            catch { }
        }
        return start;
    }

    private static string LanIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork) return ua.Address.ToString();
            }
        }
        catch { }
        return "127.0.0.1";
    }

    // ------------------------------------------------------------------
    // 内核 → 界面
    // ------------------------------------------------------------------

    private void OnKernelPush(string json)
    {
        if (_closing) return;
        if (json.Contains("""{"ev":"restarting"}""", StringComparison.Ordinal)) ArmUpdateExitFallback();
        bool petChanged = json.Contains("""{"ev":"pet"}""", StringComparison.Ordinal);
        PostToUi(json);
        lock (_chatGate)
        {
            if (_chat != null) SendTo(_chat, json);
        }
        if (petChanged) ApplyPet();
    }

    /// <summary>
    /// 升级兜底：装完更新要退出进程，升级脚本才肯往下走。界面若没能发出 quitApp
    /// （WebView 异常等），到点由外壳自己退，别让「正在启动升级程序…」一直卡着。
    /// </summary>
    private void ArmUpdateExitFallback()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(12));
                if (!_closing) Quit();
            }
            catch { }
        });
    }

    private void PostToUi(string json) => SendTo(_win, json);

    private static void SendTo(PhotinoWindow? w, string json)
    {
        if (w == null) return;
        try { w.SendWebMessage(json); } catch { }
    }

    // ------------------------------------------------------------------
    // 桌宠 + 桌宠聊天浮窗
    // ------------------------------------------------------------------

    private readonly object _chatGate = new();

    /// <summary>按配置显示 / 关闭桌宠（设置里改开关立即生效，不必重启）。</summary>
    private void ApplyPet()
    {
        try
        {
            if (!_kernel.Config.PetEnabled)
            {
                var pet = _pet;
                _pet = null;
                pet?.Dispose();
                HideChat();
                return;
            }
            if (_pet != null)
            {
                _pet.Show();
                return;
            }
            EnsurePetScheduled();
        }
        catch (Exception ex) { _pet = null; Log("桌宠启动失败：" + ex.Message); }
    }

    /// <summary>
    /// 桌宠窗口只能等主窗口的原生窗口 + 消息循环都就绪之后再建：Photino 全局只有一个消息循环
    /// （静态字段），提前建或者在别的线程建第二个窗口，GTK 会直接断言失败 abort 掉整个进程
    /// —— 表现就是「双击图标完全没反应」。这里等到就绪再派发到 UI 线程。
    /// </summary>
    private void EnsurePetScheduled()
    {
        lock (_chatGate)
        {
            if (_petScheduled) return;
            _petScheduled = true;
        }
        _ = Task.Run(() =>
        {
            try
            {
                if (!_winReady.Wait(TimeSpan.FromSeconds(30))) { _petScheduled = false; return; }
                Thread.Sleep(400);                        // 让主窗口真正进消息循环
                _win?.Invoke(ApplyPetNow);                // 派发到 UI 线程
            }
            catch (Exception ex) { Log("桌宠调度失败：" + ex.Message); _petScheduled = false; }
        });
    }

    /// <summary>只允许在 UI 线程调用（由 EnsurePetScheduled 派发）。</summary>
    private void ApplyPetNow()
    {
        try
        {
            if (!_kernel.Config.PetEnabled || _pet != null) return;
            string img = Path.Combine(_rootDir, "pet.png");
            if (!File.Exists(img)) return;
            var cfg = _kernel.Config;
            _pet = new PetWindow(img,
                onChat: () => ToggleChat(),
                onMenu: () => { try { _kernel.InvokeAsync("""{"cmd":"setPetEnabled","on":false}"""); } catch { } ApplyPet(); },
                onOpenMain: ShowMain,
                onMoved: (x, y) => { try { _kernel.NotePetPosition(x, y); } catch { } },
                startX: cfg.PetX < 0 ? int.MinValue : cfg.PetX,
                startY: cfg.PetY < 0 ? int.MinValue : cfg.PetY,
                transparent: !Platform.IsLinux);
            _pet.Start();
        }
        catch (Exception ex) { _pet = null; Log("桌宠启动失败：" + ex.Message); }
    }

    /// <summary>桌宠的独立聊天浮窗（同一个 HTML，?pet=1 走精简布局）。</summary>
    private void ToggleChat()
    {
        lock (_chatGate)
        {
            if (_chat != null)
            {
                var old = _chat;
                _chat = null;
                try { old.Close(); } catch { }
                return;
            }
        }

        var th = new Thread(() =>
        {
            PhotinoWindow? w = null;
            try
            {
                w = new PhotinoWindow()
                    .SetTitle("deepsleep · 桌宠")
                    .SetUseOsDefaultSize(false)
                    .SetSize(384, 560)
                    .SetMinSize(320, 420)
                    .SetResizable(true)
                    .SetContextMenuEnabled(true)
                    .SetLogVerbosity(0);
                w.RegisterWebMessageReceivedHandler((_, message) => OnMessage(message));
                string url = new Uri(UiPath(), UriKind.Absolute).AbsoluteUri + "?pet=1";
                w.Load(new Uri(url, UriKind.Absolute));
                lock (_chatGate) { _chat = w; }
                w.WaitForClose();
            }
            catch (Exception ex) { Log("桌宠浮窗启动失败：" + ex.Message); }
            finally
            {
                lock (_chatGate) { if (ReferenceEquals(_chat, w)) _chat = null; }
                try { w?.Close(); } catch { }
            }
        })
        { IsBackground = true, Name = "deepsleep-petchat" };
        th.Start();
    }

    private void HideChat()
    {
        PhotinoWindow? c;
        lock (_chatGate) { c = _chat; _chat = null; }
        try { c?.Close(); } catch { }
    }

    private void Shutdown()
    {
        _closing = true;
        try { CoreServer.Stop(); } catch { }
        try { _kernel.Shutdown(); } catch { }
        try { _pet?.Dispose(); } catch { }
        _pet = null;
        HideChat();
    }

    public void Dispose() => Shutdown();

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    private static string WithId(string json, int id)
    {
        if (id <= 0 || json.Length == 0 || json[json.Length - 1] != '}') return json;
        return json[..^1] + ""","id":""" + id + "}";
    }

    private static string Ok(int id) => """{"id":""" + id + ""","ok":true}""";

    private void Log(string text)
    {
        try
        {
            File.AppendAllText(Path.Combine(_dataDir, "shell.log"),
                "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + text + "\n");
        }
        catch { }
    }
}

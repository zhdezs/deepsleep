using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using TrollWrangler.Core;
using TrollWrangler.CoreHost;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace TrollWrangler;

/// <summary>
/// 外壳 = WebView2 宿主：把 ui\ 里的 HTML 界面当成本程序的界面跑，逻辑全部交给 Deepsleep.Core。
/// 界面与内核之间只走 JSON（WebMessage / PostWebMessageAsJson）。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly Kernel _kernel = new();
    private readonly string _dataDir;
    private CoreWebView2Environment? _env;
    private WebView2 _web = null!;
    private bool _webReady;
    private bool _webRetried;
    private const string CompatArgs = "--no-sandbox --disable-gpu --disable-gpu-compositing " +
        "--use-angle=swiftshader --enable-unsafe-swiftshader --disable-features=RendererCodeIntegrity";
    private DesktopPet? _pet;
    private bool _tunnelOn;
    private PetChatWindow? _petChat;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        Title = "deepsleep · AI 助手";
        try { SystemBackdrop = new DesktopAcrylicBackdrop(); }
        catch { try { SystemBackdrop = new MicaBackdrop(); } catch { } }

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(1500, 960));
        SetupTitleBar();

        _dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(_dataDir);

        _web = Web;
        _kernel.Push += OnKernelPush;
        try { _kernel.Init(_dataDir); }
        catch (Exception ex) { Log("内核初始化失败：" + ex); }
        StartCoreServer();

        ApplyTheme(_kernel.Config.Theme == "dark");
        _ = StartWebAsync();
        ApplyPetVisibility();

        Closed += (_, _) =>
        {
            _closing = true;
            try { CoreServer.Stop(); } catch { }
            try { _kernel.Shutdown(); } catch { }
            try { _petChat?.Close(); } catch { }
            try { _pet?.Dispose(); } catch { }
            _pet = null;
            _petChat = null;
        };
    }

    // ------------------------------------------------------------------
    // 标题栏 / 主题
    // ------------------------------------------------------------------

    private void SetupTitleBar()
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarDrag);
        }
        catch (Exception ex) { Log("自绘标题栏失败：" + ex.Message); }
    }

    private void ApplyTheme(bool dark)
    {
        RootGrid.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        try { TitleBarDrag.Background = new SolidColorBrush(dark ? Color.FromArgb(255, 0x2B, 0x2B, 0x2E) : Color.FromArgb(255, 0xF6, 0xF6, 0xF7)); }
        catch { }
        try
        {
            var tb = AppWindow.TitleBar;
            tb.PreferredHeightOption = TitleBarHeightOption.Standard;
            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonHoverBackgroundColor = Color.FromArgb(0x33, 0x80, 0x80, 0x80);
            tb.ButtonPressedBackgroundColor = Color.FromArgb(0x55, 0x80, 0x80, 0x80);
            var fg = dark ? Colors.White : Color.FromArgb(255, 0x33, 0x33, 0x33);
            tb.ButtonForegroundColor = fg;
            tb.ButtonHoverForegroundColor = fg;
            tb.ButtonInactiveForegroundColor = Color.FromArgb(0x80, fg.R, fg.G, fg.B);
        }
        catch { }
    }

    // ------------------------------------------------------------------
    // WebView2
    // ------------------------------------------------------------------

    private async Task StartWebAsync()
    {
        Log("start web, WebCompat=" + _kernel.Config.WebCompat);
        await InitWebAsync(_web, _kernel.Config.WebCompat);
        _ = WatchdogAsync();
    }

    /// <summary>界面迟迟没渲染出来（WebView2 起不来）→ 显示兜底面板，别让用户看着白窗口。
    /// 低配机 / 首次初始化 WebView2 会很慢，所以先等 90 秒；引擎对象已经建出来、只是页面
    /// 还没加载完的，再给 60 秒。老版本只等 20 秒，慢机器上经常误报。</summary>
    private async Task WatchdogAsync()
    {
        for (int i = 0; i < 180 && !_closing && !_webReady; i++) await Task.Delay(500);
        if (_closing) return;
        if (_webReady) { HideFallback(); return; }
        if (_web?.CoreWebView2 != null)
        {
            for (int i = 0; i < 120 && !_closing && !_webReady; i++) await Task.Delay(500);
            if (_closing) return;
            if (_webReady) { HideFallback(); return; }
        }
        ShowFallback("界面引擎（WebView2）没能启动 —— 本程序用 Edge WebView2 渲染界面，"
            + "如果系统里缺它或它被安全软件拦了，就会这样。\n\n"
            + "可以点「重试」再试一次；还是不行的话，装一下微软的 Edge WebView2 运行时再启动本程序。\n"
            + "细节已写进日志（见下方「打开数据目录」）。");
    }

    private void ShowFallback(string text)
    {
        Log("显示兜底面板：" + text.Replace("\n", " "));
        try
        {
            FallbackText.Text = text;
            Fallback.Visibility = Visibility.Visible;
        }
        catch { }
    }

    /// <summary>界面真起来了就把兜底面板收掉，免得盖在能用的界面上。</summary>
    private void HideFallback()
    {
        try { if (Fallback.Visibility == Visibility.Visible) Fallback.Visibility = Visibility.Collapsed; } catch { }
    }

    private async void RetryClick(object sender, RoutedEventArgs e)
    {
        try { Fallback.Visibility = Visibility.Collapsed; } catch { }
        _webReady = false;
        _webRetried = false;
        await RebuildWebAsync(_kernel.Config.WebCompat);
        _ = WatchdogAsync();
    }

    private void OpenLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + _dataDir + "\"") { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { Log("打开目录失败：" + ex.Message); }
    }

    // ==================================================================
    // 一键绑定的授权窗口
    // ==================================================================

    private bool _authOpen;
    private string _authRedirect = "";
    private string _authUrl = "";
    private bool _authWebReady;

    /// <summary>给内核用的「打开应用内授权窗口」。返回 false 表示没建成，内核会退回系统浏览器。</summary>
    private bool TryOpenAuthWindow(string url, string redirectPrefix)
    {
        try
        {
            if (_authOpen) { Log("授权窗口已开着，忽略重复请求"); return false; }
            _authOpen = true;
            _authRedirect = redirectPrefix ?? "";
            _authUrl = url ?? "";

            AuthPane.Visibility = Visibility.Visible;
            AuthTitle.Text = "授权登录 · 完成后自动回到 deepsleep";
            AuthWeb.Visibility = Visibility.Visible;
            try { BarTitle.Text = "deepsleep · 授权登录中…"; } catch { }

            var dispatcher = AuthWeb.DispatcherQueue;
            _ = dispatcher.TryEnqueue(async () => await InitAuthWebAsync(url));
            return true;
        }
        catch (Exception ex)
        {
            Log("打开授权窗口失败：" + ex.Message);
            _authOpen = false;
            return false;
        }
    }

    private async Task InitAuthWebAsync(string url)
    {
        try
        {
            // 复用已经建好的 WebView2：重复点「一键绑定」时 EnsureCoreWebView2Async 会抛
            if (!_authWebReady)
            {
                var opts = new CoreWebView2EnvironmentOptions();
                var env = await CoreWebView2Environment.CreateWithOptionsAsync(null,
                    Path.Combine(_dataDir, "webview"), opts);
                await AuthWeb.EnsureCoreWebView2Async(env);
                for (int i = 0; i < 200 && AuthWeb.CoreWebView2 == null; i++) await Task.Delay(50);
                var c0 = AuthWeb.CoreWebView2;
                if (c0 == null) { Log("授权窗口 WebView2 未就绪"); CloseAuthWindow(); InternalBrowser.Release(); return; }

                c0.Settings.AreDevToolsEnabled = false;
                c0.Settings.IsStatusBarEnabled = false;

                // 关键一步：拦下「平台跳回本机回调地址」的导航，把授权码直接拿走。
                // 不能等它真的去请求 127.0.0.1 —— 拦截必须发生在导航阶段。
                // （NavigationStarting 对 302 链的每一跳都会触发，所以这里不用再挂 Redirecting；
                //   万一漏了，内核那边还有一条回环监听兜底。）
                c0.NavigationStarting += (s, e) => HandleAuthNav(e.Uri, cancel => e.Cancel = cancel);
                // 授权页里的第三方登录有可能会开新窗口 —— 默认行为是丢给系统浏览器（会话不共享，
                // 用户在那儿登完，应用这边再也等不到结果）。这里强制留在同一个窗口里走完。
                c0.NewWindowRequested += (s, e) =>
                {
                    e.Handled = true;
                    Log("授权页请求新窗口 → 改为当前窗口打开：" + e.Uri);
                    try { c0.Navigate(e.Uri); } catch { }
                };
                c0.NavigationCompleted += (s, e) =>
                {
                    Log($"授权页导航完成 success={e.IsSuccess} err={e.WebErrorStatus} host={HostOf(c0.Source)}");
                    HintIfStuck(c0.Source);
                };
                _authWebReady = true;
            }

            Log("授权页打开：" + url.Substring(0, Math.Min(120, url.Length)));
            AuthWeb.CoreWebView2!.Navigate(url);
        }
        catch (Exception ex)
        {
            Log("初始化授权窗口失败：" + ex.Message);
            CloseAuthWindow();
            try { InternalBrowser.Release(); } catch { }
        }
    }

    private static string HostOf(string uri)
    {
        try { return new Uri(uri).Host; } catch { return uri ?? ""; }
    }

    /// <summary>命中回环回调 → 拿走授权码、关窗；返回 true 表示这次导航被接管。</summary>
    private void HandleAuthNav(string uri, Action<bool> cancel)
    {
        try
        {
            if (InternalBrowser.ShouldIntercept(uri, _authRedirect))
            {
                Log("拦下授权回调，已取走授权码");
                cancel(true);
                CloseAuthWindow();
            }
        }
        catch (Exception ex) { Log("拦截授权回调失败：" + ex.Message); }
    }

    /// <summary>
    /// 按当前所在站点给「下一步该干嘛」的提示。
    /// 最常见的误判是：在 GitHub 登录完以为结束了，其实 GitHub 上还有一步
    /// 「Authorize OpenRouter」的授权确认页 —— 不点那个按钮，流程永远不会回到 deepsleep。
    /// </summary>
    private void HintIfStuck(string uri)
    {
        try
        {
            string host = HostOf(uri);
            if (host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
                AuthTitle.Text = "GitHub 页面上还需要再点一次「Authorize」授权按钮，才会回到 deepsleep";
            else if (host.Contains("google.com") || host.Contains("accounts."))
                AuthTitle.Text = "Google 页面上还需要点「继续 / 允许」，才会回到 deepsleep";
            else if (host.Contains("openrouter") || host.Contains("nano-gpt"))
                AuthTitle.Text = "在这一页点「Authorize / 允许」完成授权；若没反应，点右上角「在系统浏览器完成授权」";
        }
        catch { }
    }

    private void AuthExternalClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_authUrl.Length == 0) return;
            // 同一个授权地址、同一个 PKCE，只是换到系统浏览器里走完 ——
            // 内核那边的回环服务一直在监听，所以照样能收到结果。
            Platform.OpenWithShell(_authUrl, false);
            AuthTitle.Text = "已交给系统浏览器 · 完成后本窗口会自动关闭，无需回来操作";
        }
        catch (Exception ex) { Log("打开系统浏览器失败：" + ex.Message); }
    }

    private void AuthCloseClick(object sender, RoutedEventArgs e)
    {
        CloseAuthWindow();
        try { InternalBrowser.Release(); } catch { }
    }

    private void CloseAuthWindow()
    {
        try
        {
            if (!_authOpen) return;
            _authOpen = false;
            _authRedirect = "";
            _authUrl = "";
            AuthPane.Visibility = Visibility.Collapsed;
            try { BarTitle.Text = "deepsleep · AI 助手"; } catch { }
            try { AuthWeb.CoreWebView2?.Navigate("about:blank"); } catch { }
        }
        catch { }
    }

    private async Task RebuildWebAsync(bool compat)
    {
        try
        {
            RootGrid.Children.Remove(_web);
            _web = new WebView2();
            Grid.SetRow(_web, 1);
            RootGrid.Children.Add(_web);
            await InitWebAsync(_web, compat);
        }
        catch (Exception ex) { Log("重建界面失败：" + ex); }
    }

    /// <summary>初始化 WebView2。compat=true 时加兼容参数（极少数环境渲染进程会崩，需要它兜底）。</summary>
    private async Task<bool> InitWebAsync(WebView2 target, bool compat)
    {
        try
        {
            var opts = new CoreWebView2EnvironmentOptions();
            if (compat) opts.AdditionalBrowserArguments = CompatArgs;
            _env = await CoreWebView2Environment.CreateWithOptionsAsync(null,
                Path.Combine(_dataDir, "webview"), opts);
            // 控件进树（拿到 XamlRoot）之后 CoreWebView2 才会真的建出来，不然它会是 null
            for (int i = 0; i < 40 && target.XamlRoot == null; i++) await Task.Delay(50);
            await target.EnsureCoreWebView2Async(_env);
            for (int i = 0; i < 200 && target.CoreWebView2 == null; i++) await Task.Delay(50);
            var core = target.CoreWebView2;
            if (core == null) { Log("WebView2 未就绪（CoreWebView2 为空）compat=" + compat); return false; }
            Log("WebView2 已创建 compat=" + compat);
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;   // 保留浏览器自带菜单（输入框右键粘贴用），会话列表等自定义右键菜单仍然先 preventDefault
            core.Settings.AreDevToolsEnabled = false;
            string uiDir = Path.Combine(AppContext.BaseDirectory, "ui");
            core.SetVirtualHostNameToFolderMapping(Kernel.UiHost, uiDir, CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping(Kernel.DataHost, _dataDir, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessage;
            core.ProcessFailed += OnWebProcessFailed;
            core.NavigationCompleted += (sender, e) =>
            {
                if (e.IsSuccess) { _webReady = true; HideFallback(); }
                Log($"nav success={e.IsSuccess} err={e.WebErrorStatus} compat={compat}");
                try { sender.PostWebMessageAsJson("{\"ev\":\"uiReady\"}"); } catch { }
                try { sender.PostWebMessageAsJson(CoreHostInfoJson()); } catch { }
            };
            core.Navigate($"https://{Kernel.UiHost}/index.html");
            return true;
        }
        catch (Exception ex)
        {
            Log("启动界面失败：" + ex);
            return false;
        }
    }

    /// <summary>渲染进程崩溃（少数被注入/受限环境）→ 记住并切到兼容参数重建一次。</summary>
    private void OnWebProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log("webview 进程失败：" + e.ProcessFailedKind + " ready=" + _webReady + " retried=" + _webRetried);
        if (_webReady || _webRetried) return;
        _webRetried = true;
        try { _kernel.Config.WebCompat = true; _kernel.Config.Save(); } catch { }
        DispatcherQueue.TryEnqueue(async () => await RebuildWebAsync(true));
    }

    /// <summary>内核 → 界面：转发给主窗口与桌宠浮窗。</summary>
    private void OnKernelPush(string json)
    {
        if (_closing) return;
        if (json.Contains("\"ev\":\"restarting\"", StringComparison.Ordinal)) ArmUpdateExitFallback();
        bool petChanged = json.Contains("\"ev\":\"pet\"", StringComparison.Ordinal);
        DispatcherQueue.TryEnqueue(() =>
        {
            try { _web?.CoreWebView2?.PostWebMessageAsJson(json); } catch { }
            _petChat?.Push(json);
            if (petChanged) ApplyPetVisibility();
        });
    }

    /// <summary>
    /// 升级兜底：装完更新要退出进程，升级脚本才肯往下走。前端若没能发出 quitApp
    /// （WebView2 异常等），到点由外壳自己退，别让「正在启动升级程序…」一直卡着。
    /// </summary>
    private void ArmUpdateExitFallback()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(12));
                DispatcherQueue.TryEnqueue(() => { try { App.ExitApp(); } catch { } });
            }
            catch { }
        });
    }

    private async void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string res;
        try { res = await HandleAsync(e.WebMessageAsJson); }
        catch (Exception ex) { Log("处理界面消息失败：" + ex); res = "{\"ok\":false,\"err\":" + JsonSerializer.Serialize(ex.Message) + "}"; }
        try { sender.PostWebMessageAsJson(res); } catch { }
    }

    /// <summary>外壳自己处理的命令（文件选择器、窗口操作）交给内核之前先拦下来。</summary>
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
                string? path = await PickFileAsync();
                if (path != null)
                {
                    int kind = 0;
                    try { using var doc = JsonDocument.Parse(json); kind = doc.RootElement.TryGetProperty("kind", out var k) && k.TryGetInt32(out int kk) ? kk : 0; } catch { }
                    await _kernel.InvokeAsync(JsonSerializer.Serialize(new { cmd = "attach", kind, path }));
                }
                return Ok(id);
            }
            case "pickFolder":
            {
                string? path = await PickFolderAsync();
                if (path != null)
                    await _kernel.InvokeAsync(JsonSerializer.Serialize(new { cmd = "skillInstallFolder", path }));
                return Ok(id);
            }
            case "showWindow":
                try { AppWindow.Show(); Activate(); } catch { }
                return Ok(id);
            case "hideWindow":
                try { AppWindow.Hide(); } catch { }
                return Ok(id);
            case "openUrl":
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    string url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch { }
                return Ok(id);
            }
            case "quitApp":
                App.ExitApp();
                return Ok(id);
            default:
                return await _kernel.InvokeAsync(json);
        }
    }

    // ------------------------------------------------------------------
    // 内核服务（HTTP + 内网穿透）：桌面版和内核版跑的是同一份 CoreServer
    // ------------------------------------------------------------------

    /// <summary>
    /// 桌面版自带内核服务：默认只监听本机，装上就能用网页端 / 手机连上来操控这台电脑；
    /// 设置里开「公网隧道」后，人在外面也能连。能力和内核版（deepsleep-core）完全一致。
    /// </summary>
    private void StartCoreServer()
    {
        try
        {
            // 一键绑定：内核发起 OAuth 时，由外壳把授权页开在应用内的窗口里；
            // 同时把授权流程的每一步写进日志，卡住时能看出断在哪。
            InternalBrowser.Opener = TryOpenAuthWindow;
            InternalBrowser.Log = Log;
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
                RootDir = AppContext.BaseDirectory,
                DataDir = _dataDir,
                Bind = System.Net.IPAddress.Loopback,
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
                try { DispatcherQueue.TryEnqueue(() => { try { AppWindow.Show(); Activate(); } catch { } }); } catch { }
                return Task.FromResult<string?>("{\"id\":" + id + ",\"ok\":true}");
            case "hideWindow":
                try { DispatcherQueue.TryEnqueue(() => { try { AppWindow.Hide(); } catch { } }); } catch { }
                return Task.FromResult<string?>("{\"id\":" + id + ",\"ok\":true}");
            case "quitApp":
                try { DispatcherQueue.TryEnqueue(() => App.ExitApp()); } catch { }
                return Task.FromResult<string?>("{\"id\":" + id + ",\"ok\":true}");
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

    /// <summary>在界面线程上推一条消息给 WebView（内核服务的日志 / 状态都用它）。</summary>
    private void PostToUi(string json)
    {
        if (_closing) return;
        try
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try { _web?.CoreWebView2?.PostWebMessageAsJson(json); } catch { }
            });
        }
        catch { }
    }

    /// <summary>内核服务的令牌：和内核版共用 data\core-token.txt，两边一致。</summary>
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
            return nt;
        }
        catch { return Guid.NewGuid().ToString("N"); }
    }

    /// <summary>挑一个没被占用的端口（内核版可能已经占着 8756）。</summary>
    private static int PickFreePort(int start)
    {
        for (int p = start; p < start + 20 && p < 65536; p++)
        {
            try
            {
                var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, p);
                l.Start();
                l.Stop();
                return p;
            }
            catch { }
        }
        return start;
    }

    /// <summary>局域网地址：取第一块已启用网卡的 IPv4。</summary>
    private static string LanIp()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return ua.Address.ToString();
            }
        }
        catch { }
        return "127.0.0.1";
    }

    /// <summary>给返回的 JSON 补上 id —— 界面层要凭 id 才会把 Promise 兑现。</summary>
    private static string WithId(string json, int id)
    {
        if (id <= 0 || json.Length == 0 || json[json.Length - 1] != '}') return json;
        return json[..^1] + ",\"id\":" + id + "}";
    }

    private static string Ok(int id) => "{\"id\":" + id + ",\"ok\":true}";

    private async Task<string?> PickFileAsync()
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex) { Log("选择文件失败：" + ex.Message); return null; }
    }

    private async Task<string?> PickFolderAsync()
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception ex) { Log("选择文件夹失败：" + ex.Message); return null; }
    }

    // ------------------------------------------------------------------
    // 日志
    // ------------------------------------------------------------------

    private void Log(string text)
    {
        try
        {
            File.AppendAllText(Path.Combine(_dataDir, "shell.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n");
        }
        catch { }
    }
    // ------------------------------------------------------------------
    // 桌面桌宠（原生透明窗口）+ 桌宠聊天浮窗（HTML）
    // ------------------------------------------------------------------

    /// <summary>按配置显示 / 关闭桌宠（设置里改了开关立即生效，不必重启）。</summary>
    private void ApplyPetVisibility()
    {
        try
        {
            if (!_kernel.Config.PetEnabled)
            {
                _pet?.Dispose();
                _pet = null;
                _petChat?.Hide();
                return;
            }
            if (_pet != null) { _pet.Show(); return; }
            string petRaw = Path.Combine(AppContext.BaseDirectory, "pet.raw");
            if (!File.Exists(petRaw)) return;
            var cfg = _kernel.Config;
            _pet = new DesktopPet(petRaw,
                onChat: () =>
                {
                    EnsurePetChat();
                    _petChat?.SetAnchor(_pet!.Bounds.X, _pet.Bounds.Y, _pet.Bounds.W, _pet.Bounds.H);
                    _petChat?.Toggle();
                },
                onOpenMain: () => { try { AppWindow.Show(); Activate(); } catch { } },
                onVisible: visible => _kernel.SetPetVisible(visible),
                onMoved: (x, y) =>
                {
                    _kernel.NotePetPosition(x, y);
                    if (_petChat is { IsVisible: true })
                        _petChat.SetAnchor(x, y, _pet!.Bounds.W, _pet.Bounds.H);
                },
                startX: cfg.PetX < 0 ? int.MinValue : cfg.PetX,
                startY: cfg.PetY < 0 ? int.MinValue : cfg.PetY);
        }
        catch (Exception ex) { _pet = null; Log("桌宠启动失败：" + ex.Message); }
    }

    /// <summary>桌宠的独立交互窗口（HTML）：被 ✕ 关掉后下次单击鲸鱼会重新创建。</summary>
    private void EnsurePetChat()
    {
        if (_petChat != null) return;
        try
        {
            _petChat = new PetChatWindow(Path.Combine(AppContext.BaseDirectory, "ui"),
                _dataDir,
                _kernel.Config.WebCompat,
                json => _kernel.InvokeAsync(json),
                () => { _petChat = null; });
        }
        catch (Exception ex) { _petChat = null; Log("桌宠浮窗启动失败：" + ex.Message); }
    }
}
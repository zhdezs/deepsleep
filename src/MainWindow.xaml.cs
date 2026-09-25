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

        ApplyTheme(_kernel.Config.Theme == "dark");
        _ = StartWebAsync();
        ApplyPetVisibility();

        Closed += (_, _) =>
        {
            _closing = true;
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

    /// <summary>界面 20 秒还没渲染出来（WebView2 起不来）→ 显示兜底面板，别让用户看着白窗口。</summary>
    private async Task WatchdogAsync()
    {
        for (int i = 0; i < 40 && !_webReady; i++) await Task.Delay(500);
        if (_webReady || _closing) return;
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
                if (e.IsSuccess) _webReady = true;
                Log($"nav success={e.IsSuccess} err={e.WebErrorStatus} compat={compat}");
                try { sender.PostWebMessageAsJson("{\"ev\":\"uiReady\"}"); } catch { }
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
        catch (Exception ex) { res = "{\"ok\":false,\"err\":" + JsonSerializer.Serialize(ex.Message) + "}"; }
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
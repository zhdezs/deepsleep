using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using TrollWrangler.Core;
using Windows.Graphics;

namespace TrollWrangler;

/// <summary>
/// 桌宠的独立聊天浮窗：和主界面共用同一套 HTML（ui\index.html?pet=1 走精简布局）与同一个内核，
/// 所以在浮窗里说话和主界面完全连贯。
/// </summary>
public sealed class PetChatWindow
{
    private const int W = 384;
    private const int H = 560;

    private readonly Window _window;
    private readonly Grid _root = new();
    private WebView2 _web = new();
    private bool _ready;
    private bool _retried;
    private const string CompatArgs = "--no-sandbox --disable-gpu --disable-features=RendererCodeIntegrity";
    private readonly Func<string, Task<string>> _invoke;
    private readonly Action _onClosed;
    private bool _visible;

    public PetChatWindow(string uiDir, string dataDir, bool compat,
                         Func<string, Task<string>> invoke, Action onClosed)
    {
        _invoke = invoke;
        _onClosed = onClosed;

        _root.Children.Add(_web);
        _window = new Window { Content = _root, Title = "deepsleep · 桌宠" };
        try { _window.SystemBackdrop = new DesktopAcrylicBackdrop(); } catch { }
        try { _window.AppWindow.IsShownInSwitchers = false; } catch { }
        try { _window.AppWindow.Resize(new SizeInt32(W, H)); } catch { }
        _window.Closed += (_, _) => { _visible = false; _onClosed(); };

        _ = InitAsync(uiDir, dataDir, compat);
    }

    private async Task InitAsync(string uiDir, string dataDir, bool compat)
    {
        try
        {
            var opts = new CoreWebView2EnvironmentOptions();
            if (compat) opts.AdditionalBrowserArguments = CompatArgs;
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null,
                Path.Combine(dataDir, "webview"), opts);
            for (int i = 0; i < 40 && _web.XamlRoot == null; i++) await Task.Delay(50);
            await _web.EnsureCoreWebView2Async(env);
            for (int i = 0; i < 40 && _web.CoreWebView2 == null; i++) await Task.Delay(50);
            var core = _web.CoreWebView2;
            if (core == null) return;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.SetVirtualHostNameToFolderMapping(Kernel.UiHost, uiDir, CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping(Kernel.DataHost, dataDir, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += async (s, e) =>
            {
                string res;
                try { res = await _invoke(e.WebMessageAsJson); }
                catch (Exception ex) { res = "{\"ok\":false}"; Debug.WriteLine(ex.Message); }
                try { s.PostWebMessageAsJson(res); } catch { }
            };
            core.NavigationCompleted += (_, e) => { if (e.IsSuccess) _ready = true; };
            core.ProcessFailed += (_, e) =>
            {
                if (_ready || _retried) return;
                _retried = true;
                _window.DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        _root.Children.Remove(_web);
                        _web = new WebView2();
                        _root.Children.Add(_web);
                        await InitAsync(uiDir, dataDir, true);
                    }
                    catch { }
                });
            };
            core.Navigate($"https://{Kernel.UiHost}/index.html?pet=1");
        }
        catch (Exception ex) { Debug.WriteLine("桌宠浮窗初始化失败：" + ex.Message); }
    }

    public void Push(string json)
    {
        try { _web?.CoreWebView2?.PostWebMessageAsJson(json); } catch { }
    }

    public bool IsVisible => _visible;

    public void Show()
    {
        try { _window.AppWindow.Show(); _window.Activate(); _visible = true; } catch { }
    }

    public void Hide()
    {
        try { _window.AppWindow.Hide(); } catch { }
        _visible = false;
    }

    public void Toggle()
    {
        if (_visible) Hide();
        else Show();
    }

    public void Close()
    {
        try { _window.Close(); } catch { }
    }

    /// <summary>把浮窗贴在桌宠旁边（右边放不下就放左边，底边对齐）。</summary>
    public void SetAnchor(int x, int y, int w, int h)
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(_window.AppWindow.Id, DisplayAreaFallback.Nearest);
            var work = area.WorkArea;
            int px = x + w + 10;
            if (px + W > work.X + work.Width) px = Math.Max(work.X, x - W - 10);
            int py = Math.Clamp(y + h - H, work.Y, Math.Max(work.Y, work.Y + work.Height - H));
            _window.AppWindow.MoveAndResize(new RectInt32(px, py, W, H));
        }
        catch { }
    }
}
#if !WINDOWS
using System;
using Photino.NET;

namespace TrollWrangler.CoreHost;

/// <summary>
/// Linux / macOS 的桌面窗口：用系统自带的 WebView（Linux 是 WebKitGTK，macOS 是 WKWebView）
/// 把内核自带的网页端装进一个原生窗口，看起来就是一个正常的桌面应用。
/// Windows 上不用它（Windows 走 WPF 外壳 TrollWrangler），所以整份文件包在 #if !WINDOWS 里。
/// </summary>
internal static class Gui
{
    /// <summary>
    /// 开窗口并阻塞到用户关闭它。返回 true = 正常关闭；false = 窗口起不来（多半是缺 WebKitGTK），
    /// 调用方应退回"纯网页模式"用系统浏览器打开。
    /// </summary>
    public static bool TryRun(string url, string title)
    {
        try
        {
            var window = new PhotinoWindow()
                .SetTitle(title)
                .SetUseOsDefaultSize(false)
                .SetSize(1280, 840)
                .SetMinSize(900, 600)
                .Center()
                .SetResizable(true)
                .SetContextMenuEnabled(true)
                .SetLogVerbosity(0);
            window.Load(new Uri(url));
            window.WaitForClose();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("× 原生窗口起不来：" + ex.Message);
            Console.WriteLine("  Linux 上需要 WebKitGTK（Debian/Ubuntu: sudo apt install libwebkit2gtk-4.1-0；");
            Console.WriteLine("  Arch: sudo pacman -S webkit2gtk-4.1；Fedora: sudo dnf install webkit2gtk4.1）。");
            return false;
        }
    }
}
#endif

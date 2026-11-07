using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler;

public partial class App : Application
{
    /// <summary>主窗口实例互斥体：「双击装扩展」时用来判断是不是已经有窗口在跑。</summary>
    private static Mutex? _uiMutex;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static void InfoBox(string text, string caption) =>
        MessageBoxW(IntPtr.Zero, text, caption, 0x00000040 /* MB_ICONINFORMATION */);

    /// <summary>供桌宠右键菜单调用：退出整个程序。</summary>
    public static void ExitApp()
    {
        try { Current?.Exit(); } catch { Environment.Exit(0); }
    }
    private Window? _window;

    public App()
    {
        InitializeComponent();
        // 崩溃日志：XAML/运行时异常写入 exe 同级 crash.log，便于排查启动崩溃
        UnhandledException += (_, e) =>
        {
            LogCrash("Application.UnhandledException", e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>双击安装扩展的结果，等主窗口建好后再弹提示。</summary>
    private (bool ok, string msg)? _extInstall;

    /// <summary>
    /// 双击 .dslt / .dslp 时系统会用 `--install-ext "路径"` 调起本程序。
    /// 这里只负责安装（此时窗口还没建，弹不了 XAML 对话框），结果留给调用方显示。
    /// 返回 true 表示这次启动是「装扩展」，不是普通打开应用。
    /// </summary>
    private bool TryHandleExtensionInstall()
    {
        try
        {
            var argv = Environment.GetCommandLineArgs();
            int idx = -1;
            for (int i = 0; i < argv.Length; i++)
                if (string.Equals(argv[i], "--install-ext", StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            string? file = (idx >= 0 && idx + 1 < argv.Length) ? argv[idx + 1] : null;
            AppLog("启动参数: " + string.Join(" | ", argv));

            // 兼容「打开方式 → deepsleep」和拖到图标上：第一个参数直接是扩展包路径
            if (file == null)
            {
                for (int i = 1; i < argv.Length; i++)
                {
                    string a = argv[i];
                    if (a.StartsWith("-")) continue;
                    string low = a.ToLowerInvariant();
                    if (low.EndsWith(".dslt") || low.EndsWith(".dslp")) { file = a; break; }
                }
            }
            if (string.IsNullOrWhiteSpace(file)) return false;
            string dataDir = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dataDir);
            var (ok, msg, _) = ExtensionStore.Install(dataDir, file);
            AppLog($"安装 {file} → ok={ok} msg={msg} data={dataDir}");
            _extInstall = (ok, msg);
            return true;
        }
        catch (Exception ex)
        {
            AppLog("安装异常: " + ex);
            _extInstall = (false, "安装失败：" + ex.Message);
            return true;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 互斥体只用来判断「是不是已经有主窗口在跑」，不阻止用户正常开第二个窗口。
        bool anotherRunning = false;
        try
        {
            _uiMutex = new Mutex(true, @"Local\deepsleep.ui", out bool createdNew);
            anotherRunning = !createdNew;
        }
        catch { /* 拿不到互斥体不影响启动 */ }

        // 双击 .dslt / .dslp 会带着 --install-ext "<文件路径>" 启动我们
        bool installOnly = TryHandleExtensionInstall();

        // 应用已经开着：装上就退，别弹第二个窗口。
        // 原窗口切回前台时会重新扫盘，新工具自己就出现在右下角工具清单里。
        if (installOnly && anotherRunning)
        {
            var r0 = _extInstall ?? (false, "没有拿到安装结果");
            AppLog("已有实例在运行 → 只安装，不开窗口");
            InfoBox((r0.ok ? "扩展已安装。\n\n" : "安装失败：\n\n") + r0.msg +
                    (r0.ok ? "\n\n切回 Deep Sleep 窗口就能看到（右下角工具清单 / ⚙ 设置 → 🧩 扩展）。" : ""),
                    r0.ok ? "扩展已安装" : "安装失败");
            Environment.Exit(0);
            return;
        }

        // 顺手登记文件关联（HKCU，当前用户，不需要管理员）。装过一次以后再双击就能直接进这里。
        try { ExtensionStore.RegisterAssociations(Environment.ProcessPath ?? ""); } catch { }
        try
        {
            _window = new MainWindow();
            _window.Activate();
            if (_extInstall is { } r) _ = ShowExtResultAsync(r.ok, r.msg);
        }
        catch (Exception ex)
        {
            LogCrash("OnLaunched", ex);
            throw;
        }
    }

    /// <summary>双击装完扩展后的提示（等界面加载完再弹，否则 XamlRoot 还是空的）。</summary>
    private async Task ShowExtResultAsync(bool ok, string msg)
    {
        try
        {
            await Task.Delay(800);
            var dlg = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = ok ? "扩展已安装" : "安装失败",
                Content = ok ? msg + "\n\n在 ⚙ 设置 → 🧩 扩展 里可以查看、启用或删除。" : msg,
                CloseButtonText = "好",
                XamlRoot = _window?.Content?.XamlRoot,
            };
            await dlg.ShowAsync();
        }
        catch { }
    }

    /// <summary>双击安装的排查日志：写 data\app-install.log（界面弹窗看不到细节）。</summary>
    private static void AppLog(string text)
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app-install.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text + Environment.NewLine);
        }
        catch { }
    }

    private static void LogCrash(string source, Exception? ex)    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n---\n");
        }
        catch { /* 日志写失败不影响主流程 */ }
    }
}

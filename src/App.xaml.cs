using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Threading.Tasks;

namespace TrollWrangler;

public partial class App : Application
{
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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            LogCrash("OnLaunched", ex);
            throw;
        }
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n---\n");
        }
        catch { /* 日志写失败不影响主流程 */ }
    }
}

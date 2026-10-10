using System.Text;
using TrollWrangler;
using TrollWrangler.Core;

namespace DeepSleep.Desktop;

/// <summary>
/// deepsleep 桌面版入口（Linux / macOS）。
/// 外壳与 Windows 桌面版（src\TrollWrangler.csproj）是同一套设计：只当 WebView 宿主，界面全在 ui\ 里，
/// 逻辑全在内核（Deepsleep.Core）；区别只是窗口后端用系统自带的 WebView（Photino）。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        string rootDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string dataDir = ArgStr(args, "--data") ?? Platform.DefaultDataDir();

        // ⚠ 桌面版是从 Finder / 启动台 / 文件管理器点开的，**没有终端可看**。
        //   以前这里没有任何兜底：只要在建窗口之前抛一次异常，用户那边就是"双击没反应 /
        //   图标弹一下就没了"，既没有报错窗口也没有日志，只能靠猜（macOS 版报"无法启动"就是这个处境）。
        //   现在任何未捕获异常都会落到文件里，让"没反应"变成一份可读的报告。
        AppDomain.CurrentDomain.UnhandledException +=
            (_, e) => WriteCrash(dataDir, "AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException +=
            (_, e) => { WriteCrash(dataDir, "TaskScheduler.UnobservedTaskException", e.Exception); e.SetObserved(); };

        try
        {
            try { Directory.CreateDirectory(dataDir); } catch { }
            using var shell = new AppShell(rootDir, dataDir);
            return shell.Run(args);
        }
        catch (Exception ex)
        {
            WriteCrash(dataDir, "Main", ex);
            return 1;
        }
    }

    /// <summary>
    /// 把致命异常写到几个候选位置（数据目录不一定建得出来，就退到临时目录 / 家目录），
    /// 同时打到 stderr —— 从终端跑的时候能直接看见。
    /// </summary>
    private static void WriteCrash(string dataDir, string where, Exception? ex)
    {
        string ver;
        try { ver = typeof(Program).Assembly.GetName().Version?.ToString() ?? "?"; } catch { ver = "?"; }
        string plat;
        try { plat = Platform.DisplayName + " / " + Platform.Rid; } catch { plat = "?"; }

        string text = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] deepsleep 启动或运行失败（" + where + "）\n"
                    + "  版本 " + ver + " · 平台 " + plat + "\n"
                    + "  数据目录 " + dataDir + "\n"
                    + ex + "\n";
        try { Console.Error.WriteLine(text); } catch { }

        var candidates = new List<string>();
        try { if (!string.IsNullOrWhiteSpace(dataDir)) candidates.Add(Path.Combine(dataDir, "crash.log")); } catch { }
        try { candidates.Add(Path.Combine(Path.GetTempPath(), "deepsleep-crash.log")); } catch { }
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home)) candidates.Add(Path.Combine(home, "deepsleep-crash.log"));
        }
        catch { }

        foreach (string p in candidates)
        {
            try { File.AppendAllText(p, text + "\n"); return; }   // 写到第一个能写的地方就够
            catch { }
        }
    }

    private static string? ArgStr(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[++i];
        return null;
    }
}

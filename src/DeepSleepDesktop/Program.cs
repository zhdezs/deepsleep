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
        try { Directory.CreateDirectory(dataDir); } catch { }

        using var shell = new AppShell(rootDir, dataDir);
        return shell.Run(args);
    }

    private static string? ArgStr(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[++i];
        return null;
    }
}

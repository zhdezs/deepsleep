using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TrollWrangler.Core;

namespace TrollWrangler.CoreHost;

/// <summary>
/// deepsleep 内核版（Core）：本机跑一个内核，浏览器里的网页版连上来就能用完整能力
/// （工具 / 文件 / 命令 / 记忆 / 技能 / 网络搜索 / 深度研究）。
/// 只监听 127.0.0.1；所有接口要配对令牌；跨域只放行自家网页版和本机页面。
/// </summary>
internal static partial class Program
{
    private static Kernel _kernel = null!;
    private static string _token = "";
    private static string _rootDir = "";
    private static string _dataDir = "";
    private static int _port = 8756;
    private static TcpListener? _listener;
    private static readonly DateTime Started = DateTime.Now;
    private static readonly object SseGate = new();
    private static readonly List<Res> SseClients = new();

    /// <summary>允许跨域调用的来源（只放行自家网页版，别的一律拒）。</summary>
    private static readonly string[] AllowOrigins =
    {
        "https://zhdezs.github.io",
        "https://zhdezs.gitee.io",
        "https://gitee.com",
    };

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try { Console.Title = "deepsleep 内核版（Core）"; } catch { }

        _rootDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        _port = ArgInt(args, "--port", 8756);
        string? dataArg = ArgStr(args, "--data");
        _dataDir = string.IsNullOrWhiteSpace(dataArg) ? DefaultDataDir() : Path.GetFullPath(dataArg!);
        Directory.CreateDirectory(_dataDir);
        string? inline = ArgStr(args, "--token");
        _token = string.IsNullOrWhiteSpace(inline) ? LoadOrCreateToken(args.Contains("--new-token")) : inline!;

        _kernel = new Kernel();
        _kernel.Push += OnPush;
        try { _kernel.Init(_dataDir); }
        catch (Exception ex) { Console.WriteLine("内核初始化失败：" + ex.Message); return 2; }

        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine("端口 " + _port + " 起不来：" + ex.Message);
            Console.WriteLine("换一个端口再试，例如：deepsleep-core --port 8757");
            return 1;
        }

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Shutdown(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { _kernel.Shutdown(); } catch { } };

        Banner();
        while (true)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { break; }
            _ = Task.Run(() => ServeAsync(client));
        }
        return 0;
    }

    private static async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                using NetworkStream s = client.GetStream();
                Req? req = await ReadRequestAsync(s);
                if (req == null) return;
                var res = new Res(s);
                await HandleAsync(req, res);
            }
        }
        catch { }
    }

    private static void Shutdown()
    {
        Console.WriteLine();
        Console.WriteLine("正在退出内核…");
        try { _listener?.Stop(); } catch { }
        try { _kernel.Shutdown(); } catch { }
        Environment.Exit(0);
    }

    private static void Banner()
    {
        string link = "https://zhdezs.github.io/deepsleep/web/core/#p=" + _port + "&t=" + _token;
        Console.WriteLine();
        Console.WriteLine("┌──────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│  deepsleep 内核版（Core）已启动                              │");
        Console.WriteLine("└──────────────────────────────────────────────────────────────┘");
        Console.WriteLine("  本机地址   http://127.0.0.1:" + _port + "/");
        Console.WriteLine("  配对令牌   " + _token);
        Console.WriteLine("  数据目录   " + _dataDir);
        Console.WriteLine();
        Console.WriteLine("  用法一（本机）：浏览器打开 http://127.0.0.1:" + _port + "/ ，点「内核版网页」");
        Console.WriteLine("  用法二（外网）：打开下面的网址，端口和令牌自动填好（这条链接别外传）：");
        Console.WriteLine("      " + link);
        Console.WriteLine();
        Console.WriteLine("  退出 Ctrl+C ｜ 换端口 --port 8757 ｜ 换令牌 --new-token ｜ 换数据目录 --data 路径");
        Console.WriteLine();
    }

    private static string DefaultDataDir()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string app = Path.Combine(local, "Programs", "deepsleep", "data");
        if (Directory.Exists(app)) return app;
        return Path.Combine(local, "deepsleep-core", "data");
    }

    private static string LoadOrCreateToken(bool force)
    {
        string file = Path.Combine(_dataDir, "core-token.txt");
        try
        {
            if (!force && File.Exists(file))
            {
                string t = File.ReadAllText(file).Trim();
                if (t.Length >= 16) return t;
            }
            string nt = Guid.NewGuid().ToString("N");
            File.WriteAllText(file, nt, new UTF8Encoding(false));
            return nt;
        }
        catch { return Guid.NewGuid().ToString("N"); }
    }

    private static string? ArgStr(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static int ArgInt(string[] args, string name, int def)
    {
        string? s = ArgStr(args, name);
        return int.TryParse(s, out int v) && v > 0 && v < 65536 ? v : def;
    }
}

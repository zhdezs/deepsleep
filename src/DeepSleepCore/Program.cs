using System.Net;
using System.Text;
using TrollWrangler.Core;

namespace TrollWrangler.CoreHost;

/// <summary>
/// deepsleep 内核版（Core）：本机跑一个内核，浏览器里的网页版连上来就能用完整能力
/// （工具 / 文件 / 命令 / 记忆 / 技能 / 网络搜索 / 深度研究）。
/// 服务端（HTTP + 内网穿透）都在 Deepsleep.Core 的 CoreServer 里 —— 桌面客户端用的是同一份实现。
/// </summary>
internal static partial class Program
{
    private static Kernel _kernel = null!;
    private static string _token = "";
    private static string _rootDir = "";
    private static string _dataDir = "";
    private static int _port = 8756;

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        // 管道 / 重定向时（脚本喂输入）按 UTF-8 读，否则中文会被按系统代码页解成乱码。
        // 真控制台不动它：ReadLine 走控制台 API，本来就是 Unicode 正确的。
        if (Console.IsInputRedirected) { try { Console.InputEncoding = Encoding.UTF8; } catch { } }
        try { Console.Title = "deepsleep 内核版（Core）"; } catch { }

        _rootDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        _port = ArgInt(args, "--port", 8756);
        string? dataArg = ArgStr(args, "--data");
        _dataDir = string.IsNullOrWhiteSpace(dataArg) ? DefaultDataDir() : Path.GetFullPath(dataArg!);
        Directory.CreateDirectory(_dataDir);
        string? inline = ArgStr(args, "--token");
        _token = string.IsNullOrWhiteSpace(inline) ? LoadOrCreateToken(args.Contains("--new-token")) : inline!;

        _cliMode = CliWanted(args);
        _kernel = new Kernel();
        if (_cliMode) _kernel.Push += OnCliPush;      // CMD 模式：同一份内核事件流
        try { _kernel.Init(_dataDir); }
        catch (Exception ex) { Console.WriteLine("内核初始化失败：" + ex.Message); return 2; }

        bool up = CoreServer.Start(new CoreServerOptions
        {
            Kernel = _kernel,
            Port = _port,
            Token = _token,
            RootDir = _rootDir,
            DataDir = _dataDir,
            Bind = ListenAddress(args),
            Tunnel = args.Contains("--tunnel"),
            TunnelCmd = ArgStr(args, "--tunnel-cmd"),
            TunnelCfPath = ArgStr(args, "--tunnel-cf"),
        });
        if (!up)
        {
            if (_cliMode) Console.WriteLine("CMD 模式下先不管端口，继续启动。");
            else
            {
                Console.WriteLine("换一个端口再试，例如：deepsleep-core --port 8757");
                return 1;
            }
        }

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Shutdown(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { CoreServer.Stop(); } catch { }
            try { _kernel.Shutdown(); } catch { }
        };

        Banner(args);

        if (_cliMode)
        {
            // CMD 模式：HTTP 服务照旧在后台跑（网页版/桌面端可以同时连），前台变成命令行聊天
            RunCli(args);
            CoreServer.Stop();
            try { _kernel.Shutdown(); } catch { }
            return 0;
        }

        // 内核版就是「服务 + CMD」：桌面窗口由桌面版（Windows = deepsleep.exe；
        // Linux / macOS = deepsleep 桌面版）负责，内核自己不开窗，服务器 / SSH 上也一样用。
        if (!args.Contains("--headless"))
            Console.WriteLine("  浏览器打开上面的网址就能用（桌面窗口请用桌面版；这里是内核版）。");
        System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
        return 0;
    }

    private static void Shutdown()
    {
        Console.WriteLine();
        Console.WriteLine("正在退出内核…");
        CoreServer.Stop();
        try { _kernel.Shutdown(); } catch { }
        Environment.Exit(0);
    }

    private static void Banner(string[] args)
    {
        string link = CoreServer.PublicBound
            ? "http://" + LanHint() + ":" + _port + "/web/core/#t=" + _token
            : "https://zhdezs.github.io/deepsleep/web/core/#p=" + _port + "&t=" + _token;
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
        if (CoreServer.PublicBound)
        {
            Console.WriteLine("      已监听所有网卡（--public / --host），上面的地址局域网内就能打开。");
            Console.WriteLine("      要跨网络超远程提问，加 --tunnel 让内核自动建公网隧道并打印配对链接。");
        }
        else
        {
            Console.WriteLine("      默认只监听本机，所以这条链接只有这台电脑能用。手机在外网提问：");
            Console.WriteLine("      加 --tunnel 重启即可（内置免注册隧道，启动后会打印手机收藏用的配对链接）。");
        }
        Console.WriteLine();
        Console.WriteLine("  超远程  --tunnel 内置隧道 ｜ --tunnel-cmd \"自备穿透命令\" ｜ --public 监听所有网卡");
        Console.WriteLine();
        Console.WriteLine("  退出 Ctrl+C ｜ 换端口 --port 8757 ｜ 换令牌 --new-token ｜ 换数据目录 --data 路径");
#if !WINDOWS
        if (args.Contains("--headless"))
            Console.WriteLine("  只跑服务    内核版不开桌面窗口，用上面的网址在浏览器里打开");
        else
            Console.WriteLine("  桌面窗口    请用桌面版（Windows 的 deepsleep.exe / Linux · macOS 的 deepsleep）");
#endif
        if (_cliMode)
        {
            Console.WriteLine();
            Console.WriteLine("  CMD 模式   直接在这里发消息（--cli 交互 / --say \"一句话\" 发完即退）");
        }
        Console.WriteLine();
    }

    private static string DefaultDataDir() => TrollWrangler.Platform.DefaultDataDir();

    /// <summary>局域网提示地址：取第一块已启用网卡上的 IPv4，取不到就退回机器名。</summary>
    private static string LanHint()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return ua.Address.ToString();
                }
            }
        }
        catch { }
        return Environment.MachineName;
    }

    /// <summary>监听地址：默认只监听本机；--public 监听所有网卡（配合隧道或局域网直连）。</summary>
    private static IPAddress ListenAddress(string[] args)
    {
        string? host = ArgStr(args, "--host");
        if (string.IsNullOrWhiteSpace(host) && args.Contains("--public")) host = "0.0.0.0";
        if (string.IsNullOrWhiteSpace(host)) return IPAddress.Loopback;
        if (!IPAddress.TryParse(host, out var ip)) return IPAddress.Loopback;
        return ip;
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

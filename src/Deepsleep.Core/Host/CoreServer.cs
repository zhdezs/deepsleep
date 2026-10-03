using System.Net;
using System.Net.Sockets;
using TrollWrangler.Core;

namespace TrollWrangler.CoreHost;

/// <summary>启动内核服务需要的参数（内核版 exe 与桌面客户端共用）。</summary>
public sealed class CoreServerOptions
{
    public Kernel Kernel = null!;
    public int Port = 8756;
    public string Token = "";
    public string RootDir = "";
    public string DataDir = "";
    public IPAddress Bind = IPAddress.Loopback;
    /// <summary>内置 cloudflared 快速隧道（--tunnel）。</summary>
    public bool Tunnel;
    /// <summary>自备穿透命令（--tunnel-cmd "frpc ..."）。</summary>
    public string? TunnelCmd;
    /// <summary>指定已有的 cloudflared（--tunnel-cf 路径）。</summary>
    public string? TunnelCfPath;
    public Action<string>? Log;
}

/// <summary>
/// 内核的 HTTP 服务端 + 内网穿透隧道。
/// 内核版 exe（deepsleep-core）和桌面客户端跑的是同一份实现，
/// 所以两边能力、跨域策略、令牌校验完全一致 —— 桌面版不会有「Core 有而我没有」的功能。
/// </summary>
public static partial class CoreServer
{
    private static Kernel _kernel = null!;
    private static string _token = "";
    private static string _rootDir = "";
    private static string _dataDir = "";
    private static int _port = 8756;
    private static bool _public;
    private static volatile bool _running;
    private static DateTime Started = DateTime.Now;
    private static TcpListener? _listener;
    private static readonly object SseGate = new();
    private static readonly List<Res> SseClients = new();

    /// <summary>允许跨域调用的来源（只放行自家网页版，别的一律拒）。</summary>
    private static readonly string[] AllowOrigins =
    {
        "https://zhdezs.github.io",
        "https://zhdezs.gitee.io",
        "https://gitee.com",
    };

    /// <summary>日志回调：桌面端把内核输出显示到界面上；为空就只写控制台。</summary>
    public static Action<string>? Log;
    /// <summary>公网地址抓到后触发（桌面端刷新配对链接用）。</summary>
    public static event Action? TunnelReady;
    /// <summary>外壳专属命令（showWindow / hideWindow / quitApp…）的接管点；返回 null 表示按默认处理。</summary>
    public static Func<string, int, string?, Task<string?>>? ShellOverride;

    public static Kernel Kernel => _kernel;
    public static int Port => _port;
    public static string Token => _token;
    public static string DataDir => _dataDir;
    public static string RootDir => _rootDir;
    public static bool Running => _running;
    public static bool PublicBound => _public;
    public static DateTime StartedAt => Started;

    internal static void Say(string line)
    {
        try { Console.WriteLine(line); } catch { }
        try { Log?.Invoke(line); } catch { }
    }

    internal static void RaiseTunnelReady() { try { TunnelReady?.Invoke(); } catch { } }

    /// <summary>起服务（端口起不来返回 false；隧道是尽力而为，失败不影响服务本身）。</summary>
    public static bool Start(CoreServerOptions o)
    {
        _kernel = o.Kernel;
        _port = o.Port;
        _token = o.Token;
        _rootDir = o.RootDir;
        _dataDir = o.DataDir;
        _public = !IPAddress.IsLoopback(o.Bind);
        Started = DateTime.Now;
        if (o.Log != null) Log = o.Log;

        _kernel.Push += OnPush;

        try
        {
            _listener = new TcpListener(o.Bind, _port);
            _listener.Start();
        }
        catch (Exception ex)
        {
            Say("端口 " + _port + " 起不来：" + ex.Message);
            _listener = null;
        }

        _running = _listener != null;
        if (_running) Task.Run(AcceptLoop);

        StartTunnel(o.Tunnel, o.TunnelCmd, o.TunnelCfPath);
        return _running;
    }

    private static readonly HashSet<string> ExtraTokens = new(StringComparer.Ordinal);

    /// <summary>超级连接的一次性令牌（只在这一轮配对里有效，配对结束就撤掉）。</summary>
    public static void AddExtraToken(string t) { if (t.Length > 0) lock (ExtraTokens) ExtraTokens.Add(t); }
    public static void RemoveExtraToken(string t) { if (t.Length > 0) lock (ExtraTokens) ExtraTokens.Remove(t); }
    internal static bool ExtraTokenOk(string t) { lock (ExtraTokens) return ExtraTokens.Contains(t); }

    /// <summary>
    /// 换绑监听地址。超级连接要在局域网里被对方直连，必须临时监听所有网卡；
    /// 配对结束再换回 127.0.0.1。
    /// </summary>
    public static bool Rebind(IPAddress addr)
    {
        var old = _listener;
        try
        {
            var nl = new TcpListener(addr, _port);
            nl.Start();
            _listener = nl;
            _public = !IPAddress.IsLoopback(addr);
            _running = true;
            Task.Run(AcceptLoop);
            try { old?.Stop(); } catch { }
            return true;
        }
        catch { return false; }
    }

    public static void Stop()
    {
        _running = false;
        StopTunnel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    private static void AcceptLoop()
    {
        var l = _listener;
        if (l == null) return;
        while (true)
        {
            TcpClient client;
            try { client = l.AcceptTcpClient(); }
            catch { break; }
            _ = Task.Run(() => ServeAsync(client));
        }
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
}

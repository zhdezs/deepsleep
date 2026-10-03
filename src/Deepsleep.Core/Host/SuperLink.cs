using System.Net;
using System.Text.Json;

namespace TrollWrangler.CoreHost;

/// <summary>
/// 超级连接：一串 6 位配对码，把两台设备的 deepsleep 连起来，连上就能用远程桌面。
///
/// 被控端（host）：生成配对码 + 一个一次性令牌 → 临时监听网卡并把地址发到信令通道
///                → 对方先试局域网直连（真点对点）；同网不通才开一条临时隧道给它连
///                → 直连成功立刻关掉隧道，保持点对点。
/// 控制端（client）：输入配对码 → 从信令通道拿到地址 → 直连优先，隧道兜底 → 连上后打开远程桌面。
///
/// 信令只传几 KB 的握手消息（ntfy.sh），连上之后数据都在两台设备之间，不经过第三方。
/// </summary>
public static class SuperLink
{
    private static readonly string SelfId = Guid.NewGuid().ToString("N")[..8];
    private static readonly object Gate = new();

    public static string Role { get; private set; } = "";
    public static string Code { get; private set; } = "";
    public static string EphemeralToken { get; private set; } = "";
    public static string State { get; private set; } = "idle";   // idle / waiting / connecting / connected / error
    public static string Message { get; private set; } = "";
    public static string PeerName { get; private set; } = "";
    public static bool P2P { get; private set; }
    public static string BaseUrl { get; private set; } = "";
    public static string TunnelUrl { get; private set; } = "";
    /// <summary>控制端：连上后对方给的令牌（开远程桌面 / 控制台用）。</summary>
    public static string RemoteToken { get; private set; } = "";

    public static event Action? Changed;

    private static CancellationTokenSource? _cts;
    private static bool _tunnelWanted;
    /// <summary>配对前本来的监听姿态：本来开着 --public 的，配对完要还给人家，别一律退回只听本机。</summary>
    private static bool _wasPublic;
    private static DateTime _startedAt = DateTime.Now;

    // ------------------------------------------------------------------ 被控端

    /// <summary>生成配对码，开始等对方连过来。</summary>
    public static void StartHost()
    {
        Stop();
        lock (Gate)
        {
            Role = "host";
            Code = Random.Shared.Next(0, 1000000).ToString("D6");
            EphemeralToken = Guid.NewGuid().ToString("N");
            State = "waiting";
            Message = "把配对码告诉另一台设备，在它的「超级连接」里输入；5 分钟内有效。";
            PeerName = "";
            P2P = false;
            BaseUrl = "";
            TunnelUrl = "";
            _tunnelWanted = false;
            _startedAt = DateTime.Now;
        }
        CoreServer.AddExtraToken(EphemeralToken);
        _wasPublic = CoreServer.PublicBound;     // 记下原样，Stop 时还原
        CoreServer.Rebind(IPAddress.Any);        // 要能在局域网里被连到
        _cts = new CancellationTokenSource();    // 自己管生命周期，别让 5 分钟把已连上的会话掐了
        var ct = _cts.Token;
        _ = Task.Run(() => HostLoop(ct), ct);
        _ = Task.Run(async () =>                  // 5 分钟内没人来就自动作废（配对码别一直有效）
        {
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch { return; }
            if (State != "connected") Stop();
        }, ct);
        Raise();
    }

    private static async Task HostLoop(CancellationToken ct)
    {
        await PublishOffer("", ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Signal.SubscribeAsync(Signal.Topic(Code), msg => OnHostMessage(msg, ct),
                                            ct, since: "5m");
            }
            catch { }
            if (ct.IsCancellationRequested) break;
            await Task.Delay(1200, ct);          // 断线重连
        }
    }

    private static void OnHostMessage(string raw, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var r = doc.RootElement;
            if (!r.TryGetProperty("t", out var te)) return;
            string t = te.GetString() ?? "";
            if (r.TryGetProperty("from", out var fe) && fe.GetString() == SelfId) return;
            long ts = r.TryGetProperty("ts", out var tse) && tse.TryGetInt64(out long v) ? v : 0;
            if (ts > 0 && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > 600) return;

            switch (t)
            {
                case "hello":
                    if (r.TryGetProperty("name", out var ne)) PeerName = ne.GetString() ?? "";
                    Message = "对方（" + (PeerName.Length > 0 ? PeerName : "另一台设备") + "）正在连你…";
                    Raise();
                    _ = PublishOffer(State == "connected" ? TunnelUrl : "", ct);
                    break;
                case "no-lan":
                    EnsureTunnel(ct);
                    break;
                case "p2p":
                    bool direct = r.TryGetProperty("ok", out var oe) && oe.ValueKind == JsonValueKind.True;
                    P2P = direct;
                    State = "connected";
                    if (direct)
                    {
                        CoreServer.StopTunnel();
                        _tunnelWanted = false;
                        Message = "对方已直连（点对点），临时通道已关闭。";
                    }
                    else
                    {
                        Message = "对方已通过临时加密通道连上（外网时走这条）。";
                    }
                    Raise();
                    break;
                case "bye":
                    Stop();
                    break;
            }
        }
        catch { }
    }

    private static void EnsureTunnel(CancellationToken ct)
    {
        if (_tunnelWanted) return;
        _tunnelWanted = true;
        State = "connecting";
        Message = "同网没连上，正在开一条临时加密通道（第一次要下组件，慢一点）…";
        Raise();
        CoreServer.TunnelReady += OnTunnelReady;
        _ = Task.Run(() => { try { CoreServer.StartTunnel(true, null, null); } catch { } });
    }

    private static void OnTunnelReady()
    {
        try { CoreServer.TunnelReady -= OnTunnelReady; } catch { }
        TunnelUrl = CoreServer.TunnelUrl;
        var ct = _cts?.Token ?? CancellationToken.None;
        _ = PublishOffer(TunnelUrl, ct);
        State = "waiting";
        Message = "临时通道已就绪，等对方连上来…";
        Raise();
    }

    private static async Task PublishOffer(string tunnel, CancellationToken ct)
    {
        try
        {
            var lan = LocalEndpoints();
            string payload = JsonSerializer.Serialize(new
            {
                t = "offer",
                from = SelfId,
                code = Code,
                token = EphemeralToken,
                lan,
                tunnel,
                port = CoreServer.Port,
                name = Environment.MachineName,
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            await Signal.PublishAsync(Signal.Topic(Code), payload, ct);
        }
        catch { }
    }

    // ------------------------------------------------------------------ 控制端

    /// <summary>输入 6 位配对码去连对方的设备。</summary>
    public static void Connect(string code)
    {
        code = (code ?? "").Trim();
        Stop();
        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            lock (Gate) { Role = "client"; State = "error"; Message = "配对码是 6 位数字。"; }
            Raise();
            return;
        }
        lock (Gate)
        {
            Role = "client";
            Code = code;
            State = "connecting";
            Message = "正在找这台设备…";
            PeerName = "";
            P2P = false;
            BaseUrl = "";
        }
        _cts = new CancellationTokenSource();    // 长期会话；「找对方」的时限由 JoinLoop 自己的轮次控制
        var ct = _cts.Token;
        _ = Task.Run(() => JoinLoop(code, ct), ct);
        Raise();
    }

    private sealed class Offer
    {
        public string Token = "";
        public string Tunnel = "";
        public string Name = "";
        public List<string> Lan = new();
    }

    private static async Task JoinLoop(string code, CancellationToken ct)
    {
        string topic = Signal.Topic(code);
        var offers = new List<Offer>();
        var gate = new object();

        void OnMsg(string raw)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var r = doc.RootElement;
                if (!r.TryGetProperty("t", out var te) || te.GetString() != "offer") return;
                if (r.TryGetProperty("from", out var fe) && fe.GetString() == SelfId) return;
                var o = new Offer();
                if (r.TryGetProperty("token", out var tk)) o.Token = tk.GetString() ?? "";
                if (r.TryGetProperty("tunnel", out var tu)) o.Tunnel = tu.GetString() ?? "";
                if (r.TryGetProperty("name", out var nm)) o.Name = nm.GetString() ?? "";
                if (r.TryGetProperty("lan", out var ln) && ln.ValueKind == JsonValueKind.Array)
                    foreach (var e in ln.EnumerateArray()) o.Lan.Add(e.GetString() ?? "");
                if (o.Token.Length == 0) return;
                lock (gate)
                {
                    bool dup = offers.Any(x => x.Token == o.Token && x.Tunnel == o.Tunnel);
                    if (!dup) offers.Add(o);
                }
            }
            catch { }
        }

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Signal.SubscribeAsync(topic, OnMsg, ct, since: "5m"); }
                catch { }
                if (ct.IsCancellationRequested) break;
                await Task.Delay(1200, ct);
            }
        }, ct);

        await Task.Delay(500, ct);
        await Signal.PublishAsync(topic, JsonSerializer.Serialize(new
        {
            t = "hello", from = SelfId, name = Environment.MachineName,
            ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }), ct);

        // 1) 局域网直连优先（真点对点）
        Offer? best = null;
        for (int i = 0; i < 8 && best == null && !ct.IsCancellationRequested; i++)
        {
            Offer? cand;
            lock (gate) cand = offers.FirstOrDefault(o => o.Lan.Count > 0);
            if (cand != null)
            {
                // 所有地址一起探，别一个一个等（外网时这些地址全都连不上，串行会白等很久）
                var probes = cand.Lan.Select(async ep =>
                {
                    string burl = ep.StartsWith("http") ? ep : "http://" + ep;
                    return await PingOk(burl, cand.Token) ? burl : null;
                }).ToArray();
                string? hit = null;
                try { hit = (await Task.WhenAll(probes)).FirstOrDefault(x => x != null); } catch { }
                if (hit != null)
                {
                    best = cand;
                    BaseUrl = hit.TrimEnd('/');
                    P2P = true;
                    break;
                }
            }
            if (best == null) await Task.Delay(600, ct);
        }

        if (best != null)
        {
            PeerName = best.Name;
            RemoteToken = best.Token;
            State = "connected";
            Message = "已直连（点对点，同一网络内），延迟最低。";
            Raise();
            await Signal.PublishAsync(topic, JsonSerializer.Serialize(new
            {
                t = "p2p", from = SelfId, ok = true, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }), ct);
            return;
        }

        // 2) 同网不通 → 请对方开临时隧道
        State = "connecting";
        Message = "同一网络里没找到，正在让对方开一条临时加密通道（第一次可能要等半分钟）…";
        Raise();
        await Signal.PublishAsync(topic, JsonSerializer.Serialize(new
        {
            t = "no-lan", from = SelfId, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }), ct);

        for (int i = 0; i < 100 && !ct.IsCancellationRequested; i++)
        {
            Offer? cand;
            lock (gate) cand = offers.LastOrDefault(o => o.Tunnel.Length > 0);
            if (cand != null && await PingOk(cand.Tunnel, cand.Token))
            {
                PeerName = cand.Name;
                RemoteToken = cand.Token;
                BaseUrl = cand.Tunnel.TrimEnd('/');
                TunnelUrl = cand.Tunnel;
                P2P = false;
                State = "connected";
                Message = "已通过临时加密通道连上（对方在别的网络）。";
                Raise();
                await Signal.PublishAsync(topic, JsonSerializer.Serialize(new
                {
                    t = "p2p", from = SelfId, ok = false, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                }), ct);
                return;
            }
            await Task.Delay(1500, ct);
        }

        State = "error";
        Message = "没连上：对方可能已经关掉配对，或者网络把通道挡了。";
        Raise();
        try { await Task.Delay(TimeSpan.FromMinutes(3), ct); } catch { return; }   // 挂着没意义，3 分钟后自己收
        Stop();
    }

    // ------------------------------------------------------------------ 公共

    public static void Stop()
    {
        // 对方还在线的话，打个招呼再走，免得它一直以为还连着。
        string byeCode = Code, byeRole = Role, byeState = State;
        bool notifyPeer = byeRole.Length > 0 && byeCode.Length == 6 && byeState != "idle";
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        try { CoreServer.TunnelReady -= OnTunnelReady; } catch { }
        if (EphemeralToken.Length > 0) CoreServer.RemoveExtraToken(EphemeralToken);
        if (_tunnelWanted)
        {
            CoreServer.StopTunnel();
            _tunnelWanted = false;
        }
        if (Role == "host")
        {
            CoreServer.Rebind(_wasPublic ? IPAddress.Any : IPAddress.Loopback);
            _wasPublic = false;
        }
        lock (Gate)
        {
            if (State != "idle")
            {
                State = "idle";
                Message = "";
            }
            Role = "";
            Code = "";
            EphemeralToken = "";
            PeerName = "";
            P2P = false;
            BaseUrl = "";
            TunnelUrl = "";
            RemoteToken = "";
        }
        Raise();
        if (notifyPeer)
        {
            _ = Task.Run(() => Signal.PublishAsync(Signal.Topic(byeCode), JsonSerializer.Serialize(new
            {
                t = "bye", from = SelfId, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }), CancellationToken.None));
        }
    }

    public static bool IsActive => State != "idle";

    public static string StatusJson() => JsonSerializer.Serialize(new
    {
        ok = true,
        role = Role,
        code = Code,
        state = State,
        message = Message,
        peer = PeerName,
        p2p = P2P,
        baseUrl = BaseUrl,
        tunnel = TunnelUrl,
        remoteToken = RemoteToken,
        lan = LocalEndpoints(),
        port = CoreServer.Port,
        rd = RemoteDesktop.Available,
    });

    private static void Raise() { try { Changed?.Invoke(); } catch { } }

    private static async Task<bool> PingOk(string baseUrl, string token)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1500) };
            string url = baseUrl.TrimEnd('/') + "/api/ping?t=" + Uri.EscapeDataString(token);
            string s = await http.GetStringAsync(url);
            return s.Contains("\"ok\":true", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>本机所有可用的局域网地址（ip:port），对方优先试这些。</summary>
    public static List<string> LocalEndpoints()
    {
        // 同一台机器上开两个 deepsleep 自测时，局域网地址互相连不上，回环能连——放最前面基本 0 延迟。
        var list = new List<string> { "127.0.0.1:" + CoreServer.Port };
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    string ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue;
                    list.Add(ip + ":" + CoreServer.Port);
                }
            }
        }
        catch { }
        return list;
    }
}

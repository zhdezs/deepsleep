using System.Net;
using System.Text.Json;

namespace TrollWrangler.CoreHost;

/// <summary>
/// 超级连接：一串 6 位配对码，把两台设备的 deepsleep 连起来，连上就能用远程桌面。
///
/// 被控端（host）：生成配对码 + 一个一次性令牌 → 临时监听网卡并把地址发到信令通道
///                → 对方先试局域网直连（真点对点）；同时手写一条内置隧道（系统自带 ssh，
///                  零下载）热着备用 → 直连成功立刻关掉隧道，保持点对点。
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
        try { CoreServer.TunnelReady -= OnTunnelReady; } catch { }
        CoreServer.TunnelReady += OnTunnelReady; // 配对期间一直盯着：隧道重建后把新地址再发一遍
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
                    if (State != "connected") EnsureTunnel(ct);   // 先把内置隧道热起来
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
                        Message = "对方已直连（点对点），内置隧道已关闭。";
                    }
                    else
                    {
                        Message = "对方已通过内置隧道连上（外网时走这条）。";
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
        Message = "同网没连上，正在开一条内置隧道（不用下载组件，约 10-40 秒）…";
        Raise();
        CoreServer.TunnelReady += OnTunnelReady;
        _ = Task.Run(() => { try { CoreServer.StartTunnel(true, null, null); } catch { } });
    }

    private static void OnTunnelReady()
    {
        string now = CoreServer.TunnelUrl;
        // 隧道断了 / 正在重建（地址被清空）：把自己的旧地址也作废，
        // 并且不要把空地址当成 offer 发出去 —— 空的 offer 会让对方以为「对方还没建好」，
        // 而继续持有旧地址又会让对方对着死域名白探。两种都不做，等新地址来了再说。
        if (now.Length == 0)
        {
            if (TunnelUrl.Length > 0)
            {
                TunnelUrl = "";
                if (State is "connecting" or "waiting")
                {
                    Message = "内置隧道断了，正在换一条重建（不用管，会自动恢复）…";
                }
                Raise();
            }
            return;
        }
        TunnelUrl = now;
        var ct = _cts?.Token ?? CancellationToken.None;
        _ = PublishOffer(TunnelUrl, ct);        // 新地址立刻发出去（隧道重建后对端也能拿到）
        if (State is "connecting" or "waiting")
        {
            State = "waiting";
            Message = "内置隧道已就绪，等对方连上来…";
        }
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
        //    窗口给足：跨机首次配对时对方要先听到 hello、再回 offer，信令有几秒延迟；
        //    这里等不到 offer 就等于白跑一轮。8 轮 × 600ms 太紧（实测偶发 offer 到得晚），
        //    放宽到 20 轮 × 600ms ≈ 12 秒，代价只是跨网时多等几秒才转隧道。
        Offer? best = null;
        for (int i = 0; i < 20 && best == null && !ct.IsCancellationRequested; i++)
        {
            Offer? cand;
            lock (gate) cand = offers.FirstOrDefault(o => o.Lan.Count > 0);
            if (cand != null)
            {
                // 所有地址一起探，别一个一个等（外网时这些地址全都连不上，串行会白等很久）
                var probes = cand.Lan.Select(async ep =>
                {
                    string burl = ep.StartsWith("http") ? ep : "http://" + ep;
                    // 跨机探测别用 1.5 秒那么久：不可达的地址多数是「连接被拒 / 立即无路由」，
                    // 会秒回；真正慢的是被防火墙丢包的，那种等 1.5 秒也回不来。
                    // 用 900ms 换更快的轮次，同一个地址下一轮还会再试。
                    return await PingOk(burl, cand.Token, 900) ? burl : null;
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
            // 前 2 轮还没见到 offer，顺手催一次 hello：信令偶发丢包时靠它自己恢复，
            // 不然对方以为没人在连，隧道也不会提前热起来。
            if (i == 2 && cand == null)
            {
                try
                {
                    await Signal.PublishAsync(topic, JsonSerializer.Serialize(new
                    {
                        t = "hello", from = SelfId, name = Environment.MachineName,
                        ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    }), ct);
                }
                catch { }
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
        Message = "同一网络里没找到，正在让对方开一条内置隧道（不用下组件，约 10-40 秒）…";
        Raise();
        await Signal.PublishAsync(topic, JsonSerializer.Serialize(new
        {
            t = "no-lan", from = SelfId, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }), ct);

        for (int i = 0; i < 100 && !ct.IsCancellationRequested; i++)
        {
            Offer? cand;
            lock (gate) cand = offers.LastOrDefault(o => o.Tunnel.Length > 0);

            // 对方一直没给隧道地址（hello 可能丢了、或它那边 ssh 还没连上）：
            // 每 10 轮重发一次 no-lan 催它，不然就是干等 2 分半。
            if (cand == null && i > 0 && i % 10 == 0)
            {
                try
                {
                    await Signal.PublishAsync(topic, JsonSerializer.Serialize(new
                    {
                        t = "no-lan", from = SelfId, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    }), ct);
                }
                catch { }
            }

            if (cand != null && await PingOk(cand.Tunnel, cand.Token, 8000))
            {
                PeerName = cand.Name;
                RemoteToken = cand.Token;
                BaseUrl = cand.Tunnel.TrimEnd('/');
                TunnelUrl = cand.Tunnel;
                P2P = false;
                State = "connected";
                Message = "已通过内置隧道连上（对方在别的网络）。";
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

    /// <summary>探一下这个地址是不是「我们要找的那台 deepsleep」，且拿的令牌是对的。</summary>
    private static async Task<bool> PingOk(string baseUrl, string token, int timeoutMs = 1500)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            string url = baseUrl.TrimEnd('/') + "/api/ping?t=" + Uri.EscapeDataString(token);
            string s = await http.GetStringAsync(url);
            // ⚠ 不能只看 "ok":true —— 令牌**错**的时候 /api/ping 也回 ok:true（只是字段少），
            //   于是任何一台跑着 deepsleep 的机器都会被判成「找到了」。
            //   「对令牌」的响应才带 dataDir 这组完整字段，用它当判据才是真的握上手了。
            return s.Contains("\"ok\":true", StringComparison.Ordinal)
                && s.Contains("\"dataDir\":", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>本机所有可用的局域网地址（ip:port），对方优先试这些。</summary>
    public static List<string> LocalEndpoints()
    {
        // ⚠ 这里**不能**放 127.0.0.1。同一台机器上开两个 deepsleep 自测确实靠它才连得上，
        //   但跨机时它会被对方当成「对端的地址」去 ping —— ping 到的是**它自己**的本机端口。
        //   如果它自己正好也开着 deepsleep（或别的服务占了同端口），就会误判「直连成功」，
        //   于是出现「连上了但打开的是自己 / 画面不对」这种极隐蔽的错。同机自测改由下面的
        //   局域网地址覆盖（同机时 192.168.x.x 也是通的），代价是几百毫秒，换来跨机不出错。
        var list = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                // 虚拟网卡（VMware VMnetX / Hyper-V vEthernet / VirtualBox / 蓝牙 / WSL）上的地址
                // 只有本机内部有意义：对方在别的机器上根本路由不到，白等 1.5 秒 / 个。
                // 而且本机装了 VMware 时，这些地址往往排在真实网卡前面，会把探测窗口整个吃掉。
                if (IsVirtualInterface(ni)) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    string ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue;          // APIPA 自配地址
                    if (ip.StartsWith("127.")) continue;              // 回环（见上）
                    list.Add(ip + ":" + CoreServer.Port);
                }
            }
        }
        catch { }

        // 真网卡一个都没有（网线没插 / 只有虚拟网卡）时，退回回环，至少保证同机自测能用。
        if (list.Count == 0) list.Add("127.0.0.1:" + CoreServer.Port);
        return list;
    }

    /// <summary>是不是只会存在于本机内部的虚拟网卡（对方跨机路由不到的那些）。</summary>
    private static bool IsVirtualInterface(System.Net.NetworkInformation.NetworkInterface ni)
    {
        try
        {
            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) return true;

            string d = (ni.Description ?? "") + " " + (ni.Name ?? "");
            string[] marks =
            {
                "VMware", "VirtualBox", "Hyper-V", "vEthernet", "Loopback", "Bluetooth",
                "WSL", "Docker", "TAP-", "Npcap", "ZeroTier", "Tailscale", "Hamachi",
                "Realtek 8812AU",   // 虚拟热点
                "Microsoft Wi-Fi Direct", "虚拟", "蓝牙",
            };
            foreach (string m in marks)
                if (d.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;

            // VMnetX / vEthernet 这类名字本身看不出来，但 VMware 系网关地址有固定形态（x.x.x.1 且本机是网关）
            return false;
        }
        catch { return false; }
    }
}

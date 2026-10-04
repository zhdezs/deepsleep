using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace TrollWrangler.CoreHost;

/// <summary>
/// 内网穿透：把内核的本地端口通过隧道暴露到公网，人在外面（手机 / 别的电脑）也能提问。
///
/// 内置隧道（默认、手写、零下载）：直接驱动系统自带的 ssh 建一条反向隧道
///   （serveo.net / localhost.run 这些免费公共入口，谁先连上、谁自检通过就用谁），
///   拿到 https 地址后先自己访问一次 /api/ping 确认真能通，再对外公布；
///   通道断了自动换一条重建，并把新地址重新发布给超级连接的对端。
///   Windows 10+ / macOS / Linux 都自带 ssh，所以不用下任何组件。
///
/// 另外两种用法（可选）：
///   --tunnel-cf 路径    自己已有的 cloudflared（也可设环境变量 DS_TUNNEL_CF）。
///   --tunnel-cmd "命令" 自己的穿透工具（frp / cpolar / ngrok / ssh -R … 都行），
///                       内核从它的输出里抓第一个公网地址。
///
/// 抓到公网地址后：打印配对链接（带令牌）、写 &lt;dataDir&gt;\tunnel.txt，桌面端和网页端都能读。
/// </summary>
public static partial class CoreServer
{
    private static Process? _tunnel;
    private static string _tunnelUrl = "";
    private static bool _builtinCf;          // 当前这条是 cloudflared（只认 trycloudflare 域名）
    private static volatile bool _tunnelStop = true;
    private static int _tunnelGen;

    /// <summary>隧道建好后的公网地址（空 = 还没建好 / 没开）。</summary>
    public static string TunnelUrl => _tunnelUrl;
    private static readonly object TunnelGate = new();

    /// <summary>起隧道。wanted = --tunnel，custom = --tunnel-cmd，cfPath = --tunnel-cf。</summary>
    public static void StartTunnel(bool wanted, string? custom, string? cfPath)
    {
        if (!wanted && string.IsNullOrWhiteSpace(custom) && string.IsNullOrWhiteSpace(cfPath)) return;
        StopTunnel();
        _tunnelStop = false;
        if (!string.IsNullOrWhiteSpace(custom)) { StartCustomTunnel(custom!); return; }
        if (!string.IsNullOrWhiteSpace(cfPath)) { StartCloudflared(cfPath!); return; }
        StartBuiltinTunnel();
    }

    /// <summary>关掉隧道（没开也无妨）。</summary>
    public static void StopTunnel()
    {
        _tunnelStop = true;
        _tunnelGen++;
        var p = _tunnel;
        _tunnel = null;
        lock (TunnelGate) _tunnelUrl = "";
        try { if (p is { HasExited: false }) p.Kill(true); } catch { }
    }

    // ================================================================ 内置隧道（手写）

    private sealed class SshEntry
    {
        public string Name = "";
        public string Target = "";
        public int Port = 22;
    }

    /// <summary>可用的隧道入口：默认两个免费的公共入口，可用 DEEPSLEEP_TUNNEL_SSH 换（user@host[:端口]，逗号分隔）。</summary>
    private static List<SshEntry> SshEntries()
    {
        var list = new List<SshEntry>();
        string? env = Environment.GetEnvironmentVariable("DEEPSLEEP_TUNNEL_SSH");
        if (!string.IsNullOrWhiteSpace(env))
        {
            foreach (string raw in env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string item = raw;
                int port = 22;
                int at = item.LastIndexOf(':');
                if (at > 0 && int.TryParse(item[(at + 1)..], out int p)) { port = p; item = item[..at]; }
                if (item.Length == 0) continue;
                list.Add(new SshEntry { Name = item, Target = item, Port = port });
            }
            return list;
        }
        list.Add(new SshEntry { Name = "serveo.net", Target = "serveo.net", Port = 22 });
        list.Add(new SshEntry { Name = "localhost.run", Target = "nokey@localhost.run", Port = 22 });
        return list;
    }

    /// <summary>系统自带的 ssh（Windows 10+ / macOS / Linux 都有，不用另外装）。</summary>
    private static string? FindSsh()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string sys = Path.Combine(win, "System32", "OpenSSH", "ssh.exe");
                if (File.Exists(sys)) return sys;
            }
            catch { }
        }
        string? onPath = FindOnPath("ssh");
        if (onPath != null) return onPath;
        return File.Exists("/usr/bin/ssh") ? "/usr/bin/ssh" : null;
    }

    /// <summary>已经有的 cloudflared（不下载，找不到就算了）。</summary>
    private static string? CloudflaredPath()
    {
        string? env = Environment.GetEnvironmentVariable("DS_TUNNEL_CF");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        bool win = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string name = win ? "cloudflared.exe" : "cloudflared";
        try
        {
            string local = Path.Combine(Path.Combine(_dataDir, "tunnel"), name);
            if (File.Exists(local)) return local;
        }
        catch { }
        return FindOnPath(name);
    }

    private static void StartBuiltinTunnel()
    {
        _builtinCf = false;
        string? ssh = FindSsh();
        string? cf = CloudflaredPath();
        if (ssh == null && cf == null)
        {
            Say("  隧道        这台机器上没有 ssh（Windows 10+ 系统自带），也没找到 cloudflared，隧道没建成");
            return;
        }
        int gen = _tunnelGen;
        _ = Task.Run(() => BuiltinTunnelLoop(gen, ssh, cf));
    }

    private static void BuiltinTunnelLoop(int gen, string? ssh, string? cf)
    {
        while (!_tunnelStop && gen == _tunnelGen)
        {
            // 免费公共入口速度飘得厉害（serveo 有时 90KB/s、有时十几 KB/s），所以建两条比一比，留快的。
            var found = new List<Candidate>();
            if (ssh != null)
            {
                foreach (var entry in SshEntries())
                {
                    if (_tunnelStop || gen != _tunnelGen) { KillAll(found); return; }
                    if (found.Count >= 2) break;
                    var job = new TunnelJob(entry.Name, false);
                    Say("  隧道        正在用手写隧道连 " + entry.Name + " …");
                    try { job.StartSsh(ssh, entry.Target, entry.Port, _port, _dataDir); }
                    catch (Exception ex) { Say("  隧道        ssh 起不来：" + ex.Message); continue; }
                    if (job.TryReady(gen, out string u, out double kbps)) found.Add(new Candidate(job, u, kbps));
                    else job.Kill();
                }
            }
            if (cf != null && found.Count < 2 && !_tunnelStop && gen == _tunnelGen)
            {
                var job = new TunnelJob("cloudflared", true);
                Say("  隧道        正在用手头已有的 cloudflared 建隧道…");
                bool started = true;
                try { job.StartCloudflared(cf, _port); }
                catch (Exception ex) { Say("  隧道        cloudflared 起不来：" + ex.Message); started = false; }
                if (!started) job.Kill();
                else if (job.TryReady(gen, out string u, out double kbps)) found.Add(new Candidate(job, u, kbps));
                else job.Kill();
            }

            if (found.Count == 0)
            {
                if (_tunnelStop || gen != _tunnelGen) return;
                Say("  隧道        这一轮都没连上，5 秒后再试…");
                for (int i = 0; i < 50 && !_tunnelStop && gen == _tunnelGen; i++) Thread.Sleep(100);
                continue;
            }

            found.Sort((a, b) => b.Kbps.CompareTo(a.Kbps));
            for (int i = 1; i < found.Count; i++)
            {
                Say("  隧道        " + found[i].Job.Name + " 实测 " + SpeedText(found[i].Kbps) + "，比另一条慢，先不用");
                found[i].Job.Kill();
            }
            if (found.Count > 1) Say("  隧道        两条都能用，留快的这条");
            var best = found[0];
            best.Job.Publish(best.Url);
            if (best.Job.Monitor(gen)) return;
            // 通道断了 → 回到 while 重来一轮（重新建 + 重新比）
        }
    }

    private sealed class Candidate
    {
        public readonly TunnelJob Job;
        public readonly string Url;
        public readonly double Kbps;
        public Candidate(TunnelJob job, string url, double kbps) { Job = job; Url = url; Kbps = kbps; }
    }

    private static void KillAll(List<Candidate> list)
    {
        foreach (var c in list) c.Job.Kill();
    }

    internal static string SpeedText(double kbps) => kbps <= 0 ? "速度没测出来" : (kbps >= 1024 ? (kbps / 1024).ToString("F1") + " MB/s" : kbps.ToString("F0") + " KB/s");

    /// <summary>一条隧道进程（ssh 反向隧道或 cloudflared），负责抓地址、自检、上报、看守。</summary>
    private sealed class TunnelJob
    {
        private readonly string _name;
        private readonly bool _cf;
        private readonly bool _strictPath;
        private readonly StringBuilder _tail = new();
        private readonly object _log = new();
        private Process? _proc;
        private string _url = "";
        private string _tested = "";
        private int _fails;

        public TunnelJob(string name, bool cloudflared) : this(name, cloudflared, !cloudflared) { }

        public TunnelJob(string name, bool cloudflared, bool strictPath)
        {
            _name = name; _cf = cloudflared; _strictPath = strictPath;
        }

        public bool HasExited
        {
            get { try { return _proc == null || _proc.HasExited; } catch { return true; } }
        }

        public void StartSsh(string ssh, string target, int port, int localPort, string dataDir)
        {
            var psi = new ProcessStartInfo(ssh)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            void O(string s) => psi.ArgumentList.Add(s);
            O("-T");
            if (port != 22) { O("-p"); O(port.ToString()); }
            O("-R"); O("80:127.0.0.1:" + localPort);          // 远端 80 → 本机内核
            O("-o"); O("StrictHostKeyChecking=accept-new");   // 第一次自动记指纹，别卡在 yes/no
            O("-o"); O("UserKnownHostsFile=" + Path.Combine(dataDir, "ssh_known_hosts"));
            O("-o"); O("ServerAliveInterval=15");
            O("-o"); O("ServerAliveCountMax=3");
            O("-o"); O("ExitOnForwardFailure=yes");
            O("-o"); O("ConnectTimeout=12");
            O("-o"); O("NumberOfPasswordPrompts=1");   // 免费入口是匿名登录，别卡在多轮密码输入
            O(target);
            Launch(psi);
        }

        /// <summary>自己的穿透命令（frp / ngrok …）：只启动进程，地址照样从输出里抓。</summary>
        public void StartAny(ProcessStartInfo psi) => Launch(psi);

        public void StartCloudflared(string exe, int localPort)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            psi.ArgumentList.Add("tunnel");
            psi.ArgumentList.Add("--url");
            psi.ArgumentList.Add("http://127.0.0.1:" + localPort);
            psi.ArgumentList.Add("--no-autoupdate");
            Launch(psi);
        }

        private void Launch(ProcessStartInfo psi)
        {
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (_, e) => Feed(e.Data);
            _proc.ErrorDataReceived += (_, e) => Feed(e.Data);
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
        }

        public void Kill()
        {
            try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        }

        private void Feed(string? line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (_log)
            {
                _tail.AppendLine(line);
                if (_tail.Length > 4000) _tail.Remove(0, _tail.Length - 3000);
            }
            if (_url.Length > 0) return;
            string? u = ExtractUrl(line);
            if (u != null) _url = u;
        }

        public string Tail()
        {
            lock (_log)
            {
                string s = _tail.ToString().Trim();
                return s.Length > 240 ? s[^240..] : s;
            }
        }

        public string Name => _name;

        /// <summary>等地址 + 自检 + 测速；可用就返回 true（是否采用由外层按速度决定）。</summary>
        public bool TryReady(int gen, out string url, out double kbps)
        {
            url = "";
            kbps = 0;
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline && !_tunnelStop && gen == _tunnelGen)
            {
                if (HasExited)
                {
                    Say("  隧道        " + _name + " 退出了：" + Tail());
                    return false;
                }
                string u = _url;
                if (u.Length > 0 && u != _tested)
                {
                    _tested = u;
                    _fails = 0;
                    Say("  隧道        " + _name + " 给了地址 " + u + "，正在自检…");
                }
                if (_tested.Length > 0)
                {
                    if (PingTunnel(_tested))
                    {
                        url = _tested;
                        kbps = MeasureSpeed(_tested);
                        return true;
                    }
                    if (++_fails >= 4)
                    {
                        Say("  隧道        " + _name + " 的地址连不上，换下一个入口");
                        return false;
                    }
                    Thread.Sleep(2500);
                    continue;
                }
                Thread.Sleep(600);
            }
            if (!_tunnelStop && gen == _tunnelGen) Say("  隧道        " + _name + " 超时没给出可用地址");
            return false;
        }

        /// <summary>通道看守：进程没了或连续自检失败就返回 false（外层换一条重建）。</summary>
        public bool Monitor(int gen)
        {
            while (!_tunnelStop && gen == _tunnelGen)
            {
                Thread.Sleep(15000);
                if (_tunnelStop || gen != _tunnelGen) return true;
                if (HasExited) { Say("  隧道        通道进程退出了"); return false; }
                if (PingTunnel(_url)) { _fails = 0; continue; }
                if (++_fails >= 3) { Say("  隧道        通道连续自检失败，重建"); return false; }
            }
            return true;
        }

        public void Publish(string url)
        {
            _tunnel = _proc;
            _builtinCf = _cf;
            lock (TunnelGate) _tunnelUrl = url;
            try { File.WriteAllText(Path.Combine(_dataDir, "tunnel.txt"), url, new UTF8Encoding(false)); } catch { }
            Say("");
            Say("  ┌─ 公网地址（超远程提问用这个） ────────────────────────────────");
            Say("  │  " + url);
            Say("  │  通道：" + _name + "（内置，零下载；已自检连通）");
            Say("  │  配对链接（手机收藏这个，令牌已带好，别外传）：");
            Say("  │  " + url + "/web/core/#t=" + _token);
            Say("  │  通道断了会自动换一条重建，地址跟着变，重新点配对即可。");
            Say("  └──────────────────────────────────────────────────────────────");
            Say("");
            RaiseTunnelReady();
        }

        private static readonly Regex UrlRe = new(@"https://[A-Za-z0-9][A-Za-z0-9.\-]*\.[A-Za-z]{2,}(/[^\s""']*)?", RegexOptions.Compiled);

        /// <summary>从输出里挑出真正的隧道地址（把服务商自己的官网 / 文档链接滤掉）。</summary>
        private string? ExtractUrl(string line)
        {
            foreach (Match m in UrlRe.Matches(line))
            {
                string raw = m.Value.TrimEnd('.', ',', ')', '"', '\'', '/');
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) continue;
                string host = uri.Host.ToLowerInvariant();
                if (_cf)
                {
                    if (!host.EndsWith(".trycloudflare.com", StringComparison.Ordinal)) continue;
                }
                else
                {
                    if (host is "localhost.run" or "serveo.net" or "www.serveo.net" or "pinggy.io") continue;
                    if (host.EndsWith(".localhost.run", StringComparison.Ordinal)) continue;
                    if (host.EndsWith(".pinggy.io", StringComparison.Ordinal) || host.EndsWith(".pinggy.link", StringComparison.Ordinal)) continue;
                    if (host.StartsWith("docs.", StringComparison.Ordinal) || host.StartsWith("console.", StringComparison.Ordinal) ||
                        host.StartsWith("admin.", StringComparison.Ordinal) || host.StartsWith("www.", StringComparison.Ordinal)) continue;
                    if (host.StartsWith("cloudflare.com", StringComparison.Ordinal) || host.EndsWith(".cloudflare.com", StringComparison.Ordinal)) continue;
                    if (_strictPath && uri.AbsolutePath.Length > 1) continue;   // 隧道地址只有域名，带路径的是文档链接
                }
                return uri.GetLeftPart(UriPartial.Authority);
            }
            return null;
        }
    }

    /// <summary>通道测速：从公网拉 256KB（/api/speedtest），返回 KB/s；失败返回 0。</summary>
    private static double MeasureSpeed(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("deepsleep/" + VersionString() + " (tunnel-speedtest)");
            string target = url.TrimEnd('/') + "/api/speedtest?n=262144&t=" + Uri.EscapeDataString(_token);
            var sw = Stopwatch.StartNew();
            using var resp = http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            using var stream = resp.Content.ReadAsStream();
            byte[] buf = new byte[65536];
            long got = 0; int rn;
            while ((rn = stream.Read(buf, 0, buf.Length)) > 0) got += rn;
            sw.Stop();
            if (got < 65536 || sw.Elapsed.TotalSeconds <= 0) return 0;
            double kbps = got / 1024.0 / sw.Elapsed.TotalSeconds;
            Say("  隧道        " + url + " 实测 " + SpeedText(kbps));
            return kbps;
        }
        catch { return 0; }
    }

    /// <summary>自检：自己从公网访问一次本机内核，确认这条隧道真的通。</summary>
    private static bool PingTunnel(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("deepsleep/" + VersionString() + " (tunnel-selfcheck)");
            string s = http.GetStringAsync(url.TrimEnd('/') + "/api/ping?t=" + Uri.EscapeDataString(_token))
                           .GetAwaiter().GetResult();
            return s.Contains("\"ok\":true", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    // ================================================================ 自备穿透命令 / cloudflared

    private static void StartCustomTunnel(string custom)
    {
        _builtinCf = false;
        int gen = _tunnelGen;
        _ = Task.Run(() =>
        {
            while (!_tunnelStop && gen == _tunnelGen)
            {
                var job = new TunnelJob("自定义命令", false, false);
                try
                {
                    var psi = TrollWrangler.Platform.ShellStartInfo(custom, null);
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    Say("  隧道        自定义命令：" + custom);
                    job.StartAny(psi);
                }
                catch (Exception ex) { Say("  隧道        启动失败：" + ex.Message); return; }
                if (job.TryReady(gen, out string cu, out _)) { job.Publish(cu); if (!job.Monitor(gen)) continue; return; }
                job.Kill();
                Say("  隧道        自定义命令没给出可用地址，10 秒后重试…");
                for (int i = 0; i < 100 && !_tunnelStop && gen == _tunnelGen; i++) Thread.Sleep(100);
            }
        });
    }

    private static void StartCloudflared(string cfPath)
    {
        string? exe = File.Exists(cfPath) ? cfPath : CloudflaredPath();
        if (exe == null)
        {
            Say("  隧道        没找到 cloudflared：" + cfPath);
            return;
        }
        try
        {
            Say("  隧道        cloudflared 快速隧道");
            var job = new TunnelJob("cloudflared", true);
            job.StartCloudflared(exe, _port);
        }
        catch (Exception ex) { Say("  隧道        启动失败：" + ex.Message); }
    }

    private static string? FindOnPath(string exe)
    {
        try
        {
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                string full = Path.Combine(p.Trim(), exe);
                try { if (File.Exists(full)) return full; } catch { }
            }
        }
        catch { }
        return null;
    }
}

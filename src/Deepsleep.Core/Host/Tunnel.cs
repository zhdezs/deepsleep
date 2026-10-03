using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace TrollWrangler.CoreHost;

/// <summary>
/// 内网穿透：把内核的本地端口通过隧道暴露到公网，人在外面（手机 / 别的电脑）也能提问。
///
/// 两种用法：
///   --tunnel            内置 cloudflared 快速隧道（免费、自带 HTTPS、不用注册）。
///                       第一次会自动下载 cloudflared（国内慢，可用环境变量 DS_TUNNEL_CF_URL 换镜像，
///                       或 --tunnel-cf 指定已有路径）。
///   --tunnel-cmd "命令" 用自己的穿透工具（frp / cpolar / ngrok / ssh -R … 都行），
///                       从它的输出里抓第一个公网地址。
///
/// 抓到公网地址后：打印配对链接（带令牌）、写 &lt;dataDir&gt;\tunnel.txt，桌面端和网页端都能读。
/// </summary>
public static partial class CoreServer
{
    private static Process? _tunnel;
    private static string _tunnelUrl = "";
    private static bool _builtinCf;   // 内置 cloudflared 模式（只认 trycloudflare 域名）

    /// <summary>隧道建好后的公网地址（空 = 还没建好 / 没开）。</summary>
    public static string TunnelUrl => _tunnelUrl;
    private static readonly object TunnelGate = new();

    /// <summary>起隧道。wanted = --tunnel，custom = --tunnel-cmd，cfPath = --tunnel-cf。</summary>
    public static void StartTunnel(bool wanted, string? custom, string? cfPath)
    {
        string? cfArg = cfPath;
        if (!wanted && string.IsNullOrWhiteSpace(custom)) return;

        try
        {
            ProcessStartInfo psi;
            if (!string.IsNullOrWhiteSpace(custom))
            {
                _builtinCf = false;
                psi = TrollWrangler.Platform.ShellStartInfo(custom!, null);
                Say("  隧道        自定义命令：" + custom);
            }
            else
            {
                string? exe = !string.IsNullOrWhiteSpace(cfArg) && File.Exists(cfArg) ? cfArg : EnsureCloudflared();
                if (exe == null)
                {
                    Say("  隧道        跳过（cloudflared 没准备好，见上面的提示）");
                    return;
                }
                _builtinCf = true;
                psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                psi.ArgumentList.Add("tunnel");
                psi.ArgumentList.Add("--url");
                psi.ArgumentList.Add("http://127.0.0.1:" + _port);
                psi.ArgumentList.Add("--no-autoupdate");
                Say("  隧道        cloudflared 快速隧道（第一次建可能等 20-30 秒）");
            }
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            _tunnel = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _tunnel.OutputDataReceived += (_, e) => TunnelLine(e.Data);
            _tunnel.ErrorDataReceived += (_, e) => TunnelLine(e.Data);
            _tunnel.Start();
            _tunnel.BeginOutputReadLine();
            _tunnel.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            Say("  隧道        启动失败：" + ex.Message);
        }
    }

    /// <summary>从隧道进程的输出里抓公网地址（cloudflared 在 stderr 打，别的工具打在哪都有）。</summary>
    private static void TunnelLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        if (_tunnelUrl.Length > 0) return;
        Match m;
        if (_builtinCf)
        {
            // cloudflared 的正式地址只有 *.trycloudflare.com
            // （它还会在开头打印自己的条款页 www.cloudflare.com，别抓错）
            m = Regex.Match(line, @"https://[a-z0-9][a-z0-9.\-]*\.trycloudflare\.com", RegexOptions.IgnoreCase);
        }
        else
        {
            m = Regex.Match(line, @"https?://[A-Za-z0-9][A-Za-z0-9.\-]*\.[A-Za-z]{2,}(:\d+)?");
            if (m.Success) m = FilterTunnelUrl(m);
        }
        if (!m.Success) return;
        lock (TunnelGate)
        {
            if (_tunnelUrl.Length > 0) return;
            _tunnelUrl = m.Value.TrimEnd('/');
        }
        string link = _tunnelUrl + "/web/core/#t=" + _token;
        try { File.WriteAllText(Path.Combine(_dataDir, "tunnel.txt"), _tunnelUrl, new UTF8Encoding(false)); } catch { }
        Say("");
        Say("  ┌─ 公网地址（超远程提问用这个） ────────────────────────────────");
        Say("  │  " + _tunnelUrl);
        Say("  │  配对链接（手机收藏这个，令牌已带好，别外传）：");
        Say("  │  " + link);
        Say("  │  刚建好要等 20-30 秒才通；打不开大多是 DNS 解析不了这个域名，");
        Say("  │  把手机/电脑的 DNS 换成 114.114.114.114 或 223.5.5.5 再试。");
        Say("  └──────────────────────────────────────────────────────────────");
        Say("");
        RaiseTunnelReady();
    }

    /// <summary>自定义隧道命令的输出里，把本机地址和 cloudflare 官方站过滤掉。</summary>
    private static Match FilterTunnelUrl(Match m)
    {
        string host;
        try { host = new Uri(m.Value).Host; } catch { return Match.Empty; }
        if (host is "127.0.0.1" or "localhost" or "0.0.0.0") return Match.Empty;
        if (host.EndsWith("cloudflare.com", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith("trycloudflare.com", StringComparison.OrdinalIgnoreCase)) return Match.Empty;
        return m;
    }

    /// <summary>关掉隧道（没开也无妨）。</summary>
    public static void StopTunnel()
    {
        try { if (_tunnel is { HasExited: false }) _tunnel.Kill(true); } catch { }
        _tunnel = null;
    }

    // ------------------------------------------------------------------ cloudflared

    private static string? EnsureCloudflared()
    {
        string dir = Path.Combine(_dataDir, "tunnel");
        bool win = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string name = win ? "cloudflared.exe" : "cloudflared";
        string local = Path.Combine(dir, name);

        string? env = Environment.GetEnvironmentVariable("DS_TUNNEL_CF");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        if (File.Exists(local)) return local;

        string? found = FindOnPath(win ? "cloudflared.exe" : "cloudflared");
        if (found != null) return found;

        try { Directory.CreateDirectory(dir); } catch { }
        string url = Environment.GetEnvironmentVariable("DS_TUNNEL_CF_URL") ?? "";
        if (string.IsNullOrWhiteSpace(url)) url = DefaultCloudflaredUrl(win);
        if (string.IsNullOrWhiteSpace(url))
        {
            Say("  隧道        这个平台没有自动下载地址，请用 --tunnel-cf 指定 cloudflared 路径");
            return null;
        }
        Say("  隧道        正在下载 cloudflared（约 50 MB，国内可能很慢）…");
        Say("              卡住就按 Ctrl+C，手动下好再用 --tunnel-cf 路径 指定，");
        Say("              或设环境变量 DS_TUNNEL_CF_URL 换成镜像地址。");
        try
        {
            DownloadCloudflared(url, local, win);
            Say("  隧道        cloudflared 就绪：" + local);
            return local;
        }
        catch (Exception ex)
        {
            Say("  隧道        下载失败：" + ex.Message);
            return null;
        }
    }

    private static string DefaultCloudflaredUrl(bool win)
    {
        const string Base = "https://github.com/cloudflare/cloudflared/releases/latest/download/";
        if (win) return RuntimeInformation.OSArchitecture == Architecture.X64 ? Base + "cloudflared-windows-amd64.exe" : "";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Base + (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "cloudflared-darwin-arm64.tgz" : "cloudflared-darwin-amd64.tgz");
        return Base + (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "cloudflared-linux-arm64" : "cloudflared-linux-amd64");
    }

    private static void DownloadCloudflared(string url, string dest, bool win)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        string tmp = dest + ".part";
        using (var src = resp.Content.ReadAsStream())
        using (var fs = File.Create(tmp))
        {
            byte[] buf = new byte[1 << 20];
            long total = 0, last = 0;
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                fs.Write(buf, 0, n);
                total += n;
                if (total - last >= 8 << 20) { last = total; Say("              已下载 " + (total / 1048576.0).ToString("F1") + " MB"); }
            }
        }
        // macOS / Linux 给的是 tgz（darwin）或裸二进制
        if (url.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)) ExtractCloudflared(tmp, dest);
        else File.Move(tmp, dest, true);
        try { File.Delete(tmp); } catch { }
        if (!win) TryChmod(dest);
    }

    private static void ExtractCloudflared(string tgz, string dest)
    {
        using var gz = new GZipStream(File.OpenRead(tgz), CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.DataStream == null) continue;
            if (!string.Equals(Path.GetFileName(entry.Name), "cloudflared", StringComparison.Ordinal)) continue;
            using var outFs = File.Create(dest);
            entry.DataStream.CopyTo(outFs);
            return;
        }
        throw new InvalidOperationException("压缩包里没找到 cloudflared");
    }

    private static void TryChmod(string path)
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                           UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                           UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch { }
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

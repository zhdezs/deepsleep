using TrollWrangler.Core;

namespace TrollWrangler.CoreHost;

/// <summary>令牌校验 + 跨域放行（只放行自家网页版和本机页面）。</summary>
public static partial class CoreServer
{
    /// <summary>内核版本号（网页端 / 桌面端显示用）。</summary>
    public static string VersionString()
    {
        try
        {
            var asm = typeof(Kernel).Assembly;
            var info = (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(
                asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            string v = info?.InformationalVersion ?? "";
            int cut = v.IndexOf('+');                     // 去掉 SourceLink 那串 +commit
            if (cut > 0) v = v[..cut];
            if (v.Length > 0) return v;                   // 能显示 2.4.1-alpha 这种
            return asm.GetName().Version?.ToString(3) ?? "0.0.0";
        }
        catch { return "0.0.0"; }
    }

    private static bool TokenOk(Req req)
    {
        string t = req.Header("X-DS-Token");
        if (t.Length == 0) t = req.Q("token") ?? "";
        if (t.Length == 0) t = req.Q("t") ?? "";
        bool ok = t.Length > 0 &&
                  (string.Equals(t, _token, StringComparison.Ordinal) || ExtraTokenOk(t));
        if (ok) Interlocked.Exchange(ref _tokenFails, 0);
        return ok;
    }

    private static int _tokenFails;

    /// <summary>
    /// 令牌错了就让对方多等一会儿：连续错越多次等越久（最多 5 秒）。
    /// 不封 IP —— 走隧道时所有请求的来源都是本机 127.0.0.1，封 IP 会把自己也关在门外。
    /// </summary>
    private static int TokenPenaltyMs()
    {
        int n = Interlocked.Increment(ref _tokenFails);
        return Math.Min(400 * n, 5000);
    }

    private static bool ApplyCors(Req req, Res res, string origin)
    {
        if (origin.Length > 0)
        {
            // 公网隧道 / 局域网 IP 直接打开内核自带页面时：Origin 的主机名和请求的 Host 一样 → 同源，放行。
            // （只放行「页面自己调自己」，别家网站仍然不在白名单里。）
            bool sameHost = false;
            try
            {
                var ou = new Uri(origin);
                string reqHost = req.Header("Host");
                sameHost = ou.Authority.Length > 0 &&
                           ou.Authority.Equals(reqHost, StringComparison.OrdinalIgnoreCase);
            }
            catch { /* Origin 不是合法 URL，按不放行处理 */ }
            bool ok = AllowOrigins.Contains(origin) || origin == "null" || sameHost
                      || origin.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)
                      || origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase)
                      // 超级连接的应用内远程桌面：桌面端界面本身是个 WebView，把对端页面
                      // 嵌在 iframe 里跑（不再拉系统浏览器）。这时 Origin 是「外层界面所在的源」，
                      // 跟本内核的 Host 对不上，上面的 sameHost 会判失败。
                      // 判据改成看令牌：请求带着有效的配对令牌（本机令牌或超级连接的一次性令牌）
                      // 就放行 —— 令牌本身就是密钥，能拿出来说明这台机器已经被合法配对了。
                      || TokenOk(req);
            if (!ok) return false;
            res.Headers["Access-Control-Allow-Origin"] = origin;
            res.Headers["Vary"] = "Origin";
            res.Headers["Access-Control-Allow-Credentials"] = "true";
        }
        else res.Headers["Access-Control-Allow-Origin"] = "*";
        res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        res.Headers["Access-Control-Allow-Headers"] = "content-type, x-ds-token, authorization";
        res.Headers["Access-Control-Max-Age"] = "600";
        // 被自己人（桌面端 / 超级连接）嵌进 iframe 是预期用法，别发拒绝嵌套的头。
        // 注意：这里刻意 **不设置** X-Frame-Options / CSP frame-ancestors。
        if (req.Has("Access-Control-Request-Private-Network", "true"))
            res.Headers["Access-Control-Allow-Private-Network"] = "true";
        return true;
    }
}

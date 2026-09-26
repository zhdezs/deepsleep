using TrollWrangler.Core;

namespace TrollWrangler.CoreHost;

/// <summary>令牌校验 + 跨域放行（只放行自家网页版和本机页面）。</summary>
internal static partial class Program
{
    private static string Version()
    {
        try { return typeof(Kernel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"; }
        catch { return "0.0.0"; }
    }

    private static bool TokenOk(Req req)
    {
        string t = req.Header("X-DS-Token");
        if (t.Length == 0) t = req.Q("token") ?? "";
        if (t.Length == 0) t = req.Q("t") ?? "";
        return t.Length > 0 && string.Equals(t, _token, StringComparison.Ordinal);
    }

    private static bool ApplyCors(Req req, Res res, string origin)
    {
        if (origin.Length > 0)
        {
            bool ok = AllowOrigins.Contains(origin) || origin == "null"
                      || origin.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)
                      || origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase);
            if (!ok) return false;
            res.Headers["Access-Control-Allow-Origin"] = origin;
            res.Headers["Vary"] = "Origin";
            res.Headers["Access-Control-Allow-Credentials"] = "true";
        }
        else res.Headers["Access-Control-Allow-Origin"] = "*";
        res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        res.Headers["Access-Control-Allow-Headers"] = "content-type, x-ds-token, authorization";
        res.Headers["Access-Control-Max-Age"] = "600";
        if (req.Has("Access-Control-Request-Private-Network", "true"))
            res.Headers["Access-Control-Allow-Private-Network"] = "true";
        return true;
    }
}

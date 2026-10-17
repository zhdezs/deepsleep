using System.Text;

namespace TrollWrangler;

/// <summary>
/// 应用内浏览器（OAuth 一键绑定用）：把授权页开在**应用自己的窗口里**，
/// 并在网络层拦下「跳回本机回调地址」的请求，把授权码直接交给 <see cref="OAuthFlow"/>。
/// <para>
/// 为什么要这样做：走系统浏览器也能拿到授权码（本地回环服务收得到），但用户会有两个割裂的窗口 ——
/// 而且一旦浏览器把 127.0.0.1 的回调页当成「其它网站」拦下来，就永远等不到结果。
/// 开在应用内，整个流程从点按钮到填好 Key 都在同一个窗口里完成，这才是真正的「一键」。
/// </para>
/// <para>
/// 平台无关：只认「redirect 前缀」，不管平台是 OpenRouter 还是别家。
/// 外壳（WinUI / Photino）实现这个接口去建原生 WebView；没实现时 <see cref="TryOpen"/>
/// 返回 false，调用方自动退回系统浏览器，功能不会丢（只是体验退一步）。
/// </para>
/// </summary>
public static class InternalBrowser
{
    /// <summary>同一条授权流程同一时刻只允许一个，防止用户连点开出多个窗口。</summary>
    private static readonly object Gate = new();
    private static bool _busy;

    /// <summary>外壳注册的「打开应用内窗口」实现。返回 false 表示外壳不支持 / 没建成。</summary>
    public static Func<string, string, bool>? Opener;

    /// <summary>诊断日志出口，外壳接上就能把授权流程的每一步写进 crash.log。</summary>
    public static Action<string>? Log;

    public static bool Enabled => Opener != null;

    public static bool TryOpen(string url, string redirectPrefix)
    {
        lock (Gate)
        {
            if (_busy) return false;
            _busy = true;
        }
        try
        {
            var opener = Opener;
            if (opener == null) { _busy = false; return false; }
            bool ok = opener(url, redirectPrefix);
            if (!ok) _busy = false;
            return ok;
        }
        catch
        {
            _busy = false;
            return false;
        }
    }

    /// <summary>窗口关闭（无论成功 / 取消 / 报错）都要调用，释放「忙碌」标记。</summary>
    public static void Release() { lock (Gate) _busy = false; }

    /// <summary>
    /// 判断要不要拦截这条导航。命中就把授权码交给回调（在 UI 线程调用），并返回 true 表示「已接管」。
    /// </summary>
    public static bool ShouldIntercept(string targetUrl, string redirectPrefix)
    {
        if (string.IsNullOrEmpty(targetUrl) || string.IsNullOrEmpty(redirectPrefix)) return false;
        string a = Strip(targetUrl), b = Strip(redirectPrefix);
        if (!a.StartsWith(b, StringComparison.OrdinalIgnoreCase)) return false;
        try { OAuthCallback.Handle(targetUrl); } catch { }
        return true;
    }

    private static string Strip(string u)
    {
        int h = u.IndexOf("://", StringComparison.Ordinal);
        return h >= 0 ? u[(h + 3)..] : u;
    }

    /// <summary>应用内窗口的初始化页。直接载入授权地址时窗口会白一下，先给个提示再跳。</summary>
    public const string LoadingPage =
        "data:text/html;charset=utf-8," + "%3C!doctype%20html%3E%3Cmeta%20charset%3D%22utf-8%22%3E" +
        "%3Cbody%20style%3D%22font-family%3Asystem-ui%2C%27Segoe%20UI%27%2Csans-serif%3Btext-align%3Acenter%3B" +
        "padding%3A80px%2024px%3Bcolor%3A%23222%22%3E%3Ch2%3E正在打开授权页%3C%2Fh2%3E" +
        "%3Cp%20style%3D%22color%3A%23666%22%3E登录并点「同意」后会自动回到%20deepsleep%2C%20无需手动复制。%3C%2Fp%3E" +
        "%3C%2Fbody%3E";

    /// <summary>给外壳日志用的简短描述。</summary>
    public static string Describe(string url, string redirectPrefix)
        => new StringBuilder().Append("open=").Append(url).Append(" redirect=").Append(redirectPrefix).ToString();
}

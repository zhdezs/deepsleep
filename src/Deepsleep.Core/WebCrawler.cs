using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler;

/// <summary>一条搜索结果。</summary>
public sealed class SearchHit
{
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Snippet { get; init; } = "";
    public string Engine { get; init; } = "";
}

/// <summary>
/// 内置爬虫式搜索：不依赖任何搜索 API / Key，直接抓取搜索引擎结果页并解析正文。
/// 多引擎依次尝试（Bing 国内 → 搜狗 → 360 → DuckDuckGo HTML），
/// 统一处理相对链接、跳转链接解包、去重与无意义标题过滤。
/// </summary>
public static class WebCrawler
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
        };
        var hc = new HttpClient(handler) { Timeout = timeout };
        hc.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        hc.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        hc.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        return hc;
    }

    /// <summary>依次尝试各引擎，返回第一个成功解析出结果（且已解跳转）的引擎名与结果。</summary>
    public static async Task<(string Engine, List<SearchHit> Hits)> SearchAsync(
        string query, CancellationToken ct, int limit = 8)
    {
        var tries = new (string Name, string Url, Func<string, List<SearchHit>> Parse)[]
        {
            ("Bing", "https://cn.bing.com/search?q=" + Uri.EscapeDataString(query), ParseBing),
            ("搜狗", "https://www.sogou.com/web?query=" + Uri.EscapeDataString(query), ParseSogou),
            ("360", "https://www.so.com/s?q=" + Uri.EscapeDataString(query), ParseSo360),
            ("DuckDuckGo", "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query), ParseDdg),
        };

        using var hc = NewClient(TimeSpan.FromSeconds(15));
        foreach (var (name, url, parse) in tries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string html = await GetStringAsync(hc, url, ct);
                var hits = Filter(parse(html), name);
                if (hits.Count == 0) continue;
                hits = await ResolveLinksAsync(hc, hits, limit, ct);
                if (hits.Count > 0)
                    return (name, hits.Take(limit).ToList());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 单引擎超时 → 换下一个
            }
            catch
            {
                // 单引擎失败 → 换下一个
            }
        }
        return ("", new List<SearchHit>());
    }

    /// <summary>抓取网页正文（去脚本/样式/标签，压缩空白），供「抓取网页」与搜索摘要复用。</summary>
    public static async Task<string> FetchTextAsync(string url, CancellationToken ct, int maxChars = 8000)
    {
        using var hc = NewClient(TimeSpan.FromSeconds(25));
        string html = await GetStringAsync(hc, url, ct);
        return Truncate(CleanHtml(html), maxChars);
    }

    public static string CleanHtml(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Regex.Replace(s, "(?is)<(script|style|noscript)[^>]*>.*?</\\1>", " ");
        s = Regex.Replace(s, "(?s)<[^>]+>", " ");
        return Regex.Replace(WebUtility.HtmlDecode(s), "\\s+", " ").Trim();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "\n…（已截断）";

    private static async Task<string> GetStringAsync(HttpClient hc, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri(url);
        using var resp = await hc.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        resp.EnsureSuccessStatusCode();
        byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        string? charset = resp.Content.Headers.ContentType?.CharSet;
        return Decode(bytes, charset);
    }

    /// <summary>按响应头 charset 解码；解码出乱码时退回 UTF-8。</summary>
    private static string Decode(byte[] bytes, string? charset)
    {
        string text;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                text = Encoding.GetEncoding(charset.Trim().Trim('"', '\'')).GetString(bytes);
                if (text.Count(ch => ch == '\uFFFD') < 8) return text;
            }
            catch { /* 未注册的编码（如 gb2312）→ 下面兜底 */ }
        }
        text = Encoding.UTF8.GetString(bytes);
        if (text.Count(ch => ch == '\uFFFD') > 8)
            text = text.Replace('\uFFFD', ' ');
        return text;
    }

    // ---------- 各引擎结果页解析 ----------

    private static List<SearchHit> ParseBing(string html)
        => ParsePairs(html,
            @"<h2[^>]*>\s*<a[^>]*href=""(?<u>https?://[^""]+)""[^>]*>(?<t>.*?)</a>",
            @"<p[^>]*class=""[^""]*b_lineclamp[^""]*""[^>]*>(?<s>.*?)</p>",
            "Bing");

    private static List<SearchHit> ParseSogou(string html)
        => ParsePairs(html,
            @"<h3[^>]*>\s*<a[^>]*href=""(?<u>[^""]+)""[^>]*>(?<t>.*?)</a>",
            @"<p[^>]*class=""[^""]*str_info[^""]*""[^>]*>(?<s>.*?)</p>",
            "搜狗");

    private static List<SearchHit> ParseSo360(string html)
        => ParsePairs(html,
            @"<h3[^>]*>\s*<a[^>]*href=""(?<u>[^""]+)""[^>]*>(?<t>.*?)</a>",
            @"<p[^>]*class=""[^""]*res-desc[^""]*""[^>]*>(?<s>.*?)</p>",
            "360");

    private static List<SearchHit> ParseDdg(string html)
        => ParsePairs(html,
            @"<a[^>]*class=""result__a""[^>]*href=""(?<u>[^""]+)""[^>]*>(?<t>.*?)</a>",
            @"<a[^>]*class=""result__snippet""[^>]*>(?<s>.*?)</a>",
            "DuckDuckGo");

    /// <summary>按「标题链接 + 摘要」两套正则解析结果页；摘要数量不足时留空。</summary>
    private static List<SearchHit> ParsePairs(string html, string linkPat, string snippetPat, string engine)
    {
        var hits = new List<SearchHit>();
        var links = Regex.Matches(html, linkPat, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var snips = Regex.Matches(html, snippetPat, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        for (int i = 0; i < links.Count; i++)
        {
            string title = CleanHtml(links[i].Groups["t"].Value);
            string url = WebUtility.HtmlDecode(links[i].Groups["u"].Value).Trim();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;
            string snippet = i < snips.Count ? CleanHtml(snips[i].Groups["s"].Value) : "";
            hits.Add(new SearchHit { Title = title, Url = url, Snippet = snippet, Engine = engine });
        }
        return hits;
    }

    /// <summary>补全相对链接、去重、去掉明显无意义的结果（导航/广告位）。</summary>
    private static List<SearchHit> Filter(List<SearchHit> hits, string engine)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<SearchHit>();
        foreach (var h in hits)
        {
            string url = h.Url;
            if (url.StartsWith("//")) url = "https:" + url;
            else if (url.StartsWith('/')) url = EngineOrigin(engine) + url;
            url = Regex.Replace(url, @"[?&](utm_[^&]*|from|fr|spm|src)=[^&]*", "");
            string title = h.Title.Trim();
            if (title.Length < 4) continue;
            if (title.Contains("百度安全验证") || title.Contains("点击继续访问")) continue;
            string key = title.Length >= 24 ? title[..24] : title;
            if (!seen.Add(key)) continue;
            list.Add(new SearchHit { Title = title, Url = url, Snippet = h.Snippet, Engine = engine });
            if (list.Count >= 16) break;
        }
        return list;
    }

    private static string EngineOrigin(string engine) => engine switch
    {
        "搜狗" => "https://www.sogou.com",
        "360" => "https://www.so.com",
        "Bing" => "https://cn.bing.com",
        _ => "https://duckduckgo.com",
    };

    /// <summary>搜索引擎的跳转链接（so.com/link、sogou /link、baidu /link?url=）解包成真实 URL。</summary>
    private static async Task<List<SearchHit>> ResolveLinksAsync(
        HttpClient hc, List<SearchHit> hits, int limit, CancellationToken ct)
    {
        var result = new List<SearchHit>();
        foreach (var h in hits.Take(limit))
        {
            if (!NeedsResolve(h.Url)) { result.Add(h); continue; }
            string real = await ResolveOneAsync(hc, h.Url, ct);
            result.Add(new SearchHit { Title = h.Title, Url = real, Snippet = h.Snippet, Engine = h.Engine });
        }
        return result;
    }

    private static bool NeedsResolve(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        string host = uri.Host.ToLowerInvariant();
        bool searchHost = host.EndsWith("so.com") || host.EndsWith("sogou.com") ||
                          host.EndsWith("baidu.com") || host.EndsWith("duckduckgo.com");
        return searchHost && (uri.AbsolutePath.Contains("link") || uri.Query.Contains("uddg=") ||
                              uri.Query.Contains("url="));
    }

    private static async Task<string> ResolveOneAsync(HttpClient hc, string url, CancellationToken ct)
    {
        try
        {
            // DuckDuckGo 的 uddg= 参数可直接解出真实地址，不必发请求
            var m = Regex.Match(url, @"[?&]uddg=(?<v>[^&]+)");
            if (m.Success)
            {
                string dec = Uri.UnescapeDataString(m.Groups["v"].Value);
                if (dec.StartsWith("http")) return dec;
            }
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri(new Uri(url).GetLeftPart(UriPartial.Authority) + "/");
            using var resp = await hc.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            string final = resp.RequestMessage?.RequestUri?.ToString() ?? url;
            if (final != url && !IsSearchHost(final)) return final;
            // 跳转页把目标藏在 meta refresh / JS location 里时的兜底
            string body = await resp.Content.ReadAsStringAsync(ct);
            var mm = Regex.Match(body,
                @"(?:URL=|location\.(?:replace|href)\s*=\s*['""])(?<v>https?://[^'""\s>]+)",
                RegexOptions.IgnoreCase);
            if (mm.Success) return WebUtility.HtmlDecode(mm.Groups["v"].Value);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch { }
        return url;
    }

    private static bool IsSearchHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        string host = uri.Host.ToLowerInvariant();
        return host.EndsWith("so.com") || host.EndsWith("sogou.com") ||
               host.EndsWith("baidu.com") || host.EndsWith("bing.com") ||
               host.EndsWith("duckduckgo.com");
    }
}

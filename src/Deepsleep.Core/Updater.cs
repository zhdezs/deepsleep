using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler;

/// <summary>OTA 更新清单。</summary>
public sealed class UpdateInfo
{
public string Version { get; set; } = "";
public string Url { get; set; } = "";
/// <summary>国内加速地址（Gitee 同名仓库的同一版本）；有就优先用它下载。</summary>
public string MirrorUrl { get; set; } = "";
/// <summary>Gitee 上把安装包切成了多片（单附件 100MB 上限）；有值就逐片下载再拼回整包。</summary>
public List<string> PartUrls { get; set; } = new();
/// <summary>每片的字节数，顺序与 PartUrls 一致（用于进度显示与完整性判断）。</summary>
public List<long> PartSizes { get; set; } = new();
public string Sha256 { get; set; } = "";
/// <summary>资产大小（字节），用于判断下载是否完整。</summary>
public long Size { get; set; }
public string Notes { get; set; } = "";
/// <summary>这个更新信息是从哪个源读到的：github / gitee / manifest。</summary>
public string Source { get; set; } = "";
}

/// <summary>
/// OTA 自动更新：检查清单 → 下载安装包（SHA256 校验）→ 退出后静默覆盖安装 → 自动重启。
///
/// 更新源支持四种写法（设置 → OTA 自动更新 → 更新源）：
///   1) GitHub 仓库：owner/repo 或 https://github.com/owner/repo
///      走官方 Releases API 读最新 tag + deepsleep-Setup.exe 资产；
///      仓库没有 Release 时自动退回仓库里的 update.json（main / master 都试）。
///   2) Gitee 仓库：gitee.com/owner/repo，也可以写 gitee:owner/repo —— 国内直连快
///   3) 清单直链：https://.../update.json（自建服务器、静态托管）
///   4) 本地 / 共享盘：D:\release\update.json（离线升级）
///
/// 填 GitHub 仓库时会顺带看同名 Gitee 仓库：同一个版本优先从 Gitee 下载（国内快一个数量级），
/// 校验始终用 GitHub 官方 SHA256。Gitee 单个附件不能超过 100MB，装不下的大安装包在那边切成
/// deepsleep-Setup.exe.part1 / .part2 / … 存放，客户端逐片下载后拼回整包再校验。
///
/// 依赖发布版的 deepsleep-Setup.exe（支持 --silent --dir 参数）。
/// </summary>
public static class Updater
{
public static string CurrentVersion { get; } = ReadCurrentVersion();

/// <summary>
/// GitHub 令牌（私有仓库 / 提高 API 限额）。只从两处读取：
///   ① 环境变量 DEEPSLEEP_GITHUB_TOKEN
///   ② data\github.token（Windows DPAPI 当前用户加密，只有本机本账号能解开）
/// 永远不会写进 config.json，也永远不会被日志或界面打印出来。
/// </summary>
public static string Token { get; private set; } = "";

/// <summary>加密令牌文件的固定位置（安装目录 data 下）。</summary>
public static string TokenPath => Path.Combine(AppContext.BaseDirectory, "data", "github.token");

public static bool HasToken => !string.IsNullOrWhiteSpace(Token);

/// <summary>读取令牌（环境变量优先，其次 DPAPI 文件）。读取失败一律当没配，不抛异常。</summary>
public static string LoadToken()
{
    try
    {
        string env = Environment.GetEnvironmentVariable("DEEPSLEEP_GITHUB_TOKEN") ?? "";
        if (!string.IsNullOrWhiteSpace(env)) { Token = env.Trim(); return Token; }
    }
    catch { /* 环境变量读不到就继续看文件 */ }

    Token = "";
    try
    {
        Token = TokenVault.Load(TokenPath).Trim();   // 多层加密（DPAPI + AES-GCM + 机器绑定 + ACL）
    }
    catch { Token = ""; }
    return Token;
}

/// <summary>HTTP 客户端带上令牌（有才带），GitHub API 强制要求 User-Agent。</summary>
private static void ApplyAuth(HttpClient hc)
{
    if (string.IsNullOrWhiteSpace(Token)) LoadToken();
    if (!string.IsNullOrWhiteSpace(Token))
        hc.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
}

// ------------------------------------------------------------------
// DPAPI（crypt32.dll）：不引用额外的 NuGet 包，直接用系统 API。
// 非 Windows 平台没有 DPAPI，一律返回空串（旧格式令牌解不开，能力不受影响）。
// ------------------------------------------------------------------
#if WINDOWS
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
private struct DataBlob
{
    public int cbData;
    public IntPtr pbData;
}

[System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true)]
private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr,
    ref DataBlob pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags,
    ref DataBlob pDataOut);

[System.Runtime.InteropServices.DllImport("kernel32.dll")]
private static extern IntPtr LocalFree(IntPtr hMem);

/// <summary>用 DPAPI（当前用户）解密；失败返回空串。</summary>
private static string DpapiUnprotect(byte[] blob)
{
    if (blob.Length == 0) return "";
    var inBlob = new DataBlob
    {
        cbData = blob.Length,
        pbData = System.Runtime.InteropServices.Marshal.AllocHGlobal(blob.Length),
    };
    var outBlob = new DataBlob();
    var entropy = new DataBlob();
    try
    {
        System.Runtime.InteropServices.Marshal.Copy(blob, 0, inBlob.pbData, blob.Length);
        if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 0, ref outBlob))
            return "";
        if (outBlob.cbData <= 0 || outBlob.pbData == IntPtr.Zero) return "";
        byte[] data = new byte[outBlob.cbData];
        System.Runtime.InteropServices.Marshal.Copy(outBlob.pbData, data, 0, outBlob.cbData);
        return Encoding.UTF8.GetString(data);
    }
    catch { return ""; }
    finally
    {
        if (inBlob.pbData != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(inBlob.pbData);
        if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
    }
}
#else
/// <summary>非 Windows 平台没有 DPAPI，一律返回空串。</summary>
private static string DpapiUnprotect(byte[] blob) => "";
#endif

/// <summary>
/// 当前版本号：**以安装目录里 deepsleep.exe 的文件版本为准**，程序集版本只当兜底。
/// 别再读内核程序集（Deepsleep.Core.dll）自己的版本 —— 它的 &lt;Version&gt; 不随发布同步，
/// 以前就是读它，结果升到 2.1.0 了还自报 2.0.0（外面看起来像"检测不到自己的版本号"）。
/// </summary>
private static string ReadCurrentVersion()
{
    try
    {
        foreach (var name in new[] { "deepsleep.exe", "deepsleep-core", "deepsleep" })
        {
            string exe = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(exe)) continue;
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            string fromExe = CleanVersion(vi.FileVersion ?? vi.ProductVersion);
            if (fromExe.Length > 0) return fromExe;
        }
    }
    catch { }

    try
    {
        var asm = typeof(Updater).Assembly;
        var attr = (System.Reflection.AssemblyInformationalVersionAttribute?)
            Attribute.GetCustomAttribute(asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
        string v = CleanVersion(attr?.InformationalVersion ?? asm.GetName().Version?.ToString());
        return v.Length > 0 ? v : "1.0.0";
    }
    catch { return "1.0.0"; }
}

/// <summary>去掉 +构建元数据、-预览后缀，以及尾部多余的 .0（2.1.0.0 → 2.1.0）。</summary>
private static string CleanVersion(string? v)
{
    if (string.IsNullOrWhiteSpace(v)) return "";
    v = v.Trim();
    int cut = v.IndexOfAny(new[] { '+', '-' });
    if (cut > 0) v = v[..cut];
    var parts = new List<string>(v.Split('.'));
    while (parts.Count > 3 && parts[^1] == "0") parts.RemoveAt(parts.Count - 1);
    return string.Join(".", parts).Trim();
}

/// <summary>统一的 HTTP 客户端：GitHub API 强制要求 User-Agent。</summary>
internal static HttpClient NewClient(TimeSpan timeout)
{
    var hc = new HttpClient { Timeout = timeout };
    hc.DefaultRequestHeaders.UserAgent.ParseAdd("deepsleep-updater/1.0");
    hc.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    hc.DefaultRequestHeaders.CacheControl =
        new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
    ApplyAuth(hc);   // 有令牌就带上：私有仓库 + 提高 API 限额
    return hc;
}

/// <summary>
/// 识别 "owner/repo" 形式的 GitHub 仓库地址。普通网址、本地路径都会返回 false，
/// 所以可以和 update.json 直链混用，不影响老配置。
/// </summary>
public static bool TryParseGitHub(string text, out string owner, out string repo)
{
    owner = ""; repo = "";
    if (string.IsNullOrWhiteSpace(text)) return false;
    string t = text.Trim();

    if (t.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
    {
        t = t["git@github.com:".Length..];
    }
    else if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri)) return false;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)) return false;
        t = uri.AbsolutePath.Trim('/');
    }
    else if (t.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
    {
        t = t["github.com/".Length..];
    }

    var parts = t.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length < 2) return false;
    owner = parts[0];
    repo = parts[1];
    if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
    if (owner.Contains(':') || repo.Contains(':') || owner.Contains(' ') || repo.Contains(' ')) return false;
    return owner.Length > 0 && repo.Length > 0;
}

/// <summary>读取更新源，返回比当前版本更新的信息；无更新或失败返回 null。</summary>
/// <param name="giteeFirst">Gitee 优先线路（⚙ 设置里的「更新线路」，默认 Gitee）：GitHub 更新源也会同时看同名 Gitee 仓库并从 Gitee 下载。</param>
public static async Task<UpdateInfo?> CheckAsync(string manifestUrl, bool giteeFirst = true,
                                                 CancellationToken ct = default)
{
    if (string.IsNullOrWhiteSpace(manifestUrl)) return null;
    manifestUrl = manifestUrl.Trim();

    // 1) 显式写 Gitee 仓库 → Gitee Releases API
    if (TryParseGitee(manifestUrl, out string gOwner, out string gRepo))
    {
        var only = await CheckGiteeAsync(gOwner, gRepo, ct).ConfigureAwait(false);
        return (only != null && IsNewer(only.Version, CurrentVersion)) ? only : null;
    }

    // 2) GitHub 仓库 → Releases API（顺便看 Gitee 同名仓库，能加速就加速）
    if (TryParseGitHub(manifestUrl, out string owner, out string repo))
    {
        // 「明明是 Gitee 却慢」的元凶：GitHub 的 api.github.com 是境外地址，国内要么连接被重置、
        // 要么一直挂到 20 秒超时，而它是串行挡在 Gitee 前面的。现在把 GitHub 那一请求丢后台，
        // 只等 Gitee；Gitee 上有同一个版本、又自带官方 SHA256 的话直接就用，一秒都不等 GitHub。
        var ghTask = CheckGitHubAsync(owner, repo, ct);
        UpdateInfo? gt = null;
        if (giteeFirst)
        {
            gt = await CheckGiteeAsync(owner, repo, ct).ConfigureAwait(false);
            if (gt != null && IsNewer(gt.Version, CurrentVersion) && !string.IsNullOrWhiteSpace(gt.Sha256))
                return gt;   // 快路径：整条链不依赖 GitHub API
        }
        var gh = await ghTask.ConfigureAwait(false);

        bool ghNewer = gh != null && IsNewer(gh.Version, CurrentVersion);
        bool gtNewer = gt != null && IsNewer(gt.Version, CurrentVersion);

        if (ghNewer)
        {
            // 同一个版本 → 优先用 Gitee 的地址下载（整包或分片），SHA256 仍用 GitHub 官方 digest。
            // 注意：Gitee 的 release JSON 里**没有 size**（只有 name + 下载地址），所以大小未知也得认，
            // 完整性最终由 GitHub 官方摘要（以及这边记录的 gh.Size）兜底。
            bool sameVersion = gt != null && gt.Version == gh!.Version;
            bool sizeOk = gt != null && (gt.Size == 0 || gh.Size == 0 || gt.Size == gh.Size);
            if (sameVersion && sizeOk)
            {
                if (gt!.PartUrls.Count > 0)
                {
                    gh.PartUrls = gt.PartUrls;
                    gh.PartSizes = gt.PartSizes;
                }
                else
                {
                    gh.MirrorUrl = gt.Url;
                }
            }
            return gh;
        }
        // GitHub 上还没有新版本、Gitee 上有（或 GitHub 挂了）→ 用 Gitee 的
        if (gtNewer) return gt;
        if (gh != null || gt != null) return null;   // 两边都读到了，但都不是新版

        // 3) 没有 Release 资产 → 退回仓库里的 update.json
        foreach (string branch in new[] { "main", "master" })
        {
            string raw = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/update.json";
            var info = await CheckManifestAsync(raw, ct).ConfigureAwait(false);
            if (info != null) return info;
        }
        return null;
    }

    // 4) 清单直链 / 本地路径
    return await CheckManifestAsync(manifestUrl, ct).ConfigureAwait(false);
}

/// <summary>
/// 识别 Gitee 仓库：gitee.com/owner/repo、https://gitee.com/owner/repo、git@gitee.com:owner/repo、
/// 也可以显式写 gitee:owner/repo（同名仓库不想和 GitHub 混用时用这个）。
/// </summary>
public static bool TryParseGitee(string text, out string owner, out string repo)
{
    owner = ""; repo = "";
    if (string.IsNullOrWhiteSpace(text)) return false;
    string t = text.Trim();

    if (t.StartsWith("gitee:", StringComparison.OrdinalIgnoreCase))
    {
        t = t["gitee:".Length..];
    }
    else if (t.StartsWith("git@gitee.com:", StringComparison.OrdinalIgnoreCase))
    {
        t = t["git@gitee.com:".Length..];
    }
    else if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri)) return false;
        if (!uri.Host.Equals("gitee.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("www.gitee.com", StringComparison.OrdinalIgnoreCase)) return false;
        t = uri.AbsolutePath.Trim('/');
    }
    else if (t.StartsWith("gitee.com/", StringComparison.OrdinalIgnoreCase))
    {
        t = t["gitee.com/".Length..];
    }
    else return false;

    var parts = t.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length < 2) return false;
    owner = parts[0];
    repo = parts[1];
    if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
    if (owner.Contains(':') || repo.Contains(':') || owner.Contains(' ') || repo.Contains(' ')) return false;
    return owner.Length > 0 && repo.Length > 0;
}

/// <summary>
/// 识别分片附件名：xxx.part1 / xxx.part01（片号从 1 开始）。Gitee 单个附件不能超过 100MB，
/// 所以大安装包在那边切成 deepsleep-Setup.exe.part1 / .part2 …，客户端下齐了再拼回整包。
/// </summary>
public static bool TryParsePartIndex(string name, out int index)
{
    index = 0;
    if (string.IsNullOrWhiteSpace(name)) return false;
    int dot = name.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);
    if (dot < 0) return false;
    string tail = name[(dot + 5)..].Trim();
    if (tail.Length == 0 || tail.Length > 4) return false;
    foreach (char c in tail) if (!char.IsDigit(c)) return false;
    return int.TryParse(tail, out index) && index > 0;
}

/// <summary>Gitee Releases：api/v5 的 latest，取 tag 当版本、body 当说明、exe 附件当安装包（没有整包就用分片）。</summary>
private static async Task<UpdateInfo?> CheckGiteeAsync(string owner, string repo, CancellationToken ct)
{
    try
    {
        using var hc = NewClient(TimeSpan.FromSeconds(20));
        hc.DefaultRequestHeaders.Accept.Clear();
        hc.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        string api = $"https://gitee.com/api/v5/repos/{owner}/{repo}/releases/latest";
        using var resp = await hc.GetAsync(api, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        string tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
        string notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
        string url = "";
        long size = 0;
        var partUrls = new List<string>();
        var partSizes = new List<long>();

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            JsonElement best = default;
            int bestScore = -1;
            var parts = new SortedDictionary<int, (string Url, long Size)>();
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string assetUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(assetUrl)) continue;
                long assetSize = a.TryGetProperty("size", out var sz0) && sz0.TryGetInt64(out long sz0v) ? sz0v : 0;

                // 分片（deepsleep-Setup.exe.part1/.part2…）：Gitee 单附件放不下整包时只能切开存
                if (TryParsePartIndex(name, out int partIndex))
                {
                    parts[partIndex] = (assetUrl, assetSize);
                    continue;
                }
                if (!IsInstallerAsset(name)) continue;
                int score = PlatformScore(name);
                if (score > bestScore) { bestScore = score; best = a; }
            }
            if (bestScore >= 0)
            {
                url = best.TryGetProperty("browser_download_url", out var u2) ? u2.GetString() ?? "" : "";
                if (best.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long szv)) size = szv;
            }
            // 没有整包 → 用分片（必须从 1 号片开始、一片不缺；缺片就当这个源没有可用的包）
            if (string.IsNullOrWhiteSpace(url) && parts.Count > 0)
            {
                bool complete = true;
                for (int i = 1; i <= parts.Count; i++)
                    if (!parts.TryGetValue(i, out var pi) || string.IsNullOrWhiteSpace(pi.Url)) { complete = false; break; }
                if (complete)
                {
                    long sum = 0;
                    for (int i = 1; i <= parts.Count; i++)
                    {
                        partUrls.Add(parts[i].Url);
                        partSizes.Add(parts[i].Size);
                        sum += parts[i].Size;
                    }
                    url = partUrls[0];
                    size = sum;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(url)) return null;

        // Gitee 那边没有 size/digest 字段，所以发布脚本会把安装包的 SHA256 写进 Release 说明里
        // （形如 "SHA256: 3d20…"）。有它就自己校验，这样走 Gitee 线路时整条链不依赖 GitHub API。
        string sha = "";
        var shaMatch = System.Text.RegularExpressions.Regex.Match(
            notes ?? "", @"(?i)sha256\s*[:：]\s*([0-9a-f]{64})");
        if (shaMatch.Success)
        {
            sha = shaMatch.Groups[1].Value.ToLowerInvariant();
            // 校验行不往界面上显示，更新说明只留人话
            notes = System.Text.RegularExpressions.Regex
                .Replace(notes ?? "", @"(?im)^\s*sha256\s*[:：].*$", "").Trim();
        }

        return new UpdateInfo
        {
            Version = NormalizeVersion(tag), Url = url, Notes = notes ?? "", Size = size, Source = "gitee",
            Sha256 = sha, PartUrls = partUrls, PartSizes = partSizes,
        };
    }
    catch { return null; }
}

/// <summary>GitHub Releases：取最新 tag 当版本、body 当更新说明、exe 资产当安装包。</summary>
private static async Task<UpdateInfo?> CheckGitHubAsync(string owner, string repo, CancellationToken ct)
{
    try
    {
        using var hc = NewClient(TimeSpan.FromSeconds(20));
        string api = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        using var resp = await hc.GetAsync(api, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;

        string tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
        string notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
        string url = "", sha = "";
        long size = 0;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            JsonElement best = default;
            int bestScore = -1;
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!IsInstallerAsset(name)) continue;
                int score = PlatformScore(name);
                if (score > bestScore) { bestScore = score; best = a; }
            }
            if (bestScore >= 0)
            {
                string browserUrl = best.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
// 私有仓库用 API 资产地址 + 令牌才能下载；公开仓库继续用浏览器直链
string apiAssetUrl = best.TryGetProperty("url", out var au) ? au.GetString() ?? "" : "";
url = (HasToken && !string.IsNullOrWhiteSpace(apiAssetUrl)) ? apiAssetUrl : browserUrl;
                    if (best.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long szv)) size = szv;
                if (best.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String)
                {
                    string d = dg.GetString() ?? "";
                    int colon = d.IndexOf(':');
                    sha = (colon >= 0 ? d[(colon + 1)..] : d).Trim();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(url)) return null;
        return new UpdateInfo { Version = NormalizeVersion(tag), Url = url, Sha256 = sha, Notes = notes, Size = size, Source = "github" };
    }
    catch { return null; }
}

/// <summary>读取 update.json（http(s) 直链或本地 / 共享盘路径）。</summary>
private static async Task<UpdateInfo?> CheckManifestAsync(string manifestUrl, CancellationToken ct)
{
    try
    {
        string json;
        if (manifestUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            using var hc = NewClient(TimeSpan.FromSeconds(20));
            json = await hc.GetStringAsync(manifestUrl, ct).ConfigureAwait(false);
        }
        else
        {
            string path = manifestUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                ? new Uri(manifestUrl).LocalPath
                : manifestUrl;
            if (!File.Exists(path)) return null;
            json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        var info = JsonSerializer.Deserialize<UpdateInfo>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (info == null || string.IsNullOrWhiteSpace(info.Version) || string.IsNullOrWhiteSpace(info.Url))
            return null;
        return IsNewer(info.Version, CurrentVersion) ? info : null;
    }
    catch { return null; }
}

/// <summary>
/// 把 Release 的 tag 规范化成版本号（去掉开头的 v）——
/// 界面上到处都会再补一个 "v"，不规范化就会显示成 "vv1.0.11"。
/// </summary>
public static string NormalizeVersion(string tag)
{
    string t = (tag ?? "").Trim();
    return t.Length > 1 && (t[0] == 'v' || t[0] == 'V') ? t[1..] : t;
}

/// <summary>
/// 版本比较（支持 1.0.0 / 1.0.0.0 / v1.0.0 / 3.0.3-alpha 形式）。
/// 数字段逐段比（缺的当 0，所以 3.0.3.1 &gt; 3.0.3）；数字全相同时，正式版排在预发布版前面
/// （3.0.3 &gt; 3.0.3-alpha）—— 不然按 .alpha 约定装的测试版永远收不到同名正式版的更新。
/// </summary>
public static bool IsNewer(string remote, string local)
{
    static (int[] Nums, string Pre) Parse(string s)
    {
        var nums = new List<int>();
        string pre = "";
        foreach (string part in (s ?? "").TrimStart('v', 'V').Split('.', '-', '+'))
        {
            string digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0 && part.Length > 0 && pre.Length == 0) pre = part;   // 3.0.3-alpha 里的 alpha
            nums.Add(int.TryParse(digits, out int n) ? n : 0);
        }
        return (nums.ToArray(), pre);
    }
    var (rn, rp) = Parse(remote);
    var (ln, lp) = Parse(local);
    int len = Math.Max(rn.Length, ln.Length);
    for (int i = 0; i < len; i++)
    {
        int r = i < rn.Length ? rn[i] : 0, l = i < ln.Length ? ln[i] : 0;
        if (r != l) return r > l;
    }
    if (rp.Length == 0 && lp.Length > 0) return true;    // 正式版 > 预发布版
    if (rp.Length > 0 && lp.Length == 0) return false;
    return string.CompareOrdinal(rp, lp) > 0;
}

/// <summary>本机平台的更新包文件名（Windows 是 Setup.exe，Unix 是 core 的 tar.gz）。</summary>
public static string InstallerFileName =>
    Platform.IsWindows ? "deepsleep-Setup.exe" : "deepsleep-core.tar.gz";

/// <summary>这个 Release 资产是不是本机平台可用的安装包。</summary>
private static bool IsInstallerAsset(string name)
{
    if (Platform.IsWindows) return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    if (!name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return false;
    string n = name.ToLowerInvariant();
    // 认本机平台 + 架构那一份：linux-x64 / linux-arm64 / osx-arm64 / osx-x64
    if (!n.Contains(Platform.Rid)) return false;
    return Platform.IsMacOS || n.Contains("linux");
}

/// <summary>给候选安装包打分（分越高越优先）。</summary>
private static int PlatformScore(string name)
{
    string n = name.ToLowerInvariant();
    int score = 0;
    if (n.Contains("deepsleep")) score += 2;
    if (Platform.IsWindows)
    {
        if (n.Contains("setup")) score += 4;
        if (n.Contains("install")) score += 1;
    }
    else
    {
        if (n.Contains("core")) score += 4;
    }
    return score;
}

public static string DownloadDir => Path.Combine(Path.GetTempPath(), "deepsleep-update");

/// <summary>
/// 这次更新的专属下载目录（版本号 + SHA256 前 8 位）。
/// 以前所有版本共用 %TEMP%\deepsleep-update\deepsleep-Setup.exe，断点续传会把上一个版本、
/// 或者上一个源剩下的半截"接着往下写"——拼出来的包 SHA256 永远对不上，于是每次都白下几百 MB、
/// 又得从头再来（这就是"明明是 Gitee 却越来越慢"的直接原因之一）。
/// </summary>
public static string DownloadDirFor(UpdateInfo info)
{
    var keep = new List<char>();
    foreach (char c in NormalizeVersion(info.Version))
        if (char.IsLetterOrDigit(c) || c == '.' || c == '-') keep.Add(c);
    string tag = new string(keep.ToArray());
    if (tag.Length == 0) tag = "unknown";
    string sha8 = "";
    if (!string.IsNullOrWhiteSpace(info.Sha256) && info.Sha256.Trim().Length >= 8)
        sha8 = new string(info.Sha256.Trim().ToLowerInvariant().Take(8).Where(char.IsLetterOrDigit).ToArray());
    return Path.Combine(DownloadDir, sha8.Length > 0 ? tag + "-" + sha8 : tag);
}

/// <summary>走 Gitee 时的最低速度兜底（KB/s）：跟探针实测速度的三分之一取大者。</summary>
private const double GiteeMinKbpsFloor = 60;
/// <summary>Gitee 下载中途"多久没数据"算卡住（秒），超了就换源。</summary>
private const int GiteeStallSeconds = 20;
/// <summary>GitHub 直连 / 加速镜像的卡住判定（跨境容易抽风，给宽一点）。</summary>
private const int HttpStallSeconds = 45;

/// <summary>下载更新包到临时目录，带进度回调；若清单提供 sha256 则校验。</summary>
/// <param name="onStatus">给界面看的文字状态（走的哪个源、有没有自动换源）。</param>
public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress,
                                               CancellationToken ct = default, Action<string>? onStatus = null)
{
    string destDir = DownloadDirFor(info);
    Directory.CreateDirectory(destDir);
    string dest = Path.Combine(destDir, InstallerFileName);
    CleanStaleDownloads(destDir);

    // ① Gitee 优先线路（默认，⚙ 设置里可切成 GitHub 线路）：只要 Gitee 上有同一个版本，
    //    就直接从 Gitee 下，不再跟 GitHub 探速比快慢（国内 Gitee 快一个数量级，探速纯属浪费时间）。
    //    只有 Gitee 连不上、报错、卡住，或长时间低于保底速度，才回退 GitHub 接着下
    //    —— 回退后照样按官方 SHA256 校验，镜像搬不了假货。
    string? giteeUrl = info.PartUrls.Count > 0
        ? info.PartUrls[0]
        : (IsGiteeUrl(info.MirrorUrl) ? info.MirrorUrl : null);
    bool giteeOnly = giteeUrl != null && info.Url.Equals(giteeUrl, StringComparison.OrdinalIgnoreCase);
    Exception? giteeError = null;
    if (giteeUrl != null)
    {
        // 不探速了，就没有"实测速度的三分之一"可用，只留保底门槛；卡住检测（GiteeStallSeconds）照旧
        const double minKbps = GiteeMinKbpsFloor;
        if (!giteeOnly) onStatus?.Invoke("正在从 Gitee 下载（Gitee 优先线路，不探速比快慢）…");
        try
        {
            // Gitee 单附件放不下整包时那边切成 part1/part2…：逐片下齐再拼回整包；
            // 片没下成、或者中途卡住/太慢，就退回下面的 GitHub 流程（照样按官方 SHA256 校验）
            if (info.PartUrls.Count > 0)
            {
                onStatus?.Invoke($"正在从 Gitee 下载分片（{info.PartUrls.Count} 片一起下，下齐后拼回整包）…");
                return await DownloadPartsAsync(info, dest, progress, ct, onStatus, minKbps).ConfigureAwait(false);
            }
            onStatus?.Invoke("正在从 Gitee 下载整包…");
            return await DownloadOnceAsync(info, info.MirrorUrl, dest, progress, ct, true,
                                           GiteeStallSeconds, minKbps, null, onStatus, "Gitee")
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            giteeError = ex;
            if (giteeOnly) throw;                 // 更新源只填了 Gitee：没有别的路可退
            CleanParts(dest);
            onStatus?.Invoke("Gitee 没下成，回退 GitHub：" + ex.Message);
        }
    }
    // 本地文件 / 共享盘：直接复制
    if (!info.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
    {
        string srcPath = info.Url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? new Uri(info.Url).LocalPath
            : info.Url;
        if (!File.Exists(srcPath)) throw new FileNotFoundException("找不到更新包：" + srcPath);
        long totalBytes = new FileInfo(srcPath).Length;
        using (var srcFile = File.OpenRead(srcPath))
        using (var dstFile = File.Create(dest))
        {
            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await srcFile.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dstFile.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                progress?.Report(Math.Min(100.0, read * 100.0 / totalBytes));
            }
        }
        VerifyHash(dest, info.Sha256);
        return dest;
    }

    // ② GitHub：直连 → 国内加速镜像，都支持断点续传；Gitee 上面已经试过，不再重复放进来
    Exception? lastError = null;
    int tries = 0;
    foreach (string candidate in BuildCandidates(info))
    {
        if (IsGiteeUrl(candidate)) continue;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            tries++;
            try
            {
                onStatus?.Invoke(tries == 1
                    ? "正在从 GitHub 下载更新包…"
                    : $"正在重试（{CandidateName(candidate)}，第 {tries} 次）…");
                return await DownloadOnceAsync(info, candidate, dest, progress, ct, true,
                                               HttpStallSeconds, 0, null, onStatus,
                                               tries == 1 ? "GitHub" : CandidateName(candidate))
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                if (ex is InvalidDataException)
                {
                    // 内容本身不对（下回来是 JSON、或者哈希对不上）：残件不能续传，删掉重来
                    try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                }
                onStatus?.Invoke("这一路没成，换下一个源重试：" + ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), ct).ConfigureAwait(false);
            }
        }
    }
    string giteeNote = giteeError == null ? "" : $"\nGitee 那边也没下成：{giteeError.Message}";
    throw new InvalidDataException(
        $"更新包下载失败（直连 + {Mirrors.Count} 个国内镜像共试了 {tries} 次）：{lastError?.Message}{giteeNote}", lastError);
}

/// <summary>是不是 Gitee 上的地址（国内源，境外可能很慢）。</summary>
private static bool IsGiteeUrl(string url) =>
    url.Contains("gitee.com/", StringComparison.OrdinalIgnoreCase);

/// <summary>给状态文案用的源名字。</summary>
private static string CandidateName(string url)
{
    try
    {
        string host = new Uri(url).Host;
        return host.Contains("github", StringComparison.OrdinalIgnoreCase) ? "GitHub 直连" : "国内加速镜像 " + host;
    }
    catch { return "候选地址"; }
}

/// <summary>
/// 清掉别的版本/别的源留下来的下载残件：这次不用它们了，留着白占几百 MB，
/// 更怕哪天被当成"半截包"接着续传，拼出一坨 SHA256 永远对不上的东西。
/// </summary>
private static void CleanStaleDownloads(string keepDir)
{
    try
    {
        if (!Directory.Exists(DownloadDir)) return;
        foreach (string d in Directory.GetDirectories(DownloadDir))
            if (!string.Equals(d.TrimEnd('\\'), keepDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(d, true); } catch { }
        // 老版本直接堆在根目录里的那种残件（没下完的 deepsleep-Setup.exe / .partN）
        foreach (string f in Directory.GetFiles(DownloadDir, "deepsleep-Setup*"))
            try { File.Delete(f); } catch { }
    }
    catch { }
}

/// <summary>清掉 Gitee 分片留下的临时文件（deepsleep-Setup.exe.part1、.part2…）。</summary>
private static void CleanParts(string dest)
{
    try
    {
        string dir = Path.GetDirectoryName(dest)!;
        foreach (string f in Directory.GetFiles(dir, Path.GetFileName(dest) + ".part*"))
            try { File.Delete(f); } catch { }
    }
    catch { }
}

/// <summary>带「卡住检测」的读取：超过 stallSeconds 一个字节都没来，就当这个源不行（抛 TimeoutException）。</summary>
private static async Task<int> ReadWithStallAsync(Stream src, byte[] buffer, CancellationToken userCt,
                                                 CancellationTokenSource stallCts, int stallSeconds)
{
    stallCts.CancelAfter(TimeSpan.FromSeconds(stallSeconds));
    try
    {
        return await src.ReadAsync(buffer, stallCts.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (!userCt.IsCancellationRequested)
    {
        throw new TimeoutException($"{stallSeconds} 秒没收到数据（连接像是卡住了）");
    }
}

/// <summary>
/// 国内可用的 GitHub 下载加速镜像（前缀式）。直连失败或中断时按顺序回退，
/// 内容与文件名都不变，最后照样用官方 SHA256 校验，不牺牲安全性。
/// </summary>
public static IReadOnlyList<string> Mirrors { get; } = new[]
{
    "https://ghproxy.net/",
    "https://gh-proxy.com/",
    "https://hub.gitmirror.com/",
};

/// <summary>候选下载地址：Gitee 镜像（国内直连最快）→ 原地址 → GitHub 加速镜像。</summary>
private static List<string> BuildCandidates(UpdateInfo info)
{
    var list = new List<string>();
    if (!string.IsNullOrWhiteSpace(info.MirrorUrl) &&
        !info.MirrorUrl.Equals(info.Url, StringComparison.OrdinalIgnoreCase))
        list.Add(info.MirrorUrl);
    string url = info.Url;
    list.Add(url);
    bool isReleaseDownload =
        url.Contains("github.com/", StringComparison.OrdinalIgnoreCase) &&
        url.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase);
    if (!isReleaseDownload) return list;   // api.github.com 资产地址 / 自建地址不支持镜像前缀
    foreach (string m in Mirrors)
    {
        if (url.StartsWith(m, StringComparison.OrdinalIgnoreCase)) continue;
        list.Add(m + url);
    }
    return list;
}

/// <summary>下载一次（支持断点续传）：网络中断留下的残件下次接着下，内容不对才算失败。</summary>
/// <param name="jsonProbe">开头像 JSON 就判失败（防止把 API 元数据当成安装包下回来）；分片用不上。</param>
/// <param name="stallSeconds">超过这么多秒没数据就判失败（0 = 不检测）。</param>
/// <param name="minKbps">下载满 15 秒后平均速度低于这个值就判失败换源（0 = 不设门槛）。</param>
private static async Task<string> DownloadOnceAsync(UpdateInfo info, string url, string dest,
                                                   IProgress<double>? progress, CancellationToken ct,
                                                   bool jsonProbe = true, int stallSeconds = 0, double minKbps = 0,
                                                   Action<DownloadTick>? onTick = null, Action<string>? onStatus = null,
                                                   string label = "")
{
    long have = File.Exists(dest) ? new FileInfo(dest).Length : 0;
    if (info.Size > 0 && have >= info.Size) have = 0;   // 已经下满却仍被判失败 → 从头重下

    using (var hc = NewClient(TimeSpan.FromMinutes(30)))
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (url.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            // API 资产必须显式要二进制，否则会返回 JSON 元数据（之前只 Clear 了 Accept，导致下回来 1441 字节的 JSON）
            req.Headers.Accept.Clear();
            req.Headers.Accept.ParseAdd("application/octet-stream");
        }
        if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);

        using var resp = await hc.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        bool append = have > 0 && (int)resp.StatusCode == 206;   // 206 = 服务器同意续传
        long start = append ? have : 0;
        long? remain = resp.Content.Headers.ContentLength;
        long goal = info.Size > 0 ? info.Size : start + (remain ?? 0);

        using (Stream src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        using (var dst = new FileStream(dest, append ? FileMode.Append : FileMode.Create,
                                        FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[262144];
            long read = start;
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var clock = Stopwatch.StartNew();
            long lastStatusMs = Environment.TickCount64;
            while (true)
            {
                int n = stallSeconds > 0
                    ? await ReadWithStallAsync(src, buffer, ct, stallCts, stallSeconds).ConfigureAwait(false)
                    : await src.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n <= 0) break;
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (goal > 0) progress?.Report(Math.Min(99.9, read * 100.0 / goal));
                if (onTick != null || (onStatus != null && label.Length > 0))
                {
                    var tick = new DownloadTick(read, start, goal, clock.Elapsed.TotalSeconds);
                    onTick?.Invoke(tick);
                    // 每秒刷一次"多快、还剩多久"——不然 0.8MB/s 下进度条看着像卡死了
                    if (onStatus != null && label.Length > 0 &&
                        Environment.TickCount64 - lastStatusMs >= 1000)
                    {
                        lastStatusMs = Environment.TickCount64;
                        onStatus(SpeedText(label, tick.Kbps, read, goal));
                    }
                }
                // 平均速度太低（境外连国内源就是这样）→ 早点判失败换源，别让人干等着
                if (minKbps > 0 && clock.Elapsed.TotalSeconds >= 15)
                {
                    double kbps = (read - start) / 1024.0 / clock.Elapsed.TotalSeconds;
                    if (kbps < minKbps)
                        throw new TimeoutException($"实际速度只有 {kbps:0} KB/s（低于 {minKbps:0} KB/s 门槛）");
                }
            }
        }
    }

    // 防御：如果下回来的还是 JSON（含 "release-assets" 之类元数据），直接判失败
    if (jsonProbe)
    {
        using var head = File.OpenRead(dest);
        var probe = new byte[2];
        if (head.Read(probe, 0, 2) == 2 && probe[0] == (byte)'{' && probe[1] == (byte)'"')
            throw new InvalidDataException("下载到的不是安装包（是 JSON 元数据）");
    }
    long actualSize = new FileInfo(dest).Length;
    if (info.Size > 0 && actualSize != info.Size)
        throw new InvalidDataException($"下载不完整：官方 {info.Size} 字节，实际 {actualSize} 字节");
    VerifyHash(dest, info.Sha256);
    progress?.Report(100);
    return dest;
}

/// <summary>
/// 下载 Gitee 上的安装包分片（deepsleep-Setup.exe.part1/N）再拼回一个完整安装包。
/// 每片单独重试、支持断点续传，拼完按官方 SHA256 校验 —— 所以和从 GitHub 下整包完全等价。
/// 多片**一起下**：单条连接吃不完整带宽，Gitee 那边两片并着下才跑得起来。
/// </summary>
private static async Task<string> DownloadPartsAsync(UpdateInfo info, string dest,
                                                     IProgress<double>? progress, CancellationToken ct,
                                                     Action<string>? onStatus = null, double minKbps = 0)
{
    int count = info.PartUrls.Count;
    long total = info.PartSizes.Sum();
    if (total <= 0) total = info.Size;

    var parts = new string[count];
    for (int i = 0; i < count; i++) parts[i] = dest + ".part" + (i + 1);

    // 并行下时每片只分到一部分带宽，单片的"太慢"门槛也要跟着降下来，别把还在跑的片误判成卡住
    double partMinKbps = count > 1 ? Math.Max(20, minKbps * 0.5) : minKbps;

    var partRead = new long[count];      // 每片已经下到多少字节
    var partKbps = new double[count];    // 每片的实时速度（KB/s）
    int finished = 0;                    // 下完几片（Gitee 不给附件大小时按片报进度）
    long lastStatusMs = 0;

    void Report(bool force = false)
    {
        long done = 0;
        for (int i = 0; i < count; i++) done += partRead[i];
        if (total > 0) progress?.Report(Math.Min(99.9, done * 100.0 / total));
        else progress?.Report(Math.Min(99.9, Volatile.Read(ref finished) * 100.0 / count));
        if (onStatus == null) return;
        long now = Environment.TickCount64;
        if (!force && now - lastStatusMs < 1000) return;
        lastStatusMs = now;
        double kbps = 0;
        for (int i = 0; i < count; i++) kbps += partKbps[i];
        onStatus(SpeedText(count > 1 ? $"Gitee 分片（{count} 片一起下）" : "Gitee 分片", kbps, done, total));
    }

    using var gate = new SemaphoreSlim(Math.Min(count, 3));   // 最多三路并发，别把源那边惹毛
    var errors = new Exception?[count];

    async Task DownloadOnePartAsync(int index)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long partSize = index < info.PartSizes.Count ? info.PartSizes[index] : 0;
            var partInfo = new UpdateInfo { Url = info.PartUrls[index], Size = partSize };
            Exception? lastError = null;

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await DownloadOnceAsync(partInfo, partInfo.Url, parts[index], null, ct, false,
                                            GiteeStallSeconds, partMinKbps,
                                            t =>
                                            {
                                                partRead[index] = t.Read;
                                                partKbps[index] = t.Kbps;
                                                Report();
                                            }).ConfigureAwait(false);
                    lastError = null;
                    partKbps[index] = 0;          // 这片下完了，速度不再计进合计
                    Interlocked.Increment(ref finished);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                    partKbps[index] = 0;
                    // 卡住 / 太慢 → 留着残片下次续传；内容不对才删掉重下
                    if (ex is not TimeoutException)
                    {
                        try { if (File.Exists(parts[index])) File.Delete(parts[index]); } catch { }
                        partRead[index] = 0;
                    }
                    onStatus?.Invoke($"Gitee 分片 {index + 1}/{count} 第 {attempt} 次没成：{ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), ct).ConfigureAwait(false);
                }
            }
            if (lastError != null) errors[index] = lastError;
            Report(true);
        }
        finally { gate.Release(); }
    }

    var tasks = new Task[count];
    for (int i = 0; i < count; i++)
    {
        int index = i;
        tasks[index] = DownloadOnePartAsync(index);
    }
    await Task.WhenAll(tasks).ConfigureAwait(false);

    for (int i = 0; i < count; i++)
        if (errors[i] != null)
            throw new InvalidDataException($"Gitee 分片 {i + 1}/{count} 下载失败：{errors[i]!.Message}", errors[i]);

    // 拼回整包（顺序必须和上传时一致，否则 SHA256 对不上）
    using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
    {
        foreach (string part in parts)
        {
            using var input = File.OpenRead(part);
            await input.CopyToAsync(output, 262144, ct).ConfigureAwait(false);
        }
    }
    foreach (string part in parts) { try { File.Delete(part); } catch { } }

    long actual = new FileInfo(dest).Length;
    if (info.Size > 0 && actual != info.Size)
        throw new InvalidDataException($"分片拼回来的安装包大小不对：官方 {info.Size} 字节，实际 {actual} 字节");
    VerifyHash(dest, info.Sha256);
    progress?.Report(100);
    return dest;
}

/// <summary>下载进度的一次采样：界面靠它算"现在多快、还剩多久"。</summary>
public readonly record struct DownloadTick(long Read, long Start, long Goal, double Seconds)
{
    public double Kbps => Seconds > 0.05 ? (Read - Start) / 1024.0 / Seconds : 0;
}

/// <summary>把速度 + 进度 + 预计剩余时间写成一行状态文案。</summary>
private static string SpeedText(string label, double kbps, long read, long goal)
{
    string speed = kbps >= 1024 ? (kbps / 1024.0).ToString("0.0") + " MB/s" : kbps.ToString("0") + " KB/s";
    var sb = new StringBuilder("正在从 ").Append(label).Append(" 下载… ").Append(speed);
    if (goal > 0)
    {
        sb.Append(" · 已下 ").Append(HumanSize(read)).Append(" / ").Append(HumanSize(goal));
        long left = goal - read;
        if (kbps > 5 && left > 0)
        {
            var t = TimeSpan.FromSeconds(left / 1024.0 / kbps);
            sb.Append(t.TotalMinutes >= 1
                ? $" · 剩余约 {(int)t.TotalMinutes} 分 {t.Seconds} 秒"
                : $" · 剩余约 {Math.Max(1, (int)Math.Round(t.TotalSeconds))} 秒");
        }
    }
    return sb.ToString();
}

/// <summary>字节数写成人看的大小。</summary>
private static string HumanSize(long bytes) => bytes >= 1048576
    ? (bytes / 1048576.0).ToString("0.0") + " MB"
    : Math.Max(0, bytes / 1024) + " KB";

/// <summary>SHA256 校验（清单未提供校验值时跳过）。</summary>
private static void VerifyHash(string file, string expectedHex)
{
    if (string.IsNullOrWhiteSpace(expectedHex)) return;
    using var fs = File.OpenRead(file);
    string actual = Convert.ToHexString(SHA256.HashData(fs));
    if (!actual.Equals(expectedHex.Trim(), StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException(
            $"更新包校验失败（SHA256 不匹配，已自动重下 {3} 次）：`n期望 {expectedHex.Trim().ToLowerInvariant()}`n实际 {actual.ToLowerInvariant()}`n文件大小 {new FileInfo(file).Length} 字节`n多半是下载被网络/代理截断，请重试，或到 Releases 页面手动下载安装包。");
}

/// <summary>
/// 应用更新：写一个临时批处理——等待当前程序退出 → 静默安装新版本到原目录 → 重新启动程序。
/// 调用后应立刻关闭当前程序。
/// </summary>
public static void ApplyAndRestart(string installerPath, string installDir, string exePath)
{
    BackupUserConfig(installDir);      // 升级前先把 config.json 备份一份
    Directory.CreateDirectory(DownloadDir);
    if (!Platform.IsWindows) { ApplyAndRestartUnix(installerPath, installDir, exePath); return; }
    string script = Path.Combine(DownloadDir, "apply-update.cmd");
    var sb = new StringBuilder();
    sb.AppendLine("@echo off");
    sb.AppendLine("chcp 65001 >nul");
    // 别用 timeout：脚本是无窗口跑的，timeout 会报"不支持输入重定向"直接返回，变成忙等；
    // ping 自己地址是唯一在所有 Windows 上都稳的"睡一会儿"
    sb.AppendLine("ping -n 3 127.0.0.1 >nul");
    sb.AppendLine("set /a _ds_wait=0");
    sb.AppendLine(":wait");
    sb.AppendLine("tasklist /fi \"IMAGENAME eq deepsleep.exe\" 2>nul | find /i \"deepsleep.exe\" >nul");
    sb.AppendLine("if errorlevel 1 goto install");
    // 最多等 30 秒：界面要是没能自己退（WebView2 异常、前端没发 quitApp），
    // 就强杀掉再来装，绝不能在这干等一整天（2.0.1 的坑）
    sb.AppendLine("set /a _ds_wait+=1");
    sb.AppendLine("if %_ds_wait% geq 20 goto kill");
    sb.AppendLine("ping -n 2 127.0.0.1 >nul");
    sb.AppendLine("goto wait");
    sb.AppendLine(":kill");
    sb.AppendLine("taskkill /f /im deepsleep.exe >nul 2>nul");
    sb.AppendLine("ping -n 3 127.0.0.1 >nul");
    sb.AppendLine(":install");
    sb.AppendLine("set /a _ds_try=0");
    sb.AppendLine(":try");
    sb.AppendLine("set /a _ds_try+=1");
    sb.AppendLine("start \"\" /wait \"" + installerPath + "\" --silent --dir \"" + installDir + "\" --no-desktop --no-launch");
    // 装失败（旧进程还占着 exe/dll，或 tasklist 被安全软件挡住误判）就再试一次，还不行就强杀重装 —— 反正不能停在这
    sb.AppendLine("if not errorlevel 1 goto ok");
    sb.AppendLine("if %_ds_try% geq 2 goto force");
    sb.AppendLine("ping -n 3 127.0.0.1 >nul");
    sb.AppendLine("goto try");
    sb.AppendLine(":force");
    sb.AppendLine("taskkill /f /im deepsleep.exe >nul 2>nul");
    sb.AppendLine("ping -n 4 127.0.0.1 >nul");
    sb.AppendLine("start \"\" /wait \"" + installerPath + "\" --silent --dir \"" + installDir + "\" --no-desktop --no-launch");
    sb.AppendLine(":ok");
    sb.AppendLine("start \"\" \"" + exePath + "\"");
    sb.AppendLine("del \"%~f0\"");
    File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));

    Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
    {
        UseShellExecute = true,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
    });
}

/// <summary>
/// Linux / macOS：写一个 bash 脚本——等本进程退出 → 解包 tar.gz 覆盖安装目录 → 重新启动。
/// </summary>
private static void ApplyAndRestartUnix(string installerPath, string installDir, string exePath)
{
    // Linux：这次更新落在用户级目录（系统装在 /opt 写不进去）时，把用户级启动项也指过去，
    // 否则菜单 / 终端里的 deepsleep 还是老的 /opt 版本，用户会以为"升级没生效"。
    if (Platform.IsLinux && string.Equals(installDir, Platform.LinuxAppsDir, StringComparison.Ordinal))
        WriteLinuxUserLaunchers(installDir);

    string script = Path.Combine(DownloadDir, "apply-update.sh");
    int pid = Environment.ProcessId;
    string dataDir = Platform.DefaultDataDir();
    string logPath = Path.Combine(dataDir, "update.log");
    // 只等"我们自己"这个 PID 退出。旧代码用 pgrep -f deepsleep —— 脚本路径 /tmp/deepsleep-update/apply-update.sh
    // 本身就含 "deepsleep"，pgrep 会匹配到自己，15 秒后 pkill -f deepsleep 把脚本自己（连进程）干掉，
    // 解包和重启都执行不到 —— Linux OTA "什么都没发生" 的元凶之一。
    var sb = new StringBuilder();
    sb.AppendLine("#!/usr/bin/env bash");
    sb.AppendLine("sleep 2");
    sb.AppendLine("_ds_pid=" + pid);
    sb.AppendLine("_ds_wait=0");
    sb.AppendLine("while kill -0 \"$_ds_pid\" >/dev/null 2>&1; do");
    sb.AppendLine("  _ds_wait=$((_ds_wait+1))");
    sb.AppendLine("  if [ $_ds_wait -ge 20 ]; then kill -9 \"$_ds_pid\" >/dev/null 2>&1; break; fi");
    sb.AppendLine("  sleep 1");
    sb.AppendLine("done");
    sb.AppendLine("mkdir -p '" + installDir + "' >/dev/null 2>&1");
    sb.AppendLine("mkdir -p '" + dataDir + "' >/dev/null 2>&1");
    sb.AppendLine("_ds_log='" + logPath + "'");
    sb.AppendLine("echo \"[$(date)] OTA: 解包到 " + installDir + "\" >> \"$_ds_log\" 2>&1");
    sb.AppendLine("tar -xzf '" + installerPath + "' -C '" + installDir + "' >> \"$_ds_log\" 2>&1");
    sb.AppendLine("chmod +x '" + exePath + "' >> \"$_ds_log\" 2>&1");
    sb.AppendLine("_ds_launch='" + exePath + "'");
    sb.AppendLine("if [ ! -x \"$_ds_launch\" ]; then _ds_launch='" + installDir + "/deepsleep.sh'; fi");
    sb.AppendLine("echo \"[$(date)] OTA: 启动 $_ds_launch\" >> \"$_ds_log\" 2>&1");
    sb.AppendLine("if [ -x \"$_ds_launch\" ]; then nohup \"$_ds_launch\" >/dev/null 2>&1 & fi");
    sb.AppendLine("rm -f -- \"$0\"");
    File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));
    try
    {
        File.SetUnixFileMode(script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    catch { /* 忽略 */ }

    Process.Start(new ProcessStartInfo("/bin/bash")
    {
        ArgumentList = { script },
        UseShellExecute = false,
        CreateNoWindow = true,
    });
}

/// <summary>
/// Linux：把用户级启动项（~/.local/share/applications/deepsleep.desktop、~/.local/bin/deepsleep）
/// 指向用户级自更新目录。系统安装在 /opt（root 所有）时 OTA 只能写用户目录，不重指启动项的话
/// 菜单里点开的仍是老版本。用户级 .desktop 在 XDG_DATA_DIRS 里排在 /usr/share 前面，
/// 同名会覆盖系统那份，全程不需要 root。
/// </summary>
private static void WriteLinuxUserLaunchers(string appsDir)
{
    try
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string launch = Path.Combine(appsDir, "deepsleep.sh");

        string apps = Path.Combine(home, ".local", "share", "applications");
        Directory.CreateDirectory(apps);
        var desktop = new StringBuilder();
        desktop.AppendLine("[Desktop Entry]");
        desktop.AppendLine("Type=Application");
        desktop.AppendLine("Name=deepsleep");
        desktop.AppendLine("Name[zh_CN]=deepsleep");
        desktop.AppendLine("GenericName=AI assistant");
        desktop.AppendLine("Comment=Cross-platform AI assistant that can edit files, run commands and search the web");
        desktop.AppendLine("Comment[zh_CN]=能读写文件、跑命令、搜网络、做深度研究的跨平台 AI 助手");
        desktop.AppendLine("Exec=" + launch + " %U");
        desktop.AppendLine("Icon=deepsleep");
        desktop.AppendLine("Terminal=false");
        desktop.AppendLine("Categories=Utility;Development;");
        desktop.AppendLine("Keywords=AI;assistant;agent;");
        desktop.AppendLine("StartupWMClass=deepsleep");
        File.WriteAllText(Path.Combine(apps, "deepsleep.desktop"), desktop.ToString(), new UTF8Encoding(false));

        // ~/.local/bin/deepsleep：终端里敲 deepsleep 也走新版本（Debian 默认 PATH 含 ~/.local/bin）
        string bin = Path.Combine(home, ".local", "bin");
        Directory.CreateDirectory(bin);
        string shim = "#!/bin/sh\n# deepsleep: 优先启动用户级自更新版本\nexec \"" + launch + "\" \"$@\"\n";
        string shimPath = Path.Combine(bin, "deepsleep");
        File.WriteAllText(shimPath, shim, new UTF8Encoding(false));
        try
        {
            File.SetUnixFileMode(shimPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch { }
    }
    catch { }
}

/// <summary>
/// 升级前把 data\config.json 备份成 data\config.json.bak。
/// 里面是 API Key 和各项设置，一旦被安装包模板覆盖、或者手抖删掉，靠它就能捞回来
/// （程序启动时发现配置不在会自动从 .bak 恢复）。
/// </summary>
public static string? BackupUserConfig(string installDir)
{
    try
    {
        string src = Path.Combine(installDir, "data", "config.json");
        if (!File.Exists(src)) return null;
        string bak = src + ".bak";
        File.Copy(src, bak, true);
        return bak;
    }
    catch { return null; }
}
}

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
// ------------------------------------------------------------------
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

private static string ReadCurrentVersion()
{
    try
    {
        var asm = typeof(Updater).Assembly;
        var attr = (System.Reflection.AssemblyInformationalVersionAttribute?)
            Attribute.GetCustomAttribute(asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
        string v = attr?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "1.0.0";
        int plus = v.IndexOf('+');
        if (plus > 0) v = v[..plus];
        int dash = v.IndexOf('-');
        return dash > 0 ? v[..dash] : v;
    }
    catch { return "1.0.0"; }
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
/// <param name="giteeMirror">GitHub 源时是否同时看同名 Gitee 仓库（国内下载快）。</param>
public static async Task<UpdateInfo?> CheckAsync(string manifestUrl, bool giteeMirror = true,
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
        var gh = await CheckGitHubAsync(owner, repo, ct).ConfigureAwait(false);
        var gt = giteeMirror ? await CheckGiteeAsync(owner, repo, ct).ConfigureAwait(false) : null;

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
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                int score = 0;
                if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase)) score += 4;
                if (name.Contains("deepsleep", StringComparison.OrdinalIgnoreCase)) score += 2;
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
        return new UpdateInfo
        {
            Version = tag, Url = url, Notes = notes, Size = size, Source = "gitee",
            PartUrls = partUrls, PartSizes = partSizes,
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
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                int score = 0;
                if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase)) score += 4;
                if (name.Contains("deepsleep", StringComparison.OrdinalIgnoreCase)) score += 2;
                if (name.Contains("install", StringComparison.OrdinalIgnoreCase)) score += 1;
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
        return new UpdateInfo { Version = tag, Url = url, Sha256 = sha, Notes = notes, Size = size, Source = "github" };
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

/// <summary>版本比较（支持 1.0.0 / 1.0.0.0 / v1.0.0 形式）。</summary>
public static bool IsNewer(string remote, string local)
{
    static int[] Parse(string s)
    {
        var nums = new List<int>();
        foreach (string part in s.TrimStart('v', 'V').Split('.', '-', '+'))
        {
            string digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            nums.Add(int.TryParse(digits, out int n) ? n : 0);
        }
        while (nums.Count < 3) nums.Add(0);
        return nums.ToArray();
    }
    int[] r = Parse(remote), l = Parse(local);
    for (int i = 0; i < 3; i++)
        if (r[i] != l[i]) return r[i] > l[i];
    return false;
}

public static string DownloadDir => Path.Combine(Path.GetTempPath(), "deepsleep-update");

/// <summary>下载更新包到临时目录，带进度回调；若清单提供 sha256 则校验。</summary>
public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress,
                                               CancellationToken ct = default)
{
    Directory.CreateDirectory(DownloadDir);
    string dest = Path.Combine(DownloadDir, "deepsleep-Setup.exe");

    // Gitee 那边只存得下 <100MB 的分片 → 先把各片下齐再拼回安装包；
    // 片没下成、或者 Gitee 挂了，就退回下面的整包流程（照样按官方 SHA256 校验）
    Exception? partsError = null;
    if (info.PartUrls.Count > 0)
    {
        bool giteeOnly = info.Url.Equals(info.PartUrls[0], StringComparison.OrdinalIgnoreCase);
        if (giteeOnly) return await DownloadPartsAsync(info, dest, progress, ct).ConfigureAwait(false);
        try
        {
            return await DownloadPartsAsync(info, dest, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            partsError = ex;
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

    // 跨境下载又慢又容易被截断：直连失败就自动换国内镜像，而且能从断点接着下
    Exception? lastError = null;
    int tries = 0;
    foreach (string candidate in BuildCandidates(info))
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            tries++;
            try
            {
                return await DownloadOnceAsync(info, candidate, dest, progress, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                if (ex is InvalidDataException)
                {
                    // 内容本身不对（下回来是 JSON、或者哈希对不上）：残件不能续传，删掉重来
                    try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                }
                await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), ct).ConfigureAwait(false);
            }
        }
    }
    string partsNote = partsError == null ? "" : $"\nGitee 分片包也没下成：{partsError.Message}";
    throw new InvalidDataException(
        $"更新包下载失败（直连 + {Mirrors.Count} 个国内镜像共试了 {tries} 次）：{lastError?.Message}{partsNote}", lastError);
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
private static async Task<string> DownloadOnceAsync(UpdateInfo info, string url, string dest,
                                                   IProgress<double>? progress, CancellationToken ct,
                                                   bool jsonProbe = true)
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
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (goal > 0) progress?.Report(Math.Min(99.9, read * 100.0 / goal));
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
/// </summary>
private static async Task<string> DownloadPartsAsync(UpdateInfo info, string dest,
                                                     IProgress<double>? progress, CancellationToken ct)
{
    int count = info.PartUrls.Count;
    long total = info.PartSizes.Sum();
    if (total <= 0) total = info.Size;

    var parts = new string[count];
    for (int i = 0; i < count; i++) parts[i] = dest + ".part" + (i + 1);

    long completed = 0;
    for (int i = 0; i < count; i++)
    {
        int index = i;
        long partSize = i < info.PartSizes.Count ? info.PartSizes[i] : 0;
        var partInfo = new UpdateInfo { Url = info.PartUrls[index], Size = partSize };
        Exception? lastError = null;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var partProgress = new Progress<double>(p =>
            {
                // Gitee 不给附件大小 → 按"第几片"均分进度，别让进度条一直停在 0
                double done = total > 0
                    ? (completed + partSize * p / 100.0) * 100.0 / total
                    : (index + p / 100.0) * 100.0 / count;
                progress?.Report(Math.Min(99.9, done));
            });
            try
            {
                await DownloadOnceAsync(partInfo, partInfo.Url, parts[index], partProgress, ct, false)
                    .ConfigureAwait(false);
                lastError = null;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                try { if (File.Exists(parts[index])) File.Delete(parts[index]); } catch { }
                await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), ct).ConfigureAwait(false);
            }
        }
        if (lastError != null)
            throw new InvalidDataException($"Gitee 分片 {index + 1}/{count} 下载失败：{lastError.Message}", lastError);

        completed += new FileInfo(parts[index]).Length;
        progress?.Report(Math.Min(99.9, total > 0
            ? completed * 100.0 / total
            : (index + 1) * 100.0 / count));
    }

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
    Directory.CreateDirectory(DownloadDir);
    string script = Path.Combine(DownloadDir, "apply-update.cmd");
    var sb = new StringBuilder();
    sb.AppendLine("@echo off");
    sb.AppendLine("chcp 65001 >nul");
    sb.AppendLine("timeout /t 2 /nobreak >nul");
    sb.AppendLine(":wait");
    sb.AppendLine("tasklist /fi \"IMAGENAME eq deepsleep.exe\" 2>nul | find /i \"deepsleep.exe\" >nul");
    sb.AppendLine("if not errorlevel 1 ( timeout /t 1 /nobreak >nul & goto wait )");
    sb.AppendLine("start \"\" /wait \"" + installerPath + "\" --silent --dir \"" + installDir + "\" --no-desktop --no-launch");
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
}

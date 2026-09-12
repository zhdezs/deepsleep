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
public string Sha256 { get; set; } = "";
public string Notes { get; set; } = "";
}

/// <summary>
/// OTA 自动更新：检查清单 → 下载安装包（SHA256 校验）→ 退出后静默覆盖安装 → 自动重启。
///
/// 更新源支持三种写法（设置 → OTA 自动更新 → 更新源）：
///   1) GitHub 仓库：owner/repo 或 https://github.com/owner/repo
///      走官方 Releases API 读最新 tag + deepsleep-Setup.exe 资产；
///      仓库没有 Release 时自动退回仓库里的 update.json（main / master 都试）。
///   2) 清单直链：https://.../update.json（自建服务器、静态托管）
///   3) 本地 / 共享盘：D:\release\update.json（离线升级）
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
public static async Task<UpdateInfo?> CheckAsync(string manifestUrl, CancellationToken ct = default)
{
    if (string.IsNullOrWhiteSpace(manifestUrl)) return null;
    manifestUrl = manifestUrl.Trim();

    // 1) GitHub 仓库 → Releases API
    if (TryParseGitHub(manifestUrl, out string owner, out string repo))
    {
        var gh = await CheckGitHubAsync(owner, repo, ct).ConfigureAwait(false);
        if (gh != null)
            return IsNewer(gh.Version, CurrentVersion) ? gh : null;

        // 2) 没有 Release 资产 → 退回仓库里的 update.json
        foreach (string branch in new[] { "main", "master" })
        {
            string raw = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/update.json";
            var info = await CheckManifestAsync(raw, ct).ConfigureAwait(false);
            if (info != null) return info;
        }
        return null;
    }

    // 3) 清单直链 / 本地路径
    return await CheckManifestAsync(manifestUrl, ct).ConfigureAwait(false);
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
                if (best.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String)
                {
                    string d = dg.GetString() ?? "";
                    int colon = d.IndexOf(':');
                    sha = (colon >= 0 ? d[(colon + 1)..] : d).Trim();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(url)) return null;
        return new UpdateInfo { Version = tag, Url = url, Sha256 = sha, Notes = notes };
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

    using (var hc = NewClient(TimeSpan.FromMinutes(30)))
    {
        if (info.Url.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
            hc.DefaultRequestHeaders.Accept.Clear();   // API 资产要 octet-stream
    using (var resp = await hc.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
    {
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        using Stream src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var dst = File.Create(dest);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total is > 0) progress?.Report(Math.Min(100.0, read * 100.0 / total.Value));
        }
    }
    VerifyHash(dest, info.Sha256);
    return dest;
    }
}

/// <summary>SHA256 校验（清单未提供校验值时跳过）。</summary>
private static void VerifyHash(string file, string expectedHex)
{
    if (string.IsNullOrWhiteSpace(expectedHex)) return;
    using var fs = File.OpenRead(file);
    string actual = Convert.ToHexString(SHA256.HashData(fs));
    if (!actual.Equals(expectedHex.Trim(), StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("更新包校验失败（SHA256 不匹配），已终止升级。");
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

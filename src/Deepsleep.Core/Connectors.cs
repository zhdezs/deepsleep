using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler;

/// <summary>一个连接器的状态（给设置页展示用，不含令牌本体）。</summary>
public sealed class ConnectorInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Account { get; set; } = "";
    public string At { get; set; } = "";
    public string Hint { get; set; } = "";
    public bool Connected => Account.Length > 0;
}

/// <summary>连接器存储：data\connectors.json，令牌一律经 （DPAPI + AES-GCM） 加密后再落盘，明文只在内存里存在几秒。</summary>
public static class ConnectorStore
{
    public static string PathOf(string dataDir) => Path.Combine(dataDir, "connectors.json");

    private static JsonObject Read(string dataDir)
    {
        try
        {
            string p = PathOf(dataDir);
            if (!File.Exists(p)) return new JsonObject();
            return JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8)) as JsonObject ?? new JsonObject();
        }
        catch { return new JsonObject(); }
    }

    private static void Write(string dataDir, JsonObject root)
    {
        try { File.WriteAllText(PathOf(dataDir), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false)); }
        catch { }
    }

    public static void Save(string dataDir, string id, string token, string account)
    {
        var root = Read(dataDir);
        root[id] = new JsonObject
        {
            ["token"] = TokenVault.Protect(token),
            ["account"] = account,
            ["at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
        };
        Write(dataDir, root);
    }

    public static void Remove(string dataDir, string id)
    {
        var root = Read(dataDir);
        root.Remove(id);
        Write(dataDir, root);
    }

    public static string Account(string dataDir, string id)
        => Read(dataDir)[id] is JsonObject o && o["account"] is JsonValue v && v.TryGetValue<string>(out var s) ? (s ?? "") : "";

    public static string At(string dataDir, string id)
        => Read(dataDir)[id] is JsonObject o && o["at"] is JsonValue v && v.TryGetValue<string>(out var s) ? (s ?? "") : "";

    /// <summary>解出令牌明文；没连过 / 解不开都返回空串。</summary>
    public static string Token(string dataDir, string id)
    {
        try
        {
            if (Read(dataDir)[id] is not JsonObject o) return "";
            string blob = o["token"] is JsonValue v && v.TryGetValue<string>(out var s) ? (s ?? "") : "";
            return blob.Length == 0 ? "" : TokenVault.Unprotect(blob);
        }
        catch { return ""; }
    }
}

/// <summary>GitHub 连接器：拿用户的 PAT（personal access token）代他调 API。</summary>
public sealed class GitHubConnector : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private const string Api = "https://api.github.com";

    public string Token { get; set; } = "";
    public bool Connected => !string.IsNullOrWhiteSpace(Token);
    public string LastError { get; private set; } = "";

    /// <summary>验一下令牌是谁的。返回 (是否成功, 说明, 登录名)。</summary>
    public async Task<(bool ok, string msg, string login)> ValidateAsync(CancellationToken ct = default)
    {
        if (!Connected) return (false, "没有填令牌", "");
        try
        {
            using var req = NewReq(HttpMethod.Get, "/user");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "令牌无效或已过期（401）"
                    : "GitHub 返回 HTTP " + (int)resp.StatusCode, "");
            using var doc = JsonDocument.Parse(body);
            string login = doc.RootElement.TryGetProperty("login", out var l) ? l.GetString() ?? "" : "";
            string name = doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? "" : "";
            int publics = doc.RootElement.TryGetProperty("public_repos", out var pr) && pr.TryGetInt32(out int pc) ? pc : 0;
            return (true, $"已连接 {login}" + (name.Length > 0 ? "（" + name + "）" : "") + $" · 公开仓库 {publics} 个", login);
        }
        catch (Exception ex) { return (false, ex.Message, ""); }
    }

    /// <summary>执行一次工具调用。action 见下面的 switch；args 是模型给的参数对象（JSON 文本）。</summary>
    public async Task<string> RunAsync(string action, string argsJson, CancellationToken ct = default)
    {
        if (!Connected) return "GitHub 连接器还没连：让用户去设置 →  连接器 点「用 GitHub 登录」，浏览器里授权一次就通了（不用手建令牌）。";
        JsonElement a = default;
        try { if (!string.IsNullOrWhiteSpace(argsJson)) using (var d = JsonDocument.Parse(argsJson)) a = d.RootElement.Clone(); }
        catch { /* 参数不是合法 JSON 就当空对象 */ }
        string S(string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) &&
                              v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
        try
        {
            switch ((action ?? "").Trim().ToLowerInvariant())
            {
                case "me" or "whoami" or "user" or "":
                    return await MeAsync(ct);

                case "repos" or "我的仓库" or "仓库列表":
                {
                    int n = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("数量", out var q) && q.TryGetInt32(out int qn) ? qn : 10;
                    n = Math.Clamp(n, 1, 30);
                    using var req = NewReq(HttpMethod.Get, $"/user/repos?sort=updated&per_page={n}");
                    return await ListReposAsync(req, ct);
                }

                case "issues" or "问题" or "issue列表":
                {
                    string repo = S("仓库");
                    if (repo.Length == 0) return "参数错误：需要「仓库」（owner/name）。";
                    string state = S("状态");
                    if (state.Length == 0) state = "open";
                    using var req = NewReq(HttpMethod.Get, $"/repos/{repo}/issues?state={Uri.EscapeDataString(state)}&per_page=20");
                    return await ListIssuesAsync(req, repo, ct);
                }

                case "issue" or "看issue":
                {
                    string repo = S("仓库");
                    string num = S("编号");
                    if (repo.Length == 0 || num.Length == 0) return "参数错误：需要「仓库」和「编号」。";
                    using var req = NewReq(HttpMethod.Get, $"/repos/{repo}/issues/{Uri.EscapeDataString(num)}");
                    return await OneIssueAsync(req, ct);
                }

                case "search" or "搜索":
                {
                    string q = S("查询");
                    if (q.Length == 0) return "参数错误：需要「查询」。";
                    string type = S("类型").ToLowerInvariant();
                    string path = type == "repos"
                        ? $"/search/repositories?q={Uri.EscapeDataString(q)}&per_page=10"
                        : $"/search/issues?q={Uri.EscapeDataString(q)}&per_page=10";
                    using var req = NewReq(HttpMethod.Get, path);
                    return await SearchAsync(req, type == "repos", ct);
                }

                case "create_issue" or "建issue" or "新建issue":
                {
                    string repo = S("仓库");
                    string title = S("标题");
                    if (repo.Length == 0 || title.Length == 0) return "参数错误：需要「仓库」和「标题」。";
                    string bodyText = S("内容");
                    using var req = NewReq(HttpMethod.Post, $"/repos/{repo}/issues");
                    req.Content = new StringContent(JsonSerializer.Serialize(new { title, body = bodyText }),
                        Encoding.UTF8, "application/json");
                    using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                    string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) return $"建 issue 失败：HTTP {(int)resp.StatusCode} {Short(body)}";
                    using var doc = JsonDocument.Parse(body);
                    string url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
                    int number = doc.RootElement.TryGetProperty("number", out var nb) && nb.TryGetInt32(out int nn) ? nn : 0;
                    return $"已在 {repo} 建好 issue #{number}：{title}\n{url}";
                }

                default:
                    return "不认识的 GitHub 操作：" + action +
                           "（可用：me / repos / issues / issue / search / create_issue）";
            }
        }
        catch (Exception ex) { LastError = ex.Message; return "GitHub 调用失败：" + ex.Message; }
    }

    private HttpRequestMessage NewReq(HttpMethod m, string path)
    {
        var req = new HttpRequestMessage(m, path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : Api + path);
        req.Headers.TryAddWithoutValidation("User-Agent", "deepsleep-connector");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token.Trim());
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return req;
    }

    private async Task<string> MeAsync(CancellationToken ct)
    {
        using var req = NewReq(HttpMethod.Get, "/user");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return $"取账号信息失败：HTTP {(int)resp.StatusCode} {Short(body)}";
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        string login = r.TryGetProperty("login", out var l) ? l.GetString() ?? "" : "";
        string name = r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
        int publics = r.TryGetProperty("public_repos", out var pr) && pr.TryGetInt32(out int pc) ? pc : 0;
        int followers = r.TryGetProperty("followers", out var fo) && fo.TryGetInt32(out int fc) ? fc : 0;
        string created = r.TryGetProperty("created_at", out var ca) ? Trunc(ca.GetString() ?? "", 10) : "";
        return $"GitHub 账号：{login}" + (name.Length > 0 ? $"（{name}）" : "") +
               $"\n公开仓库 {publics} 个 · 关注者 {followers} 个 · 注册于 {created}";
    }

    private static async Task<string> ListReposAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return $"取仓库列表失败：HTTP {(int)resp.StatusCode} {Short(body)}";
        using var doc = JsonDocument.Parse(body);
        var sb = new StringBuilder("最近更新的仓库：\n");
        int i = 0;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            i++;
            string full = r.TryGetProperty("full_name", out var f) ? f.GetString() ?? "" : "";
            string desc = r.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
            int stars = r.TryGetProperty("stargazers_count", out var s) && s.TryGetInt32(out int st) ? st : 0;
            string upd = r.TryGetProperty("updated_at", out var u) ? u.GetString() ?? "" : "";
            sb.Append($"{i}. {full}  ★{stars}  {Trunc(upd, 10)}").Append('\n');
            if (desc.Length > 0) sb.Append("   ").Append(Trunc(desc, 100)).Append('\n');
        }
        if (i == 0) sb.Append("（这个账号没有可见仓库）");
        return sb.ToString().TrimEnd();
    }

    private static async Task<string> ListIssuesAsync(HttpRequestMessage req, string repo, CancellationToken ct)
    {
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return $"取 issue 列表失败：HTTP {(int)resp.StatusCode} {Short(body)}";
        using var doc = JsonDocument.Parse(body);
        var sb = new StringBuilder(repo + "的 issue：\n");
        int i = 0;
        foreach (var it in doc.RootElement.EnumerateArray())
        {
            i++;
            int num = it.TryGetProperty("number", out var n) && n.TryGetInt32(out int nn) ? nn : 0;
            string title = it.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string state = it.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
            sb.Append($"#{num} [{state}] {Trunc(title, 90)}\n");
            if (i >= 20) break;
        }
        if (i == 0) sb.Append("（没有匹配的 issue）");
        return sb.ToString().TrimEnd();
    }

    private static async Task<string> OneIssueAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return $"取 issue 失败：HTTP {(int)resp.StatusCode} {Short(body)}";
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        string title = r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        string state = r.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
        string bodyText = r.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
        string url = r.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
        return $"标题：{title}\n状态：{state}\n链接：{url}\n\n{Trunc(bodyText, 1500)}";
    }

    private static async Task<string> SearchAsync(HttpRequestMessage req, bool repos, CancellationToken ct)
    {
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return $"搜索失败：HTTP {(int)resp.StatusCode} {Short(body)}";
        using var doc = JsonDocument.Parse(body);
        var sb = new StringBuilder();
        int total = doc.RootElement.TryGetProperty("total_count", out var tc) && tc.TryGetInt32(out int n) ? n : 0;
        sb.Append($"共 {total} 条结果：\n");
        if (doc.RootElement.TryGetProperty("items", out var items))
        {
            int i = 0;
            foreach (var it in items.EnumerateArray())
            {
                i++;
                if (repos)
                {
                    string full = it.TryGetProperty("full_name", out var f) ? f.GetString() ?? "" : "";
                    int stars = it.TryGetProperty("stargazers_count", out var s) && s.TryGetInt32(out int st) ? st : 0;
                    sb.Append($"{i}. {full}  ★{stars}\n");
                }
                else
                {
                    int num = it.TryGetProperty("number", out var nn) && nn.TryGetInt32(out int n2) ? n2 : 0;
                    string title = it.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    string url = it.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
                    sb.Append($"{i}. #{num} {Trunc(title, 80)}\n   {url}\n");
                }
                if (i >= 10) break;
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string Short(string s) => (s ?? "").Length <= 200 ? (s ?? "") : s[..200] + "…";
    private static string Trunc(string s, int n) => (s ?? "").Length <= n ? (s ?? "") : s[..n] + "…";

    public void Dispose() { }
}

/// <summary>连接器目录：新增一家在这里登记即可。</summary>
/// <remarks>
/// Kind 决定界面上怎么「连」：
///   · login —— 走对方的登录授权（GitHub 设备码），用户不用碰令牌；
///   · local —— 连本机程序（Blender），探测到 exe 并验一次版本就算连上；
///   · token —— 只能手填令牌的第三方。
/// </remarks>
public static class ConnectorCatalog
{
    public static readonly (string Id, string Name, string Hint, string Kind)[] All =
    {
        ("github", "GitHub", "点「用 GitHub 登录」，在浏览器里授权一次即可（不需要手建令牌）。想要更小权限也可以手动填 PAT。", "login"),
        ("blender", "Blender", "点「连接」自动找本机 Blender（Program Files / PATH），找不到再手填 blender.exe 路径。连上之后模型才有建模工具。", "local"),
    };
}

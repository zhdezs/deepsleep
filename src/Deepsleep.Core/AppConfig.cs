using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrollWrangler;

/// <summary>
/// 应用配置：持久化到 data/config.json。
/// 含 API 档位的 key / 地址 / 模型名，以及本地 Ollama 模型名。
/// </summary>
public sealed class AppConfig
{
    public string ApiKey { get; set; } = "";
    /// <summary>API 协议格式：openai / anthropic / gemini。</summary>
    public string ApiFormat { get; set; } = "openai";
    public string ApiUrl { get; set; } = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
    public string ApiModel { get; set; } = "glm-4-flash-250414";
    public bool ApiThinking { get; set; } = false;
    public string ReasoningEffort { get; set; } = "high";
    public string OllamaModel { get; set; } = "qwen2.5:1.5b";
    public string OllamaModelStrong { get; set; } = "qwen2.5:1.5b";
    public string OllamaUrl { get; set; } = "http://127.0.0.1:11434";
    public string ImageApiUrl { get; set; } = "https://open.bigmodel.cn/api/paas/v4/images/generations";
    public string ImageApiKey { get; set; } = "";
    public string ImageModel { get; set; } = "cogview-3-flash";
    public string VisionApiUrl { get; set; } = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
    public string VisionApiKey { get; set; } = "";
    public string VisionModel { get; set; } = "glm-4.6v-flash";
    public bool MultimodalMain { get; set; } = false;
    public string CounterApiUrl { get; set; } = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
    public string CounterApiKey { get; set; } = "";
    public string CounterModel { get; set; } = "glm-4-flash-250414";
    public bool UseOllama { get; set; } = false;
    public string Theme { get; set; } = "light";
    /// <summary>OTA 更新清单地址（返回 {"version":"1.0.1","url":"...","sha256":"...","notes":"..."}）。</summary>
    public string UpdateUrl { get; set; } = "";
    /// <summary>启动时自动检查更新。</summary>
    public bool AutoCheckUpdate { get; set; } = true;
    /// <summary>
    /// 更新线路：<c>"gitee"</c>（默认，国内快）或 <c>"github"</c>；客户端 ⚙ 设置里可随时切换。
    /// Gitee 线路下不探速，直接走 Gitee，只有 Gitee 连不上/下不动才回退 GitHub；校验始终用官方 SHA256。
    /// </summary>
    public string UpdateSource { get; set; } = "gitee";
    /// <summary>是否把 Gitee 当优先线路（UpdateSource != "github"）。只读派生值，不写进配置文件。</summary>
    [JsonIgnore]
    public bool GiteeFirst => !string.Equals(UpdateSource, "github", StringComparison.OrdinalIgnoreCase);
    /// <summary>是否显示桌面鲸鱼桌宠。</summary>
    public bool PetEnabled { get; set; } = true;
    /// <summary>联网搜索开关（🌐）。默认开启，设置里可关；会持久化到 config.json。</summary>
    public bool WebSearch { get; set; } = true;
    /// <summary>桌宠左上角坐标（拖到哪记到哪）；-1 表示用默认的右下角位置。</summary>
    /// <summary>WebView2 兼容模式：界面渲染进程崩溃过就记住，下次直接用兼容参数启动。</summary>
    public bool WebCompat { get; set; } = false;

    public int PetX { get; set; } = -1;
    public int PetY { get; set; } = -1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private string _path = "";

    public string ConfigPath => _path;

    public static AppConfig Load(string dir)
    {
        var cfg = new AppConfig();
        cfg._path = Path.Combine(dir, "config.json");
        try
        {
            // 万一配置被升级程序弄丢了（以前踩过：安装包里的空模板把用户的配置覆盖掉），
            // 用升级前自动留的 config.json.bak 恢复，别让人重填一次 API Key
            if (!File.Exists(cfg._path) && File.Exists(cfg._path + ".bak"))
            {
                try { File.Copy(cfg._path + ".bak", cfg._path, true); } catch { }
            }
            if (File.Exists(cfg._path))
            {
                string json = File.ReadAllText(cfg._path);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                // 兼容旧字段名 deepseek_api_key（历史版本）与新字段 ApiKey
                string? v;
                if ((v = GetStr(root, "apiKey", "ApiKey", "deepseek_api_key")) != null) cfg.ApiKey = v;
                if ((v = GetStr(root, "apiFormat", "ApiFormat")) != null) cfg.ApiFormat = v;
                if ((v = GetStr(root, "apiUrl", "ApiUrl")) != null) cfg.ApiUrl = v;
                if ((v = GetStr(root, "apiModel", "ApiModel")) != null) cfg.ApiModel = v;
                if ((root.TryGetProperty("apiThinking", out var at) || root.TryGetProperty("ApiThinking", out at)))
                    cfg.ApiThinking = at.ValueKind == JsonValueKind.True;
                if ((v = GetStr(root, "reasoningEffort", "ReasoningEffort")) != null) cfg.ReasoningEffort = v;
                if ((v = GetStr(root, "updateUrl", "UpdateUrl")) != null) cfg.UpdateUrl = v;
                if ((root.TryGetProperty("autoCheckUpdate", out var acu) || root.TryGetProperty("AutoCheckUpdate", out acu)))
                    cfg.AutoCheckUpdate = acu.ValueKind != JsonValueKind.False;
                if ((v = GetStr(root, "ollamaModel", "OllamaModel")) != null) cfg.OllamaModel = v;
                if ((v = GetStr(root, "ollamaModelStrong", "OllamaModelStrong")) != null) cfg.OllamaModelStrong = v;
                if ((v = GetStr(root, "ollamaUrl", "OllamaUrl")) != null) cfg.OllamaUrl = v;
                if ((v = GetStr(root, "imageApiUrl", "ImageApiUrl")) != null) cfg.ImageApiUrl = v;
                if ((v = GetStr(root, "imageApiKey", "ImageApiKey")) != null) cfg.ImageApiKey = v;
                if ((v = GetStr(root, "imageModel", "ImageModel")) != null) cfg.ImageModel = v;
                if ((v = GetStr(root, "visionApiUrl", "VisionApiUrl")) != null) cfg.VisionApiUrl = v;
                if ((v = GetStr(root, "visionApiKey", "VisionApiKey")) != null) cfg.VisionApiKey = v;
                if ((v = GetStr(root, "visionModel", "VisionModel")) != null) cfg.VisionModel = v;
                if ((root.TryGetProperty("multimodalMain", out var mm) || root.TryGetProperty("MultimodalMain", out mm)) &&
                    (mm.ValueKind == JsonValueKind.True || mm.ValueKind == JsonValueKind.False))
                    cfg.MultimodalMain = mm.GetBoolean();
                if ((v = GetStr(root, "counterApiUrl", "CounterApiUrl")) != null) cfg.CounterApiUrl = v;
                if ((v = GetStr(root, "counterApiKey", "CounterApiKey")) != null) cfg.CounterApiKey = v;
                if ((v = GetStr(root, "counterModel", "CounterModel")) != null) cfg.CounterModel = v;
                if (root.TryGetProperty("useOllama", out var uo) || root.TryGetProperty("UseOllama", out uo))
                    if (uo.ValueKind == JsonValueKind.True || uo.ValueKind == JsonValueKind.False)
                        cfg.UseOllama = uo.GetBoolean();
                if ((v = GetStr(root, "theme", "Theme")) != null) cfg.Theme = v;
                // 下面几项早先只有写、没有读：隐藏了桌宠 / 选了线路，重启就白设了
                if (root.TryGetProperty("petEnabled", out var pe) || root.TryGetProperty("PetEnabled", out pe))
                    if (pe.ValueKind == JsonValueKind.True || pe.ValueKind == JsonValueKind.False)
                        cfg.PetEnabled = pe.GetBoolean();
                if (root.TryGetProperty("webSearch", out var ws) || root.TryGetProperty("WebSearch", out ws))
                    if (ws.ValueKind == JsonValueKind.True || ws.ValueKind == JsonValueKind.False)
                        cfg.WebSearch = ws.GetBoolean();
                if ((v = GetStr(root, "updateSource", "UpdateSource")) != null)
                    cfg.UpdateSource = v.Trim().ToLowerInvariant() == "github" ? "github" : "gitee";
                // 兼容旧版本配置里的布尔开关：giteeMirror=true 就是 Gitee 线路，false 是 GitHub 线路
                else if (root.TryGetProperty("giteeMirror", out var gm) || root.TryGetProperty("GiteeMirror", out gm))
                    if (gm.ValueKind == JsonValueKind.True || gm.ValueKind == JsonValueKind.False)
                        cfg.UpdateSource = gm.GetBoolean() ? "gitee" : "github";
                if (root.TryGetProperty("petX", out var px) || root.TryGetProperty("PetX", out px))
                    if (px.ValueKind == JsonValueKind.Number && px.TryGetInt32(out int pxv)) cfg.PetX = pxv;
                if (root.TryGetProperty("petY", out var py) || root.TryGetProperty("PetY", out py))
                    if (py.ValueKind == JsonValueKind.Number && py.TryGetInt32(out int pyv)) cfg.PetY = pyv;
            }
        }
        catch
        {
            // 配置损坏时用默认值
        }
        return cfg;
    }

    /// <summary>按候选字段名查找字符串，找不到返回 null（保留调用方默认值）。</summary>
    private static string? GetStr(JsonElement root, params string[] names)
    {
        foreach (var n in names)
        {
            if (root.TryGetProperty(n, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
        }
        return null;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(_path, json);
        }
        catch
        {
            // 忽略写失败
        }
    }
}

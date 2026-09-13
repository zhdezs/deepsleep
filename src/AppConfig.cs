using System;
using System.IO;
using System.Text.Json;

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
    /// <summary>是否显示桌面鲸鱼桌宠。</summary>
    public bool PetEnabled { get; set; } = true;

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

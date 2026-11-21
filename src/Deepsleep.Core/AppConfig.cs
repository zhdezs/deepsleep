using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrollWrangler;

/// <summary>应用配置：持久化到 data/config.json。 含 API 档位的 key / 地址 / 模型名，以及本地 Ollama 模型名。</summary>
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
    /// <summary>视频生成：默认智谱 CogVideoX-Flash（异步任务：提交拿 id → 轮询 async-result）。</summary>
    public string VideoApiUrl { get; set; } = "https://open.bigmodel.cn/api/paas/v4/videos/generations";
    /// <summary>查询地址模板（可选，含 {id}）。留空按智谱协议推导。</summary>
    public string VideoPollUrl { get; set; } = "";
    public string VideoApiKey { get; set; } = "";
    public string VideoModel { get; set; } = "cogvideox-flash";
    /// <summary>没填自己的 Key 时，自动使用内置免费 Key（和网页版同一把，智谱免费模型矩阵）。</summary>
    public bool UseFreeKey { get; set; } = true;
    public bool MultimodalMain { get; set; } = false;
    public string CounterApiUrl { get; set; } = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
    public string CounterApiKey { get; set; } = "";
    public string CounterModel { get; set; } = "glm-4-flash-250414";
    public bool UseOllama { get; set; } = false;
    public string Theme { get; set; } = "light";
    /// <summary>OTA 更新源：owner/repo（GitHub / Gitee 同名仓库）或 update.json 直链。</summary>
    public string UpdateUrl { get; set; } = "zhdezs/deepsleep";
    /// <summary>启动时自动检查更新。</summary>
    public bool AutoCheckUpdate { get; set; } = true;
    /// <summary>更新线路："gitee"（默认，国内快）或 "github"；客户端设置里可随时切换。</summary>
    public string UpdateSource { get; set; } = "gitee";
    /// <summary>是否把 Gitee 当优先线路（UpdateSource != "github"）。只读派生值，不写进配置文件。</summary>
    [JsonIgnore]
    public bool GiteeFirst => !string.Equals(UpdateSource, "github", StringComparison.OrdinalIgnoreCase);
    /// <summary>是否显示桌面鲸鱼桌宠。</summary>
    public bool PetEnabled { get; set; } = true;
    /// <summary>联网搜索 / 深度研究开关（）。默认开启，设置里可关；会持久化到 config.json。</summary>
    public bool WebSearch { get; set; } = true;
    /// <summary>上下文窗口上限（token）。0 = 跟随模型（从模型名里的 32k/128k 等推断）。</summary>
    public int CtxWindowOverride { get; set; } = 0;

    /// <summary>Router 常驻层用的模型。默认跟主模型（ = true）； 勾掉之后可以单独给 Router 指一家：路由/调度用便宜的、Worker 用能打的，是很自然的搭配。</summary>
    public ModelBinding Router { get; set; } = new();
    /// <summary>Worker（拆分出来的子智能体）用的模型。默认跟主模型。</summary>
    public ModelBinding Worker { get; set; } = new();

    /// <summary>一套「接口地址 + Key + 模型名 + 协议」的绑定，可单独给 Router / Worker 用。</summary>
    public sealed class ModelBinding
    {
        /// <summary>true = 跟随主模型（用 ApiUrl/ApiKey/ApiModel/ApiFormat）。</summary>
        public bool UseMain { get; set; } = true;
        public string ApiUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string ApiModel { get; set; } = "";
        public string ApiFormat { get; set; } = "openai";

        [JsonIgnore]
        public bool Configured => !UseMain && ApiUrl.Trim().Length > 0 && ApiModel.Trim().Length > 0;

        public ModelBinding Clone() => new()
        {
            UseMain = UseMain, ApiUrl = ApiUrl, ApiKey = ApiKey, ApiModel = ApiModel, ApiFormat = ApiFormat,
        };
    }

    /// <summary>Blender 可执行文件路径（连接器连接时写回；留空则自动探测）。</summary>
    public string BlenderPath { get; set; } = "";

/// <summary>GitHub 设备码登录用的 client_id，留空用内置 App。</summary>
    public string GitHubClientId { get; set; } = "";

    /// <summary>SuperAgent：用户消息是否先交 Router 判定再决定直做还是拆 Worker（默认开）。</summary>
    public bool RouterAuto { get; set; } = true;

    /// <summary>SuperAgent Watchdog：Worker/Router 会话多久没动静算卡死（分钟，默认 10）。</summary>
    public int WatchdogMinutes { get; set; } = 10;

    /// <summary>桌宠左上角坐标（拖到哪记到哪）；-1 表示用默认的右下角位置。</summary>
    /// <summary>WebView2 兼容模式：界面渲染进程崩溃过就记住，下次直接用兼容参数启动。</summary>
    public bool WebCompat { get; set; } = false;

    public int PetX { get; set; } = -1;
    public int PetY { get; set; } = -1;

    /// <summary>按供应商保存的 API 配置档案：key = 该供应商的「API 地址」。 用来解决「换了供应商就得把地址/模型/Key 全部重填一遍，想换回去又忘了上次填的啥」。</summary>
    public Dictionary<string, ApiProfile> ApiProfiles { get; set; } = new();

    /// <summary>单个供应商的配置快照。</summary>
    public sealed class ApiProfile
    {
        public string ApiKey { get; set; } = "";
        public string ApiModel { get; set; } = "";
        public string ApiFormat { get; set; } = "openai";
        /// <summary>界面下拉里显示的供应商名（方便以后在列表里认出来）。</summary>
        public string Label { get; set; } = "";
        /// <summary>最后一次使用时间（ISO 8601），用于「最近使用」排序。</summary>
        public string Updated { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private string _path = "";

    public string ConfigPath => _path;

    /// <summary>拿一份「接口指向某绑定」的配置副本：Router / Worker 各自指定模型时用。</summary>
    public AppConfig WithBinding(ModelBinding? b)
    {
        if (b == null || !b.Configured) return this;
        var c = (AppConfig)MemberwiseClone();
        c.ApiUrl = b.ApiUrl.Trim();
        c.ApiModel = b.ApiModel.Trim();
        c.ApiFormat = string.IsNullOrWhiteSpace(b.ApiFormat) ? "openai" : b.ApiFormat.Trim();
        // Key 留空就沿用主 Key：同一个网关换了模型名，没必要再抄一遍 Key
        c.ApiKey = string.IsNullOrWhiteSpace(b.ApiKey) ? ApiKey : b.ApiKey.Trim();
        return c;
    }

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
                // 空值不覆盖默认更新源（老配置里可能是 "UpdateUrl": ""，会把默认值写没）
                if ((v = GetStr(root, "updateUrl", "UpdateUrl")) != null && v.Trim().Length > 0) cfg.UpdateUrl = v;
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
                if ((v = GetStr(root, "videoApiUrl", "VideoApiUrl")) != null) cfg.VideoApiUrl = v;
                if ((v = GetStr(root, "videoPollUrl", "VideoPollUrl")) != null) cfg.VideoPollUrl = v;
                if ((v = GetStr(root, "videoApiKey", "VideoApiKey")) != null) cfg.VideoApiKey = v;
                if ((v = GetStr(root, "videoModel", "VideoModel")) != null) cfg.VideoModel = v;
                if (root.TryGetProperty("useFreeKey", out var ufk) &&
                    (ufk.ValueKind == JsonValueKind.True || ufk.ValueKind == JsonValueKind.False))
                    cfg.UseFreeKey = ufk.GetBoolean();
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
                if ((v = GetStr(root, "blenderPath", "BlenderPath")) != null) cfg.BlenderPath = v;
                if ((v = GetStr(root, "githubClientId", "GitHubClientId")) != null) cfg.GitHubClientId = v;
                if (root.TryGetProperty("routerAuto", out var ra) || root.TryGetProperty("RouterAuto", out ra))
                    if (ra.ValueKind == JsonValueKind.True || ra.ValueKind == JsonValueKind.False) cfg.RouterAuto = ra.GetBoolean();
                if (root.TryGetProperty("watchdogMinutes", out var wd) || root.TryGetProperty("WatchdogMinutes", out wd))
                    if (wd.ValueKind == JsonValueKind.Number && wd.TryGetInt32(out int wdv) && wdv >= 0)
                        cfg.WatchdogMinutes = Math.Clamp(wdv, 0, 240);
                // 上下文上限以前只存不读：用户手填的值重启就没了，用量环又回到模型推断值。
                if (root.TryGetProperty("ctxWindowOverride", out var cwo) || root.TryGetProperty("CtxWindowOverride", out cwo))
                    if (cwo.ValueKind == JsonValueKind.Number && cwo.TryGetInt32(out int cwv)) cfg.CtxWindowOverride = cwv;
                if (root.TryGetProperty("petY", out var py) || root.TryGetProperty("PetY", out py))
                    if (py.ValueKind == JsonValueKind.Number && py.TryGetInt32(out int pyv)) cfg.PetY = pyv;
                // Router / Worker 的独立模型绑定（嵌套对象；旧配置没有这段就保持「跟随主模型」）
                if (root.TryGetProperty("router", out var rt) || root.TryGetProperty("Router", out rt))
                    cfg.Router = ReadBinding(rt) ?? cfg.Router;
                if (root.TryGetProperty("worker", out var wk) || root.TryGetProperty("Worker", out wk))
                    cfg.Worker = ReadBinding(wk) ?? cfg.Worker;
                // 供应商档案：以前只写不读 —— 存了一堆供应商，重启全没了。
                if (root.TryGetProperty("apiProfiles", out var aps) || root.TryGetProperty("ApiProfiles", out aps))
                {
                    if (aps.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var kv in aps.EnumerateObject())
                        {
                            if (kv.Value.ValueKind != JsonValueKind.Object) continue;
                            var one = kv.Value;
                            cfg.ApiProfiles[kv.Name] = new ApiProfile
                            {
                                ApiKey = GetStr(one, "apiKey", "ApiKey") ?? "",
                                ApiModel = GetStr(one, "apiModel", "ApiModel") ?? "",
                                ApiFormat = GetStr(one, "apiFormat", "ApiFormat") ?? "openai",
                                Label = GetStr(one, "label", "Label") ?? "",
                                Updated = GetStr(one, "updated", "Updated") ?? "",
                            };
                        }
                    }
                }
            }
        }
        catch
        {
            // 配置损坏时用默认值
        }
        return cfg;
    }

    /// <summary>读一个 {useMain, apiUrl, apiKey, apiModel, apiFormat} 绑定；类型不对就返回 null。</summary>
    public static ModelBinding? ReadBinding(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var b = new ModelBinding();
        if (el.TryGetProperty("useMain", out var um) || el.TryGetProperty("UseMain", out um))
            if (um.ValueKind == JsonValueKind.True || um.ValueKind == JsonValueKind.False) b.UseMain = um.GetBoolean();
        b.ApiUrl = GetStr(el, "apiUrl", "ApiUrl") ?? "";
        b.ApiKey = GetStr(el, "apiKey", "ApiKey") ?? "";
        b.ApiModel = GetStr(el, "apiModel", "ApiModel") ?? "";
        b.ApiFormat = GetStr(el, "apiFormat", "ApiFormat") ?? "openai";
        return b;
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TrollWrangler;

/// <summary>持久化的单条消息（不含纯界面字段，头像那类按标签页类型重建）。
/// ⚠️ 工具卡的 ToolName / ToolSummary / ToolDetail / ToolKind **必须在这里**：
/// 少了它们，重启后 RestoreConversation 恢复出的项没有工具身份，前端
/// <c>if (it.tool)</c> 判空 → 工具卡退化成一段裸文本全文（曾因此出过一个 bug）。</summary>
public sealed class ChatMessageDto
{
    public string Text { get; set; } = "";
    public string Meta { get; set; } = "";
    public bool IsSelf { get; set; }
    public bool IsSys { get; set; }
    public bool Accepted { get; set; }
    public string TimeStr { get; set; } = "";
    public bool ShowTime { get; set; }

    /// <summary>工具名（运行命令 / 写入文件 / 网络搜索…）。空 = 普通气泡。</summary>
    public string ToolName { get; set; } = "";
    /// <summary>折叠时那一句摘要。</summary>
    public string ToolSummary { get; set; } = "";
    /// <summary>展开后的明细（命令输出 / 文件内容 / 搜索结果）。</summary>
    public string ToolDetail { get; set; } = "";
    /// <summary>工具大类：cmd / file / search / research / vision / read / other。</summary>
    public string ToolKind { get; set; } = "";
    /// <summary>模型思考过程（reasoning），重开后仍然折叠在正文上方。</summary>
    public string Thought { get; set; } = "";
    /// <summary>附带的本地图片（生成图片 / 上传的图片）。</summary>
    public string? ImagePath { get; set; }
    public string AttachmentName { get; set; } = "";
    public string AttachmentSize { get; set; } = "";
    /// <summary>集群成员气泡显示的角色名。</summary>
    public string Speaker { get; set; } = "";
}

/// <summary>持久化的单个会话。</summary>
public sealed class ConversationDto
{
    public string Title { get; set; } = "";
    public int Sid { get; set; }
    public string Created { get; set; } = "";
    public bool AutoApprove { get; set; }
    public bool IsPinned { get; set; }
    public List<ChatMessageDto> Messages { get; set; } = new();
}

/// <summary>对话历史持久化：保存/加载到 data\conversations.json（版本化 JSON，零第三方依赖）。</summary>
public static class ConversationStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string PathFor(string dataDir) => Path.Combine(dataDir, "conversations.json");

    public static void Save(string dataDir,
        IReadOnlyList<ConversationDto> agent, IReadOnlyList<ConversationDto> counter,
        IReadOnlyList<ConversationDto> cluster)
    {
        try
        {
            var payload = new Dictionary<string, object>
            {
                ["version"] = 3,
                ["agent"] = agent,
                ["counter"] = counter,
                ["cluster"] = cluster,
            };
            File.WriteAllText(PathFor(dataDir), JsonSerializer.Serialize(payload, JsonOpts));
        }
        catch { /* 保存失败不影响运行 */ }
    }

    public static (List<ConversationDto> agent, List<ConversationDto> counter, List<ConversationDto> cluster) Load(string dataDir)
    {
        var agent = new List<ConversationDto>();
        var counter = new List<ConversationDto>();
        var cluster = new List<ConversationDto>();
        try
        {
            string path = PathFor(dataDir);
            if (!File.Exists(path)) return (agent, counter, cluster);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("agent", out var a) && a.ValueKind == JsonValueKind.Array)
                agent = a.Deserialize<List<ConversationDto>>() ?? new List<ConversationDto>();
            if (root.TryGetProperty("counter", out var c) && c.ValueKind == JsonValueKind.Array)
                counter = c.Deserialize<List<ConversationDto>>() ?? new List<ConversationDto>();
            if (root.TryGetProperty("cluster", out var cl) && cl.ValueKind == JsonValueKind.Array)
                cluster = cl.Deserialize<List<ConversationDto>>() ?? new List<ConversationDto>();
        }
        catch { /* 损坏则返回空列表，回退到默认新会话 */ }
        return (agent, counter, cluster);
    }
}

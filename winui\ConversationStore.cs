using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TrollWrangler;

/// <summary>持久化的单条消息（不含 UI 专属字段，恢复时按标签页类型重建）。</summary>
public sealed class ChatMessageDto
{
    public string Text { get; set; } = "";
    public string Meta { get; set; } = "";
    public bool IsSelf { get; set; }
    public bool IsSys { get; set; }
    public bool Accepted { get; set; }
    public string TimeStr { get; set; } = "";
    public bool ShowTime { get; set; }
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
                ["version"] = 2,
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

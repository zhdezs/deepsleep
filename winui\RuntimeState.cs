using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TrollWrangler;

/// <summary>Agent 会话消息快照（含角色，供跨重启恢复上下文）。</summary>
public sealed class AgentMsgDto
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string Meta { get; set; } = "";
    public string? ImagePath { get; set; }
}

public sealed class AgentSessionDto
{
    public int Sid { get; set; }
    public List<AgentMsgDto> Messages { get; set; } = new();
}

public sealed class ClusterWorkerDto
{
    public int Sid { get; set; }
    public string Name { get; set; } = "";
    public string Task { get; set; } = "";
    public string Status { get; set; } = "空闲";
    public string LogText { get; set; } = "";
}

public sealed class ClusterRunDto
{
    public int Sid { get; set; }
    public string UserText { get; set; } = "";
    public List<ClusterWorkerDto> Workers { get; set; } = new();
    public int SummarySid { get; set; }
}

/// <summary>运行时状态（断点恢复用）：Agent/集群会话与运行状态，持久化到 data\runtime_state.json。</summary>
public sealed class RuntimeState
{
    public List<AgentSessionDto> AgentSessions { get; set; } = new();
    public Dictionary<int, string> AgentStates { get; set; } = new();
    public List<ClusterRunDto> ClusterRuns { get; set; } = new();
    public Dictionary<int, string> ClusterStates { get; set; } = new();
}

/// <summary>运行时状态持久化：程序被直接关闭 / 电脑强制关机后，重启仍可恢复断点。</summary>
public static class RuntimeStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string PathFor(string dataDir) => Path.Combine(dataDir, "runtime_state.json");

    public static void Save(string dataDir, RuntimeState state)
    {
        try
        {
            File.WriteAllText(PathFor(dataDir), JsonSerializer.Serialize(state, JsonOpts));
        }
        catch { /* 保存失败不影响运行 */ }
    }

    public static RuntimeState? Load(string dataDir)
    {
        try
        {
            string path = PathFor(dataDir);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(path));
        }
        catch { return null; }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TrollWrangler;

/// <summary>集群成员持久化数据。</summary>
public sealed class ClusterMemberDto
{
    public string Name { get; set; } = "";
    public string Task { get; set; } = "";
    public string Status { get; set; } = "空闲";
    public string LogText { get; set; } = "";
    public int Sid { get; set; }
}

/// <summary>Agent 集群整体持久化状态。</summary>
public sealed class ClusterStateDto
{
    public string Summary { get; set; } = "";
    public List<ClusterMemberDto> Members { get; set; } = new();
}

/// <summary>集群状态持久化：保存/加载到 data\cluster.json。</summary>
public static class ClusterStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string PathFor(string dataDir) => Path.Combine(dataDir, "cluster.json");

    public static void Save(string dataDir, ClusterStateDto state)
    {
        try
        {
            File.WriteAllText(PathFor(dataDir), JsonSerializer.Serialize(state, JsonOpts));
        }
        catch { /* 保存失败不影响运行 */ }
    }

    public static ClusterStateDto? Load(string dataDir)
    {
        try
        {
            string path = PathFor(dataDir);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ClusterStateDto>(File.ReadAllText(path));
        }
        catch { return null; }
    }
}

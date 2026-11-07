using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TrollWrangler;

/// <summary>
/// Session 数据库（对应 SuperAgent 架构里的 [5] Session 数据库）。
/// <para>
/// 以前所有会话挤在 data/runtime_state.json 一个快照里：没有元数据、消息被截到 4000 字、
/// 也不能按状态查询，Router / Watchdog 根本没法用。现在每个 Session 一个独立 json 文件，
/// index.json 存可查询的元数据（kind / parent / state / 时间戳 / 摘要）。
/// </para>
/// <para>写入用「临时文件 + 原子替换」，避免强关时写坏一半的文件。</para>
/// </summary>
public sealed class SessionRecord
{
    public int Sid { get; set; }
    /// <summary>main = 主助手会话；worker = 子智能体隔离会话；router = 全局路由会话（第 2 期用）。</summary>
    public string Kind { get; set; } = "main";
    /// <summary>Worker 属于哪次 Router 任务 / 哪个父 Session。</summary>
    public int? ParentSid { get; set; }
    public string Title { get; set; } = "";
    /// <summary>active / idle / stopped / done。Watchdog 靠这个字段发现卡死的会话。</summary>
    public string State { get; set; } = "idle";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>共享上下文摘要：Router 只读这个，不碰 Worker 的私有历史。</summary>
    public string Summary { get; set; } = "";
    public List<AgentMessage> Messages { get; set; } = new();
    public Dictionary<string, string> Meta { get; set; } = new();
}

/// <summary>索引里的轻量条目，供列表 / 查询，不用把消息全文读出来。</summary>
public sealed class SessionIndexEntry
{
    public int Sid { get; set; }
    public string Kind { get; set; } = "main";
    public int? ParentSid { get; set; }
    public string Title { get; set; } = "";
    public string State { get; set; } = "idle";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public int MessageCount { get; set; }
    public string Summary { get; set; } = "";
}

public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _dir;
    private readonly object _gate = new();
    private List<SessionIndexEntry> _index = new();

    public SessionStore(string dataDir)
    {
        _dir = Path.Combine(dataDir, "sessions");
        Directory.CreateDirectory(_dir);
        _index = ReadIndex();
    }

    public string Dir => _dir;
    public string PathFor(int sid) => Path.Combine(_dir, sid + ".json");
    private string IndexPath => Path.Combine(_dir, "index.json");

    private static void AtomicWrite(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, true);
    }

    private List<SessionIndexEntry> ReadIndex()
    {
        try
        {
            if (!File.Exists(IndexPath)) return new List<SessionIndexEntry>();
            return JsonSerializer.Deserialize<List<SessionIndexEntry>>(File.ReadAllText(IndexPath), JsonOpts)
                   ?? new List<SessionIndexEntry>();
        }
        catch { return new List<SessionIndexEntry>(); }
    }

    private void SaveIndex()
    {
        try { AtomicWrite(IndexPath, JsonSerializer.Serialize(_index, JsonOpts)); }
        catch { /* 索引坏了下次会重建，不影响单个 session 文件 */ }
    }

    private static SessionIndexEntry ToEntry(SessionRecord r) => new()
    {
        Sid = r.Sid, Kind = r.Kind, ParentSid = r.ParentSid, Title = r.Title,
        State = r.State, CreatedUtc = r.CreatedUtc, UpdatedUtc = r.UpdatedUtc,
        MessageCount = r.Messages.Count, Summary = r.Summary,
    };

    /// <summary>写入/更新一个 Session；已有记录会保留 CreatedUtc、kind 等元数据。</summary>
    public void Upsert(SessionRecord rec)
    {
        lock (_gate)
        {
            var old = _index.FirstOrDefault(e => e.Sid == rec.Sid);
            if (old != null) rec.CreatedUtc = old.CreatedUtc;
            rec.UpdatedUtc = DateTimeOffset.UtcNow;
            try { AtomicWrite(PathFor(rec.Sid), JsonSerializer.Serialize(rec, JsonOpts)); }
            catch { return; }
            _index.RemoveAll(e => e.Sid == rec.Sid);
            _index.Add(ToEntry(rec));
            _index.Sort((a, b) => a.Sid.CompareTo(b.Sid));
            SaveIndex();
        }
    }

    /// <summary>批量把 Agent 内存里的会话快照落盘。</summary>
    public void SaveAll(IEnumerable<KeyValuePair<int, List<AgentMessage>>> snapshot,
                        Func<int, string>? kindOf = null)
    {
        foreach (var kv in snapshot)
        {
            var existing = Load(kv.Key);
            var messages = kv.Value.Where(m => m != null).ToList();
            string kind = kindOf?.Invoke(kv.Key) ?? existing?.Kind ?? "main";
            string title = existing?.Title ?? "";
            if (string.IsNullOrWhiteSpace(title))
            {
                var firstUser = messages.FirstOrDefault(m => m.Role == "user")?.Content ?? "";
                title = firstUser.Length > 40 ? firstUser.Substring(0, 40) + "…" : firstUser;
            }
            // ScheduleSave 会频繁触发；内容没变就别反复写盘、也别把 UpdatedUtc 刷成现在
            if (existing != null && existing.Kind == kind && existing.Title == title &&
                JsonSerializer.Serialize(existing.Messages, JsonOpts) == JsonSerializer.Serialize(messages, JsonOpts))
                continue;

            var rec = existing ?? new SessionRecord { Sid = kv.Key };
            rec.Kind = kind;
            rec.Title = title;
            rec.Messages = messages;
            Upsert(rec);
        }
    }

    public SessionRecord? Load(int sid)
    {
        try
        {
            string p = PathFor(sid);
            if (!File.Exists(p)) return null;
            return JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(p), JsonOpts);
        }
        catch { return null; }
    }

    /// <summary>要不要恢复哪些会话：只取有内容的，供 Agent.RestoreSession 用。</summary>
    public List<KeyValuePair<int, List<AgentMessage>>> RestoreAll()
    {
        var list = new List<KeyValuePair<int, List<AgentMessage>>>();
        foreach (var e in _index.ToList())
        {
            var r = Load(e.Sid);
            if (r?.Messages != null && r.Messages.Count > 0)
                list.Add(new KeyValuePair<int, List<AgentMessage>>(e.Sid, r.Messages));
        }
        return list;
    }

    /// <summary>按 kind / state 查询（Router 找 Worker、Watchdog 找卡死会话都用它）。</summary>
    public List<SessionIndexEntry> List(string? kind = null, string? state = null, int? parentSid = null)
        => _index.Where(e => (kind == null || e.Kind == kind) &&
                             (state == null || e.State == state) &&
                             (parentSid == null || e.ParentSid == parentSid))
                 .OrderByDescending(e => e.UpdatedUtc).ToList();

    public bool SetState(int sid, string state)
    {
        var r = Load(sid);
        if (r == null) return false;
        r.State = state;
        Upsert(r);
        return true;
    }

    public bool SetSummary(int sid, string summary)
    {
        var r = Load(sid);
        if (r == null) return false;
        r.Summary = summary;
        Upsert(r);
        return true;
    }

    public bool Delete(int sid)
    {
        lock (_gate)
        {
            try { if (File.Exists(PathFor(sid))) File.Delete(PathFor(sid)); } catch { return false; }
            _index.RemoveAll(e => e.Sid == sid);
            SaveIndex();
            return true;
        }
    }

    /// <summary>
    /// 从旧的 runtime_state.json 快照迁移一次。返回迁移的会话数。
    /// 已有 index.json 就说明迁过了，不会重复覆盖新数据。
    /// </summary>
    public int MigrateIfNeeded(RuntimeState? old)
    {
        if (old == null || old.AgentSessions.Count == 0) return 0;
        if (File.Exists(IndexPath)) return 0;
        int n = 0;
        foreach (var s in old.AgentSessions)
        {
            var rec = new SessionRecord
            {
                Sid = s.Sid,
                Kind = s.Sid >= 500000 ? "worker" : "main",
                State = old.AgentStates.ContainsKey(s.Sid) ? "stopped" : "idle",
                Messages = s.Messages.Select(m => new AgentMessage
                {
                    Role = m.Role, Content = m.Content, Meta = m.Meta,
                    ImagePath = m.ImagePath, VideoPath = m.VideoPath,
                }).Where(m => !string.IsNullOrWhiteSpace(m.Content)).ToList(),
            };
            var first = rec.Messages.FirstOrDefault(m => m.Role == "user")?.Content ?? "";
            rec.Title = first.Length > 40 ? first.Substring(0, 40) + "…" : first;
            Upsert(rec);
            n++;
        }
        return n;
    }
}

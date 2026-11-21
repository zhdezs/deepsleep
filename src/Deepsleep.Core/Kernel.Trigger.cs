using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler.Core;

/// <summary>Trigger：按间隔、每天固定时间或文件变化触发一条任务，统一交给 Router。</summary>
public sealed partial class Kernel
{
    /// <summary>一个触发器。字段平铺，方便直接进 JSON。</summary>
    public sealed class AgentTrigger
    {
        public string Id { get; set; } = "";
        /// <summary>interval / daily / file。</summary>
        public string Kind { get; set; } = "interval";
        /// <summary>到点后交给 Router 的那句话（它就是一条"用户消息"）。</summary>
        public string Prompt { get; set; } = "";
        /// <summary>interval：间隔分钟。</summary>
        public int EveryMinutes { get; set; } = 30;
        /// <summary>daily：HH:mm。</summary>
        public string At { get; set; } = "09:00";
        /// <summary>file：盯着哪个文件。</summary>
        public string Path { get; set; } = "";
        public bool Enabled { get; set; } = true;
        /// <summary>上次触发时间（yyyy-MM-dd HH:mm），防重复触发。</summary>
        public string LastFired { get; set; } = "";
        /// <summary>file 类型：上次看到的写入时间（ticks），用来判断"变了没有"。</summary>
        public long LastStamp { get; set; }
    }

    private readonly List<AgentTrigger> _triggers = new();
    private readonly object _triggerGate = new();
    private Timer? _triggerTimer;
    private int _triggerBusy;

    private string TriggersPath => System.IO.Path.Combine(_dataDir, "triggers.json");

    private void LoadTriggers()
    {
        lock (_triggerGate)
        {
            _triggers.Clear();
            try
            {
                if (!File.Exists(TriggersPath)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(TriggersPath, Encoding.UTF8));
                if (!doc.RootElement.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return;
                foreach (var el in arr.EnumerateArray())
                {
                    string prompt = Str(el, "prompt");
                    if (prompt.Length == 0) continue;
                    _triggers.Add(new AgentTrigger
                    {
                        Id = Str(el, "id") is { Length: > 0 } id ? id : "t" + Guid.NewGuid().ToString("N")[..6],
                        Kind = Str(el, "kind") is { Length: > 0 } k ? k : "interval",
                        Prompt = prompt,
                        EveryMinutes = el.TryGetProperty("everyMinutes", out var em) && em.TryGetInt32(out int emv) ? Math.Clamp(emv, 1, 1440) : 30,
                        At = Str(el, "at") is { Length: > 0 } at ? at : "09:00",
                        Path = Str(el, "path"),
                        Enabled = !(el.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False),
                        LastFired = Str(el, "lastFired"),
                        LastStamp = el.TryGetProperty("lastStamp", out var ls) && ls.TryGetInt64(out long lsv) ? lsv : 0,
                    });
                }
            }
            catch { /* 读坏了就当没有触发器，不影响启动 */ }
        }
    }

    private void SaveTriggers()
    {
        try
        {
            JsonArray arr;
            lock (_triggerGate)
            {
                arr = new JsonArray();
                foreach (var t in _triggers)
                    arr.Add(new JsonObject
                    {
                        ["id"] = t.Id, ["kind"] = t.Kind, ["prompt"] = t.Prompt,
                        ["everyMinutes"] = t.EveryMinutes, ["at"] = t.At, ["path"] = t.Path,
                        ["enabled"] = t.Enabled, ["lastFired"] = t.LastFired, ["lastStamp"] = t.LastStamp,
                    });
            }
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(TriggersPath,
                new JsonObject { ["items"] = arr }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
    }

    private List<object> TriggersJson()
    {
        lock (_triggerGate)
            return _triggers.Select(t => (object)new
            {
                id = t.Id, kind = t.Kind, prompt = t.Prompt, everyMinutes = t.EveryMinutes,
                at = t.At, path = t.Path, enabled = t.Enabled, lastFired = t.LastFired,
            }).ToList();
    }

    private void EmitTriggers() => Emit(new { ev = "triggers", list = TriggersJson() });

    private void TriggerAdd(JsonElement a)
    {
        string prompt = (GetStr(a, "prompt") ?? "").Trim();
        if (prompt.Length == 0) { Toast("先写一句「到点让助手做什么」", "error"); return; }
        string kind = (GetStr(a, "kind") ?? "interval").Trim().ToLowerInvariant();
        if (kind != "interval" && kind != "daily" && kind != "file") kind = "interval";
        string path = (GetStr(a, "path") ?? "").Trim();
        if (kind == "file" && path.Length == 0) { Toast("文件触发器需要填一个文件路径", "error"); return; }

        var t = new AgentTrigger
        {
            Id = "t" + Guid.NewGuid().ToString("N")[..6],
            Kind = kind,
            Prompt = prompt,
            EveryMinutes = GetInt(a, "everyMinutes", 30),
            At = GetStr(a, "at") is { Length: > 0 } at ? at : "09:00",
            Path = path,
            Enabled = true,
        };
        // file 触发器先记下当前时间：不然第一次检查就当成"刚变过"白跑一轮
        if (kind == "file")
        {
            try { t.LastStamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0; } catch { }
        }
        lock (_triggerGate) _triggers.Add(t);
        SaveTriggers();
        EmitTriggers();
        Toast("⏰ 已添加触发器：" + Describe(t));
    }

    private void TriggerRemove(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        int n;
        lock (_triggerGate) n = _triggers.RemoveAll(t => t.Id == id);
        if (n == 0) { Toast("没有这个触发器", "error"); return; }
        SaveTriggers();
        EmitTriggers();
        Toast("已删除触发器");
    }

    private void TriggerToggle(string id, bool on)
    {
        lock (_triggerGate)
        {
            var t = _triggers.FirstOrDefault(x => x.Id == id);
            if (t == null) return;
            t.Enabled = on;
        }
        SaveTriggers();
        EmitTriggers();
    }

    private static string Describe(AgentTrigger t) => t.Kind switch
    {
        "daily" => "每天 " + t.At + " → " + Trunc1(t.Prompt),
        "file" => "文件变动 → " + Trunc1(t.Prompt) + "（" + t.Path + "）",
        _ => "每 " + t.EveryMinutes + "分钟 → " + Trunc1(t.Prompt),
    };

    private static string Trunc1(string s) => (s ?? "").Length <= 40 ? (s ?? "") : s[..40] + "…";

    private void StartTriggerLoop()
    {
        StopTriggerLoop();
        bool any;
        lock (_triggerGate) any = _triggers.Count > 0;
        if (!any) return;
        _triggerTimer = new Timer(_ => TriggerTick(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    private void StopTriggerLoop()
    {
        try { _triggerTimer?.Dispose(); } catch { }
        _triggerTimer = null;
    }

    private void TriggerTick()
    {
        if (Interlocked.CompareExchange(ref _triggerBusy, 1, 0) != 0) return;
        try
        {
            var due = new List<AgentTrigger>();
            var now = DateTime.Now;
            string stamp = now.ToString("yyyy-MM-dd HH:mm");

            lock (_triggerGate)
            {
                foreach (var t in _triggers)
                {
                    if (!t.Enabled) continue;
                    switch (t.Kind)
                    {
                        case "daily":
                            if (string.Equals(t.At.Trim(), now.ToString("HH:mm"), StringComparison.Ordinal) &&
                                !t.LastFired.StartsWith(now.ToString("yyyy-MM-dd"), StringComparison.Ordinal))
                            { t.LastFired = stamp; due.Add(t); }
                            break;

                        case "file":
                        {
                            long cur = 0;
                            try { if (t.Path.Length > 0 && File.Exists(t.Path)) cur = File.GetLastWriteTimeUtc(t.Path).Ticks; } catch { }
                            if (cur > 0 && cur != t.LastStamp)
                            {
                                t.LastStamp = cur;
                                t.LastFired = stamp;
                                due.Add(t);
                            }
                            break;
                        }

                        default: // interval
                        {
                            DateTime last;
                            bool ok = DateTime.TryParse(t.LastFired, out last);
                            if (!ok || (now - last).TotalMinutes >= Math.Max(1, t.EveryMinutes))
                            {
                                t.LastFired = stamp;
                                due.Add(t);
                            }
                            break;
                        }
                    }
                }
            }

            if (due.Count == 0) return;
            SaveTriggers();
            EmitTriggers();

            foreach (var t in due) _ = RunTriggerAsync(t);
        }
        catch { /* 触发器出问题不能把主循环带崩 */ }
        finally { Interlocked.Exchange(ref _triggerBusy, 0); }
    }

    /// <summary>触发一次：消息进的是同一个 Router，跟用户手打没有区别。</summary>
    private async Task RunTriggerAsync(AgentTrigger t)
    {
        try
        {
            int sid = _agentCur?.Sid ?? 0;
            if (sid == 0) return;
            // 主助手正在跑就跳过这一轮（不排队堆积：触发器是"有机会就做"，不是必须做）
            if (GetAgentState(sid) == RunState.Running)
            {
                Toast("⏰ 触发器跳过这一轮：助手正在忙（" + Trunc1(t.Prompt) + "）", "info");
                return;
            }
            AddAgentSys(sid, "⏰ 触发器（" + Describe(t) + "）触发了这次任务。");
            Emit(new { ev = "triggerFired", id = t.Id, kind = t.Kind, prompt = Trunc1(t.Prompt) });
            await RouteAndRunAsync(sid, t.Prompt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Toast("⏰ 触发器执行失败：" + ex.Message, "error");
        }
    }

    private static string Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
}

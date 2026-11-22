using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TrollWrangler.Core;

/// <summary>Router / Worker 两层：Router 常驻（就是主会话本身，一直活着、一直记得上下文）， Worker 是一份「可增可减的名单」，按角色匹配复用同一个会话，不再每拆一次就换一批新 id。</summary>
public sealed partial class Kernel
{
    /// <summary>Worker 名单上限。再多也没意义，同时跑的上限依旧是 4。</summary>
    private const int MaxWorkerSeats = 24;

    private readonly object _seatGate = new();
    private List<WorkerSeat> _seats = new();
    private bool _seatsLoaded;

    /// <summary>名单里的一个 Worker 席位（角色 = 身份，Sid = 它专属的会话）。</summary>
    public sealed class WorkerSeat
    {
        public string Id { get; set; } = "";
        /// <summary>角色名（前端/模型给的 role）。同名即同一个人，复用会话。</summary>
        public string Role { get; set; } = "";
        /// <summary>备注：擅长什么，给 Router 做挑选依据。</summary>
        public string Note { get; set; } = "";
        /// <summary>该 Worker 专属会话 id；0 表示还没跑过。</summary>
        public int Sid { get; set; }
        /// <summary>它上次实际使用的模型名（设置里可单独给 Worker 指模型）。</summary>
        public string Model { get; set; } = "";
        /// <summary>跑过几轮。</summary>
        public int Runs { get; set; }
        public string LastUsed { get; set; } = "";
    }

    private string SeatsPath => Path.Combine(_dataDir, "workers.json");

    private void LoadSeats()
    {
        lock (_seatGate)
        {
            if (_seatsLoaded) return;
            _seatsLoaded = true;
            try
            {
                if (!File.Exists(SeatsPath)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(SeatsPath, Encoding.UTF8));
                if (!doc.RootElement.TryGetProperty("seats", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return;
                foreach (var el in arr.EnumerateArray())
                {
                    string role = el.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                        ? (r.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(role)) continue;
                    _seats.Add(new WorkerSeat
                    {
                        Id = el.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String
                            ? (i.GetString() ?? "") : NewSeatId(),
                        Role = role,
                        Note = el.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String
                            ? (n.GetString() ?? "") : "",
                        Sid = el.TryGetProperty("sid", out var s) && s.TryGetInt32(out int sv) ? sv : 0,
                        Model = el.TryGetProperty("model", out var md) && md.ValueKind == JsonValueKind.String
                            ? (md.GetString() ?? "") : "",
                        Runs = el.TryGetProperty("runs", out var ru) && ru.TryGetInt32(out int rv) ? rv : 0,
                        LastUsed = el.TryGetProperty("lastUsed", out var lu) && lu.ValueKind == JsonValueKind.String
                            ? (lu.GetString() ?? "") : "",
                    });
                }
            }
            catch { /* 名单读不出来就当空的，不影响干活 */ }
        }
    }

    private void SaveSeats()
    {
        try
        {
            List<WorkerSeat> snap;
            lock (_seatGate) snap = _seats.ToList();
            var arr = new JsonArray();
            foreach (var s in snap)
                arr.Add(new JsonObject
                {
                    ["id"] = s.Id,
                    ["role"] = s.Role,
                    ["note"] = s.Note,
                    ["sid"] = s.Sid,
                    ["model"] = s.Model,
                    ["runs"] = s.Runs,
                    ["lastUsed"] = s.LastUsed,
                });
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(SeatsPath,
                new JsonObject { ["seats"] = arr }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* 写失败不影响本轮 */ }
    }

    private static string NewSeatId() => "w" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>按角色找席位；没有就新建（名额满了就把最久没用的换掉）。</summary>
    private WorkerSeat SeatFor(string role)
    {
        LoadSeats();
        string key = (role ?? "").Trim();
        if (key.Length == 0) key = "执行";
        lock (_seatGate)
        {
            var hit = _seats.FirstOrDefault(s => string.Equals(s.Role, key, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
            if (_seats.Count >= MaxWorkerSeats)
            {
                var oldest = _seats.OrderBy(s => s.LastUsed).First();
                _seats.Remove(oldest);
            }
            var seat = new WorkerSeat { Id = NewSeatId(), Role = key };
            _seats.Add(seat);
            return seat;
        }
    }

    /// <summary>把 Router 会话定为「常驻」：同一条主会话永远复用同一个 Router 记录，状态一直是 active。</summary>
    private int EnsureRouterSession(int parentSid, string summary, int roles)
    {
        var existing = Sessions.List("router", null, parentSid).FirstOrDefault();
        int routerSid = existing?.Sid ?? _nextRouterSid++;
        // 常驻 = 不写 done：这是 Router 本身活着，不是「这次拆分结束了」
        UpsertSession(routerSid, "router", parentSid,
            existing?.Title is { Length: > 0 } t ? t : "Router · " + roles + "路",
            "active", summary, new List<AgentMessage>());
        return routerSid;
    }

    private List<object> SeatsJson()
    {
        LoadSeats();
        lock (_seatGate)
            return _seats
                .OrderByDescending(s => s.LastUsed)
                .Select(s => (object)new
                {
                    id = s.Id, role = s.Role, note = s.Note, sid = s.Sid, model = s.Model,
                    runs = s.Runs, lastUsed = s.LastUsed,
                })
                .ToList();
    }

    private void WorkerAdd(string role, string note = "")
    {
        if (string.IsNullOrWhiteSpace(role)) { Toast("先给这个 Worker 起个角色名", "error"); return; }
        LoadSeats();
        lock (_seatGate)
        {
            if (_seats.Any(s => string.Equals(s.Role, role.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                Toast("名单里已经有「" + role.Trim() + "」了", "error");
                return;
            }
            if (_seats.Count >= MaxWorkerSeats)
            {
                Toast("Worker 名单最多 " + MaxWorkerSeats + "个，先删几个", "error");
                return;
            }
            _seats.Add(new WorkerSeat { Id = NewSeatId(), Role = role.Trim(), Note = (note ?? "").Trim() });
        }
        SaveSeats();
        Emit(new { ev = "workers", list = SeatsJson() });
        Toast("已加入 Worker 名单：" + role.Trim());
    }

    /// <summary>从名单里移除（它的会话记录保留，历史不会凭空消失）。</summary>
    private void WorkerRemove(string id)
    {
        LoadSeats();
        int removed = 0;
        lock (_seatGate) removed = _seats.RemoveAll(s => s.Id == id || s.Role == id);
        if (removed == 0) { Toast("名单里没有这个 Worker", "error"); return; }
        SaveSeats();
        Emit(new { ev = "workers", list = SeatsJson() });
        Toast("已从名单移除（会话历史仍保留）");
    }

    private void EmitWorkers() => Emit(new { ev = "workers", list = SeatsJson() });

    // ---- 发送入口 → 常驻 Router ----

    /// <summary>用户发一条消息 → 先给常驻 Router 判定是「自己答」还是「派活」。</summary>
    private async Task RouteAndRunAsync(int sid, string text)
    {
        string t = (text ?? "").Trim();
        // 斜杠命令是工具指令，不是任务：不路由，直接照原样交给主助手
        if (!_config.RouterAuto || t.Length == 0 || t.StartsWith("/", StringComparison.Ordinal))
        {
            await RunAgentAsync(sid, text).ConfigureAwait(false);
            return;
        }

        RouteDecision d = AgentRouter.Decide(t, _agent.RunMode);
        Emit(new
        {
            ev = "routerPick",
            auto = true,
            path = d.Path == RoutePath.Slow ? "slow" : "fast",
            workers = d.Workers,
            roles = d.Roles,
            reasons = d.Reasons,
            confidence = d.Confidence,
        });

        if (!d.ShouldSplit)
        {
            await RunAgentAsync(sid, text).ConfigureAwait(false);
            return;
        }

        // ---- 慢路径：先规划（花一次 Router 模型调用），再并行 ----
        var cts = new CancellationTokenSource();
        _agentCts[sid] = cts;
        try
        {
            AddAgentSys(sid, " Router 判定：这次是多路任务（" + d.Reasons.FirstOrDefault("多步骤 / 多领域") +
                             "）。先规划分工，再并行执行。");
            var tasks = await PlanTasksAsync(t, d, cts.Token).ConfigureAwait(false);
            if (tasks == null || tasks.Count < 2)
            {
                AddAgentSys(sid, "规划没给出可用的分工（或判断根本不值得拆），改由单助手直做。");
                await RunAgentAsync(sid, text).ConfigureAwait(false);
                return;
            }

            AddAgentSys(sid, " Router 派出 " + tasks.Count + "路：" +
                             string.Join(" / ", tasks.Select(x => x.Role)) +
                             "。各自的上下文互相隔离，只共享我下发的简报。" +
                             "（Router 模型 " + RouterCfg.ApiModel + " · Worker 模型 " + WorkerCfg.ApiModel + "）");
            string summary = await RunSplitAsync(BuildSplitArgs(tasks), cts.Token).ConfigureAwait(false);

            // 把「原始请求 + 各路产出」一起交回主助手写最终回答。
            // 不能只给汇总：模型不知道用户到底问的是什么，就会写出一段没有对象的总结。
            string merged =
                "（这一轮由 Router 直接调度：你的助手没有再自己拆任务）\n\n" +
                "用户的原始请求：\n" + text + "\n\n" + summary;
            await RunAgentAsync(sid, merged).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SetAgentState(sid, RunState.Stopped);
            AddAgentSys(sid, "⏸ 已停止。");
        }
        catch (Exception ex)
        {
            AddAgentSys(sid, "Router 调度出错，改由单助手直做：" + ex.Message);
            await RunAgentAsync(sid, text).ConfigureAwait(false);
        }
        finally
        {
            if (_agentCts.TryGetValue(sid, out var cur) && ReferenceEquals(cur, cts)) _agentCts.Remove(sid);
            try { cts.Dispose(); } catch { }
        }
    }

    /// <summary>让 Router 用它自己的模型做一次「分工规划」：输出严格 JSON 的 tasks 数组。</summary>
    private async Task<List<(string Role, string Task)>?> PlanTasksAsync(string text, RouteDecision d, CancellationToken ct)
    {
        var cfg = RouterCfg;
        if (string.IsNullOrWhiteSpace(cfg.ApiUrl) || string.IsNullOrWhiteSpace(cfg.ApiModel)) return null;

        // 把用户维护的 Worker 名单给 Router 看：优先派给「已知的人」，名单外的角色才新开
        var seats = SeatsJson();
        var roster = new StringBuilder();
        foreach (var s in seats.Cast<object>())
        {
            var ty = s.GetType();
            string role = ty.GetProperty("role")?.GetValue(s)?.ToString() ?? "";
            string note = ty.GetProperty("note")?.GetValue(s)?.ToString() ?? "";
            if (role.Length == 0) continue;
            roster.AppendLine("- " + role + (note.Length > 0 ? "：" + note : ""));
        }

        string system =
            "你是 deepsleep 的任务路由器（Router）。用户刚发来一条请求，你要决定怎么分工。\n" +
            "规则：\n" +
            "1. 只有真正可以并行、彼此不依赖的活才拆；能一个助手顺序做完的，就输出空数组。\n" +
            "2. 最多 " + AgentRouter.MaxWorkers + "路，每路必须是自包含的子任务（执行者看不到用户原话，也看不到别的路）。\n" +
            "3. 优先使用下面「可用 Worker 名单」里的角色；确实需要新角色才自拟。\n" +
            "4. 只输出 JSON，不要解释、不要代码块外的任何文字。格式：\n" +
            "{\"tasks\":[{\"role\":\"前端\",\"task\":\"具体要做的事，写清产出形式与落盘路径要求\"}]}\n" +
            "不拆就输出：{\"tasks\":[]}\n";
        string user =
            (roster.Length > 0 ? "可用 Worker 名单：\n" + roster + "\n" : "（Worker 名单为空，可自拟角色）\n") +
            "Router 已经识别到的候选角色：" + (d.Roles.Count > 0 ? string.Join(" / ", d.Roles) : "（无）") + "\n" +
            "运行模式：" + _agent.RunMode + "（" +
            (_agent.RunMode == "chat" ? "只读，不要规划任何写文件/跑命令的子任务" : "可写文件、可跑命令") + "）\n\n" +
            "用户请求：\n" + text;

        try
        {
            using var client = new ApiClient
            {
                Url = cfg.ApiUrl, ApiKey = cfg.ApiKey, Model = cfg.ApiModel, Format = cfg.ApiFormat,
            };
            string? reply = await client.ChatAsync(system, user, 60, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(reply)) return null;
            var tasks = ParseSplitTasks(ExtractJson(reply));
            // Router 自己说「不拆」也是有效结论：交给上层回退直做
            if (tasks.Count < 2) return null;
            return tasks.Take(AgentRouter.MaxWorkers).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>从模型回复里抠出第一段完整 JSON 对象（容忍 ```json 包裹、前后废话）。</summary>
    private static string ExtractJson(string s)
    {
        string t = (s ?? "").Trim();
        int fence = t.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            int nl = t.IndexOf('\n', fence);
            if (nl > 0)
            {
                int end = t.IndexOf("```", nl + 1, StringComparison.Ordinal);
                if (end > nl) t = t.Substring(nl + 1, end - nl - 1).Trim();
            }
        }
        int first = t.IndexOf('{');
        int last = t.LastIndexOf('}');
        if (first >= 0 && last > first) t = t.Substring(first, last - first + 1);
        return t;
    }

    private static string BuildSplitArgs(List<(string Role, string Task)> tasks)
        => JsonSerializer.Serialize(new
        {
            tasks = tasks.Select(x => new { role = x.Role, task = x.Task }).ToList(),
        });

    /// <summary>给前端设置页用的绑定 JSON（不含 Key 本身，只告诉它「配没配」）。</summary>
    private static object BindingJson(AppConfig.ModelBinding b) => new
    {
        useMain = b == null || b.UseMain,
        apiUrl = b?.ApiUrl ?? "",
        apiModel = b?.ApiModel ?? "",
        apiFormat = b?.ApiFormat ?? "openai",
        hasKey = !string.IsNullOrWhiteSpace(b?.ApiKey),
        configured = b != null && b.Configured,
    };
}

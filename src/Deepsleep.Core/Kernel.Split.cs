using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler.Core;

/// <summary>「拆分任务」的执行器：把主助手交给的一个复杂任务拆成若干子任务，用独立的子智能体并行执行，再汇总。</summary>
public sealed partial class Kernel
{
    /// <summary>一次最多拆几路。太多会同时打满 API 限流、也会把用户电脑跑满。</summary>
    private const int MaxSplitTasks = 5;

    private async Task<string> RunSplitAsync(string argsJson, CancellationToken ct)
    {
        var tasks = ParseSplitTasks(argsJson);
        if (tasks.Count == 0)
            return "参数错误：需要一个 tasks 数组，形如 {\"tasks\":[{\"role\":\"前端\",\"task\":\"...\"}]}。";

        bool truncated = false;
        if (tasks.Count > MaxSplitTasks)
        {
            tasks = tasks.Take(MaxSplitTasks).ToList();
            truncated = true;
        }

        string mode = _agent.RunMode;

        // Router 会话持有共享简报，Worker 只看这份简报，各自历史互相不可见。
        int parentSid = _agentCurrentSid();
        var decision = AgentRouter.Decide(
            string.Join("\n", tasks.Select(t => t.Role + "：" + t.Task)), mode, tasks.Count);
        var shared = new SharedContext
        {
            Goal = Cap(tasks.Count > 1 ? tasks[0].Task : tasks[0].Task, 400),
            Mode = mode,
            Roles = tasks.Select(t => t.Role).ToList(),
            PriorMemory = Sessions.List("router", null, parentSid).FirstOrDefault()?.Summary ?? "",
        };
        foreach (string c in new[]
                 {
                     mode == "chat" ? "只读：不改文件、不跑会改动电脑的命令" : "产物必须真实落盘并给出完整路径",
                     "不要反问、不要等确认，直接交付",
                     tasks.Count > 1 ? "只做自己这一路，别替其它角色做" : "单路执行",
                 })
            shared.Constraints.Add(c);

        // Router 常驻：同一条主会话永远复用同一个 Router 记录（不写 done、不换 id），
        // 所以「路由层记得上次拆过什么」这件事不用额外存 —— 记录本身就是它的记忆。
        int routerSid = EnsureRouterSession(parentSid, shared.ToSummary(), tasks.Count);

        // Worker 走名单：同角色复用同一个会话（继承上次的产物与上下文），新角色才开新席位。
        var workerSids = new int[tasks.Count];
        var seats = new WorkerSeat[tasks.Count];
        for (int i = 0; i < tasks.Count; i++)
        {
            var seat = SeatFor(tasks[i].Role);
            seats[i] = seat;
            if (seat.Sid <= 0) seat.Sid = _nextWorkerSid++;
            workerSids[i] = seat.Sid;
            UpsertSession(workerSids[i], "worker", routerSid, tasks[i].Role, "active",
                "等待执行：" + Cap(tasks[i].Task, 80), new List<AgentMessage>());
        }
        SaveSeats();
        EmitWorkers();
        Emit(new
        {
            ev = "routerPlan",
            routerSid,
            parentSid,
            path = decision.Path == RoutePath.Slow ? "slow" : "fast",
            confidence = decision.Confidence,
            reasons = decision.Reasons,
            routerModel = RouterCfg.ApiModel,
            workerModel = WorkerCfg.ApiModel,
            workers = tasks.Select((t, i) => (object)new
            {
                sid = workerSids[i], role = t.Role, task = Cap(t.Task, 120), state = "active",
                model = WorkerCfg.ApiModel,
            }).ToList(),
        });

        // 先落一张卡片：用户能看见「拆了几路、每路在干嘛」，而不是干等一大段空白
        int sid = parentSid;
        var started = new List<ChatItem>();
        foreach (var (role, task) in tasks)
        {
            int seatIndex = started.Count;
            var item = new ChatItem
            {
                IsSelf = false,
                CanAccept = false,
                ToolName = Agent.ToolSplit,
                ToolSummary = $"{role}：{Cap(task, 60)}",
                ToolDetail = $"**角色**：{role}\n\n**子任务**：{task}\n\n**模型**：{WorkerCfg.ApiModel}\n\n（Worker 会话 #{workerSids[seatIndex]} 独立执行，完成后在此汇总）",
                ToolKind = "split",
                Text = $"拆出子任务「{role}」",
                Meta = "拆分任务",
                AvatarText = "AI",
                SelfAvatarText = "你",
                AvatarColor = "#4A90D9",
                WorkerSid = workerSids[seatIndex],
                SessionId = sid,
                TimeStr = Now(),
                ShowTime = false,
            };
            var conv = _agentConvs.FirstOrDefault(c => c.Sid == sid);
            if (conv != null)
            {
                conv.Items.Add(item);
                Emit(new { ev = "add", kind = 0, conv = sid, item = ItemJson(item) });
            }
            started.Add(item);
        }
        ScheduleSave();

        // 每路一个独立 Agent，并行跑；并发上限沿用集群那套（4），避免同时触发各家限流
        var results = new string[tasks.Count];
        var workerModels = new string[tasks.Count];
        using var gate = new SemaphoreSlim(Math.Min(4, tasks.Count));
        var workers = new List<Task>();
        for (int i = 0; i < tasks.Count; i++)
        {
            int idx = i;
            workers.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                // 登记为「真在跑」：看门狗据此区分僵尸会话和正常长任务
                MarkWorkerLive(workerSids[idx], true);
                try
                {
                    var one = await RunOneSubAgentAsync(tasks[idx].Role, tasks[idx].Task, mode, ct,
                                                        workerSids[idx], shared).ConfigureAwait(false);
                    results[idx] = one.Text;
                    workerModels[idx] = one.Model;
                }
                catch (OperationCanceledException) { results[idx] = "（已停止）"; }
                catch (Exception ex) { results[idx] = "[子智能体出错] " + ex.Message; }
                finally
                {
                    MarkWorkerLive(workerSids[idx], false);
                    gate.Release();
                }
            }, ct));
        }
        try { await Task.WhenAll(workers).ConfigureAwait(false); } catch { /* 单路异常已在内部兜住 */ }

        // 收口：Worker 会话落最终状态与摘要，Router 汇总成组织记忆（同会话下一次拆分直接复用）
        var artifacts = new List<string>();
        for (int i = 0; i < tasks.Count; i++)
        {
            bool ok = !results[i].StartsWith("[子智能体出错]", StringComparison.Ordinal)
                      && results[i] != "（已停止）" && !string.IsNullOrWhiteSpace(results[i]);
            string state = results[i] == "（已停止）" ? "stopped" : ok ? "done" : "stopped";
            string summary = ok ? Cap(results[i], 200) : results[i];
            // 追加到它自己的历史里（不覆盖）：下一轮复用同一席位时能接着上次的上下文。
            // 只留最近 40 条 —— Worker 的会话不该无限膨胀，重点靠 summary。
            List<AgentMessage> hist;
            try { hist = Sessions.Load(workerSids[i])?.Messages ?? new List<AgentMessage>(); }
            catch { hist = new List<AgentMessage>(); }
            hist.Add(new AgentMessage { Role = "user", Content = tasks[i].Task });
            hist.Add(new AgentMessage { Role = "assistant", Content = results[i], Model = workerModels[i] });
            if (hist.Count > 40) hist = hist.Skip(hist.Count - 40).ToList();
            UpsertSession(workerSids[i], "worker", routerSid, tasks[i].Role, state, summary, hist);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         results[i] ?? "", @"(?m)^\s*(?:产出路径|文件|产物)[：:]\s*(\S.+)$"))
                artifacts.Add(m.Groups[1].Value.Trim());
        }
        shared.Artifacts = artifacts.Distinct().Take(12).ToList();
        // Router 依旧是 active（常驻）；只把这一轮的组织记忆写进 summary，下一轮直接读它
        UpsertSession(routerSid, "router", parentSid,
            "Router · 已跑 " + tasks.Count + "路（" + Now() + "）", "active", shared.ToSummary(), new List<AgentMessage>());
        // 席位记一笔：谁跑过、什么时候跑的（名单面板按最近使用排序）
        for (int i = 0; i < seats.Length; i++)
        {
            if (seats[i] == null) continue;
            seats[i].Runs++;
            seats[i].LastUsed = Now();
            if (!string.IsNullOrWhiteSpace(workerModels[i])) seats[i].Model = workerModels[i];
        }
        SaveSeats();
        EmitWorkers();
        Emit(new
        {
            ev = "routerDone",
            routerSid,
            path = decision.Path == RoutePath.Slow ? "slow" : "fast",
            routerModel = RouterCfg.ApiModel,
            workers = tasks.Select((t, i) => (object)new
            {
                sid = workerSids[i], role = t.Role, model = workerModels[i],
                state = results[i] == "（已停止）" ? "stopped" : "done",
            }).ToList(),
            artifacts = shared.Artifacts,
        });

        // 把结果回填到卡片（用户点开就能看到每一路到底做了什么）
        var conv2 = _agentConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv2 != null)
        {
            for (int i = 0; i < tasks.Count && i < started.Count; i++)
            {
                string modelLine = string.IsNullOrWhiteSpace(workerModels[i])
                    ? "" : $"**模型**：{workerModels[i]}\n\n";
                started[i].Model = workerModels[i];
                started[i].ToolDetail = $"**角色**：{tasks[i].Role}\n\n**子任务**：{tasks[i].Task}\n\n"
                    + modelLine
                    + $"**Worker 会话**：#{workerSids[i]}\n\n---\n\n"
                    + (string.IsNullOrWhiteSpace(results[i]) ? "（没有产出）" : results[i]);
                Emit(new
                {
                    ev = "msgUpdate", kind = 0, conv = sid, id = started[i].Id,
                    toolDetail = started[i].ToolDetail, model = started[i].Model,
                });
            }
            ScheduleSave();
        }

        // 汇总回给主助手：它拿到这段之后要继续决策 / 写最终回答
        var sb = new StringBuilder();
        sb.AppendLine($"已并行执行 {tasks.Count} 路子任务" + (truncated ? $"（超过上限 {MaxSplitTasks}，只执行了前 {MaxSplitTasks} 路）" : "") + "：");
        for (int i = 0; i < tasks.Count; i++)
        {
            sb.AppendLine();
            sb.AppendLine($"### {i + 1}. {tasks[i].Role}");
            sb.AppendLine("任务：" + tasks[i].Task);
            sb.AppendLine("产出：");
            sb.AppendLine(string.IsNullOrWhiteSpace(results[i]) ? "（无产出）" : results[i]);
        }
        sb.AppendLine();
        sb.AppendLine("以上是各路子智能体的原始产出。请据此给用户一个完整的最终回答：");
        sb.AppendLine("说明每路做了什么、产物在哪个路径、有哪些遗留问题；不要原样复述，也不要再重复调用「拆分任务」。");
        if (mode == "chat")
            sb.AppendLine("（当前是 chat 模式：子智能体同样只做了只读工作，没有改动电脑。）");
        return sb.ToString();
    }

    /// <summary>跑一路子任务：独立 Agent 实例（独立 Worker 会话），配置继承主助手，但权限按 chat 规则收紧。</summary>
    private async Task<(string Text, string Model)> RunOneSubAgentAsync(string role, string task, string mode,
                                                   CancellationToken ct, int workerSid = -1, SharedContext? shared = null)
    {
        var sub = new Agent(_engine)
        {
            RunMode = mode == "chat" ? "chat" : "boom",   // 子智能体没人给它确认，work 模式会卡死，所以直接给足权限
            DataDir = "",
            Streaming = false,                            // 子智能体的过程不往界面刷，只回传最终产出
            MaxConcurrentCalls = 2,
            AutoRemember = false,
        };
        sub.ApplyConfig(WorkerCfg);   // Worker 可以单独指定模型（设置里「Worker 模型」）
        sub.Backend = _agent.Backend;
        sub.MultimodalMain = _config.MultimodalMain;
        sub.Skills = _agent.Skills;
        // 提示里写清角色与交付要求：子智能体看不到主对话，必须自包含（共享简报由 Router 下发）
        string prompt =
            $"你是一名独立执行子任务的智能体，你的角色是「{role}」。\n" +
            "完成后直接给出你的产出（可以是结论、代码、文件路径等），不要反问、不要等确认。\n" +
            (mode == "chat"
                ? "当前处于只读模式：禁止执行任何会改动电脑的操作（不写文件、不运行命令），只做分析与给出建议。\n"
                : "把产物真实写到磁盘，并在产出里给出完整本地路径，形如「产出路径：D:\\…」。\n") +
            (shared != null ? "\n" + shared.ToBrief() + "\n" : "") +
            "\n你的任务：\n" + task;
        // 复用席位：把这位 Worker 上次的历史灌回实例，它才真的「记得上一轮」。
        // （只复用 sid 而不回灌历史，界面看着是一个会话，模型却是白纸 —— 那就等于没复用。）
        if (workerSid > 0)
        {
            try
            {
                var rec = Sessions.Load(workerSid);
                if (rec?.Messages is { Count: > 0 }) sub.RestoreSession(workerSid, rec.Messages);
            }
            catch { /* 历史读不到就当新会话跑，不阻塞 */ }
        }
        string text = await sub.RunAsync(workerSid, prompt, ct).ConfigureAwait(false);
        return (text, sub.ModelTag);
    }

    /// <summary>落一个 Session 记录（保留 CreatedUtc、Meta 等已有字段，避免每次覆盖丢元数据）。</summary>
    private void UpsertSession(int sid, string kind, int? parentSid, string title, string state,
                               string summary, List<AgentMessage> messages)
    {
        try
        {
            var rec = Sessions.Load(sid) ?? new SessionRecord { Sid = sid };
            rec.Kind = kind;
            rec.ParentSid = parentSid;
            rec.Title = string.IsNullOrWhiteSpace(title) ? rec.Title : title;
            rec.State = state;
            rec.Summary = summary ?? "";
            if (messages.Count > 0) rec.Messages = messages;
            Sessions.Upsert(rec);
        }
        catch { /* 落盘失败不能影响主流程：内存里照样跑完 */ }
    }

    /// <summary>解析拆分任务的参数。容忍几种常见写法（tasks/task/子任务、role/name/角色）。</summary>
    private static List<(string Role, string Task)> ParseSplitTasks(string argsJson)
    {
        var list = new List<(string, string)>();
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            JsonElement arr = default;
            bool found = false;
            if (root.ValueKind == JsonValueKind.Array) { arr = root; found = true; }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "tasks", "task", "子任务", "items", "subtasks" })
                    if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array) { arr = v; found = true; break; }
            }
            if (!found) return list;

            int n = 0;
            foreach (var it in arr.EnumerateArray())
            {
                if (it.ValueKind == JsonValueKind.String)
                {
                    string t = it.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(t)) list.Add(($"子任务{++n}", t.Trim()));
                    continue;
                }
                if (it.ValueKind != JsonValueKind.Object) continue;
                string role = Str(it, "role") ?? Str(it, "name") ?? Str(it, "角色") ?? Str(it, "title") ?? $"子任务{n + 1}";
                string task = Str(it, "task") ?? Str(it, "description") ?? Str(it, "任务") ?? Str(it, "prompt") ?? "";
                if (string.IsNullOrWhiteSpace(task)) continue;
                list.Add((role.Trim(), task.Trim()));
                n++;
            }
        }
        catch { /* 参数不是合法 JSON：当没拆 */ }
        return list;

        static string? Str(JsonElement o, string name)
            => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>当前主助手会话 id（拆分卡片要落到它所在的会话里）。</summary>
    private int _agentCurrentSid() => _agentCur?.Sid ?? 0;
}

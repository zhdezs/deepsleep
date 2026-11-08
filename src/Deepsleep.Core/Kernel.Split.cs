using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler.Core;

/// <summary>
/// 「拆分任务」的执行器：把主助手交给的一个复杂任务拆成若干子任务，用**独立**的子智能体并行执行，再汇总。
/// <para>
/// 为什么每个子任务起一个新的 <see cref="Agent"/>，而不是复用集群那份共享实例：
/// 共享实例要靠 sid 区分彼此，而 Agent 内部有 <c>_currentSid</c> 这类可变状态 ——
/// 一路并行就会互相踩。独立实例彼此完全隔离，谁都不影响谁，代价只是多几个对象。
/// </para>
/// <para>
/// 权限继承：主助手是 chat（不改动电脑）时，子智能体也必须是 chat，
/// 否则用户会在「纯聊天」模式下被悄悄改了磁盘 —— 这是不可接受的越权。
/// </para>
/// </summary>
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

        // ---- Router 层（第 2 期）：一次拆分 = 一个 Router 会话 + N 个上下文隔离的 Worker 会话 ----
        // Router 持有共享上下文（目标/权限/约束/角色/产物/同会话历史结论），Worker 只能看这份简报，
        // 彼此的私有历史互相不可见 —— 这就是"共享上下文 + 上下文隔离"的落地方式。
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

        int routerSid = _nextRouterSid++;
        UpsertSession(routerSid, "router", parentSid, "路由：" + Cap(tasks[0].Role, 20) + " 等 " + tasks.Count + " 路",
            "active", shared.ToSummary(), new List<AgentMessage>());

        var workerSids = new int[tasks.Count];
        for (int i = 0; i < tasks.Count; i++)
        {
            workerSids[i] = _nextWorkerSid++;
            UpsertSession(workerSids[i], "worker", routerSid, tasks[i].Role, "active",
                "等待执行：" + Cap(tasks[i].Task, 80), new List<AgentMessage>());
        }
        Emit(new
        {
            ev = "routerPlan",
            routerSid,
            parentSid,
            path = decision.Path == RoutePath.Slow ? "slow" : "fast",
            confidence = decision.Confidence,
            reasons = decision.Reasons,
            workers = tasks.Select((t, i) => (object)new
            {
                sid = workerSids[i], role = t.Role, task = Cap(t.Task, 120), state = "active",
            }).ToList(),
        });

        // 先落一张卡片：用户能看见「拆了几路、每路在干嘛」，而不是干等一大段空白
        int sid = parentSid;
        var started = new List<ChatItem>();
        foreach (var (role, task) in tasks)
        {
            var item = new ChatItem
            {
                IsSelf = false,
                CanAccept = false,
                ToolName = Agent.ToolSplit,
                ToolSummary = $"{role}：{Cap(task, 60)}",
                ToolDetail = $"**角色**：{role}\n\n**子任务**：{task}\n\n（Worker 会话 #{workerSids[started.Count]} 独立执行，完成后在此汇总）",
                ToolKind = "split",
                Text = $"拆出子任务「{role}」",
                Meta = "拆分任务",
                AvatarText = "AI",
                SelfAvatarText = "你",
                AvatarColor = "#4A90D9",
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
        using var gate = new SemaphoreSlim(Math.Min(4, tasks.Count));
        var workers = new List<Task>();
        for (int i = 0; i < tasks.Count; i++)
        {
            int idx = i;
            workers.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    results[idx] = await RunOneSubAgentAsync(tasks[idx].Role, tasks[idx].Task, mode, ct,
                                                             workerSids[idx], shared).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { results[idx] = "（已停止）"; }
                catch (Exception ex) { results[idx] = "[子智能体出错] " + ex.Message; }
                finally { gate.Release(); }
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
            UpsertSession(workerSids[i], "worker", routerSid, tasks[i].Role, state, summary,
                new List<AgentMessage>
                {
                    new() { Role = "user", Content = tasks[i].Task },
                    new() { Role = "assistant", Content = results[i] },
                });
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         results[i] ?? "", @"(?m)^\s*(?:产出路径|文件|产物)[：:]\s*(\S.+)$"))
                artifacts.Add(m.Groups[1].Value.Trim());
        }
        shared.Artifacts = artifacts.Distinct().Take(12).ToList();
        UpsertSession(routerSid, "router", parentSid, "路由：" + Cap(tasks[0].Role, 20) + " 等 " + tasks.Count + " 路",
            "done", shared.ToSummary(), new List<AgentMessage>());
        Emit(new
        {
            ev = "routerDone",
            routerSid,
            path = decision.Path == RoutePath.Slow ? "slow" : "fast",
            workers = tasks.Select((t, i) => (object)new
            {
                sid = workerSids[i], role = t.Role, state = results[i] == "（已停止）" ? "stopped" : "done",
            }).ToList(),
            artifacts = shared.Artifacts,
        });

        // 把结果回填到卡片（用户点开就能看到每一路到底做了什么）
        var conv2 = _agentConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv2 != null)
        {
            for (int i = 0; i < tasks.Count && i < started.Count; i++)
            {
                started[i].ToolDetail = $"**角色**：{tasks[i].Role}\n\n**子任务**：{tasks[i].Task}\n\n"
                    + $"**Worker 会话**：#{workerSids[i]}\n\n---\n\n"
                    + (string.IsNullOrWhiteSpace(results[i]) ? "（没有产出）" : results[i]);
                Emit(new { ev = "msgUpdate", kind = 0, conv = sid, id = started[i].Id, toolDetail = started[i].ToolDetail });
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
    private async Task<string> RunOneSubAgentAsync(string role, string task, string mode, CancellationToken ct,
                                                   int workerSid = -1, SharedContext? shared = null)
    {
        var sub = new Agent(_engine)
        {
            RunMode = mode == "chat" ? "chat" : "boom",   // 子智能体没人给它确认，work 模式会卡死，所以直接给足权限
            DataDir = "",
            Streaming = false,                            // 子智能体的过程不往界面刷，只回传最终产出
            MaxConcurrentCalls = 2,
            AutoRemember = false,
        };
        sub.ApplyConfig(_config);
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
        // 传真实 Worker 会话 id：它在 Session 库里是一个独立会话，上下文与其它 Worker 完全隔离
        return await sub.RunAsync(workerSid, prompt, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 落一个 Session 记录（保留 CreatedUtc、Meta 等已有字段，避免每次覆盖丢元数据）。
    /// Router / Worker 的状态与摘要都走这里，Watchdog（第 4 期）直接读同一份数据。
    /// </summary>
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

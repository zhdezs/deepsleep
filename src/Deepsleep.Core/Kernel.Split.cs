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

        // 先落一张卡片：用户能看见「拆了几路、每路在干嘛」，而不是干等一大段空白
        int sid = _agentCurrentSid();
        var started = new List<ChatItem>();
        foreach (var (role, task) in tasks)
        {
            var item = new ChatItem
            {
                IsSelf = false,
                CanAccept = false,
                ToolName = Agent.ToolSplit,
                ToolSummary = $"{role}：{Cap(task, 60)}",
                ToolDetail = $"**角色**：{role}\n\n**子任务**：{task}\n\n（子智能体独立执行，完成后在此汇总）",
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
                    results[idx] = await RunOneSubAgentAsync(tasks[idx].Role, tasks[idx].Task, mode, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { results[idx] = "（已停止）"; }
                catch (Exception ex) { results[idx] = "[子智能体出错] " + ex.Message; }
                finally { gate.Release(); }
            }, ct));
        }
        try { await Task.WhenAll(workers).ConfigureAwait(false); } catch { /* 单路异常已在内部兜住 */ }

        // 把结果回填到卡片（用户点开就能看到每一路到底做了什么）
        var conv2 = _agentConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv2 != null)
        {
            for (int i = 0; i < tasks.Count && i < started.Count; i++)
            {
                started[i].ToolDetail = $"**角色**：{tasks[i].Role}\n\n**子任务**：{tasks[i].Task}\n\n---\n\n"
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

    /// <summary>跑一路子任务：独立 Agent 实例，配置继承主助手，但权限按 chat 规则收紧。</summary>
    private async Task<string> RunOneSubAgentAsync(string role, string task, string mode, CancellationToken ct)
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
        // 提示里写清角色与交付要求：子智能体看不到主对话，必须自包含
        string prompt =
            $"你是一名独立执行子任务的智能体，你的角色是「{role}」。\n" +
            "完成后直接给出你的产出（可以是结论、代码、文件路径等），不要反问、不要等确认。\n" +
            (mode == "chat"
                ? "当前处于只读模式：禁止执行任何会改动电脑的操作（不写文件、不运行命令），只做分析与给出建议。\n"
                : "把产物真实写到磁盘，并在产出里给出完整本地路径。\n") +
            "\n你的任务：\n" + task;
        return await sub.RunAsync(-1, prompt, ct).ConfigureAwait(false);
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

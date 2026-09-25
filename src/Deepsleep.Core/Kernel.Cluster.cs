using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler.Core;

/// <summary>
/// 内核：Agent 集群（一句话拆解 → 多 Agent 并行 → 指挥官汇总）。
/// </summary>
public sealed partial class Kernel
{
    /// <summary>一次集群执行的状态：指令文本、子任务成员、汇总会话 id。</summary>
    private sealed class ClusterRun
    {
        public string UserText = "";
        public List<ClusterWorker> Workers = new();
        public int SummarySid;
    }

    // ------------------------------------------------------------------
    // 状态
    // ------------------------------------------------------------------

    private RunState GetClusterState(int sid)
        => _clusterStates.TryGetValue(sid, out var s) ? s : RunState.Idle;

    private void SetClusterState(int sid, RunState state)
    {
        _clusterStates[sid] = state;
        EmitStatus(2);
    }

    private string ClusterBusy(int sid)
    {
        string running = "";
        if (_clusterRuns.TryGetValue(sid, out var run))
        {
            int n = run.Workers.Count(w => w.Status == "运行中");
            running = n > 0 ? $"{n}/{run.Workers.Count} 个 Agent 并行中" : "";
        }
        return GetClusterState(sid) switch
        {
            RunState.Running => running.Length > 0 ? "● " + running : "● 执行中…",
            RunState.Stopped => "⏸ 已停止（可点恢复）",
            _ => "",
        };
    }

    private string ClusterStatusText()
    {
        string backend = _clusterAgent.Backend switch
        {
            "api" => $"外部 API（{_config.ApiModel}）",
            "llm" => $"本地 Ollama（{_config.OllamaModel}）",
            "local" => "纯本地（自训练）",
            _ => !string.IsNullOrWhiteSpace(_config.ApiKey) ? $"自动 · API（{_config.ApiModel}）" : "自动 · 本地 Ollama",
        };
        int running = 0, total = 0;
        foreach (var c in _clusterConvs)
            if (_clusterRuns.TryGetValue(c.Sid, out var r))
            {
                total += r.Workers.Count;
                running += r.Workers.Count(w => w.Status == "运行中");
            }
        string lair = total > 0 ? $"　并行：{running}/{total}" : "";
        return $"模式：boom · 全自动　后端：{backend}　联网：{(_clusterAgent.WebSearchEnabled ? "开" : "关")}" +
               $"　🧠 记忆开{(_clusterAgent.FastMode ? "　⚡ 极速" : "")}　工具：命令/Python/读写/抓取/图片/看图{lair}";
    }

    // ------------------------------------------------------------------
    // 会话内小助手
    // ------------------------------------------------------------------

    private void AddClusterSys(int sid, string text)
    {
        var conv = _clusterConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv == null) return;
        var item = new ChatItem { IsSys = true, Text = text, SessionId = sid, TimeStr = Now(), ShowTime = ShouldShowTime() };
        conv.Items.Add(item);
        Emit(new { ev = "add", kind = 2, conv = sid, item = ItemJson(item) });
    }

    private void AddClusterThinking(int sid)
    {
        var conv = _clusterConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv == null) return;
        if (_clusterThinking.TryGetValue(sid, out var prev) && conv.Items.Contains(prev))
            RemoveRaw(conv, prev);
        var item = new ChatItem
        {
            IsSelf = false,
            Thinking = true,
            CanAccept = false,
            Text = "思考中",
            Meta = "AI",
            AvatarText = "AI",
            SelfAvatarText = "你",
            AvatarColor = "#7C4DFF",
            SessionId = sid,
            TimeStr = Now(),
            ShowTime = false,
        };
        _clusterThinking[sid] = item;
        conv.Items.Add(item);
        Emit(new { ev = "add", kind = 2, conv = sid, item = ItemJson(item) });
    }

    private void RemoveClusterThinking(int sid)
    {
        if (!_clusterThinking.TryGetValue(sid, out var item)) return;
        var conv = _clusterConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv != null && conv.Items.Contains(item)) RemoveRaw(conv, item);
        _clusterThinking.Remove(sid);
    }

    private ClusterRun? FindRunBySid(int sid)
    {
        foreach (var c in _clusterConvs)
            if (_clusterRuns.TryGetValue(c.Sid, out var r) &&
                (r.SummarySid == sid || r.Workers.Any(w => w.Sid == sid))) return r;
        return null;
    }

    private Conversation? ConvOfRun(ClusterRun run)
    {
        foreach (var c in _clusterConvs)
            if (_clusterRuns.TryGetValue(c.Sid, out var r) && ReferenceEquals(r, run)) return c;
        return null;
    }

    private ChatItem MakeClusterBubble(ClusterRun run, int sid, string name, bool isSummary)
    {
        int idx = run.Workers.FindIndex(w => w.Sid == sid);
        return new ChatItem
        {
            IsSelf = false,
            CanAccept = false,
            Text = "",
            Meta = isSummary ? "指挥官汇总" : $"Agent「{name}」",
            AvatarText = isSummary ? "指挥" : WorkerAvatar(name),
            SelfAvatarText = "你",
            AvatarColor = isSummary ? "#F09F1F" : ClusterColors[Math.Max(0, idx % ClusterColors.Length)],
            Speaker = isSummary ? "指挥官" : $"Agent「{name}」",
            SessionId = sid,
            TimeStr = Now(),
            ShowTime = ShouldShowTime(),
        };
    }

    private void AddClusterBubble(ClusterRun run, int sid, string text, string name, string meta, bool isSummary)
    {
        var conv = ConvOfRun(run);
        if (conv == null) return;
        var item = MakeClusterBubble(run, sid, name, isSummary);
        item.Meta = meta;
        item.Text = text;
        conv.Items.Add(item);
        _clusterStreamItems[sid] = item;
        Emit(new { ev = "add", kind = 2, conv = conv.Sid, item = ItemJson(item) });
    }

    // ------------------------------------------------------------------
    // 集群 Agent 事件
    // ------------------------------------------------------------------

    private void OnClusterMessage(int sid, AgentMessage m)
    {
        lock (_gate)
        {
            var w = FindRunBySid(sid)?.Workers.FirstOrDefault(x => x.Sid == sid);
            if (w == null || m.Role == "user") return;
            if (m.Role == "tool")
            {
                string t = m.Content.Length > 300 ? m.Content[..300] + "…" : m.Content;
                w.LogText += $"[工具「{m.Meta}」] {t}\n";
                if (w.LogText.Length > 8000) w.LogText = w.LogText[^8000..];
            }
        }
    }

    private void OnClusterDelta(int sid, string delta)
    {
        lock (_gate)
        {
            var run = FindRunBySid(sid);
            var conv = run == null ? null : ConvOfRun(run);
            if (run == null || conv == null) return;
            if (_clusterThinking.TryGetValue(conv.Sid, out var th) && conv.Items.Contains(th))
            {
                RemoveRaw(conv, th);
                _clusterThinking.Remove(conv.Sid);
            }
            if (!_clusterStreamItems.TryGetValue(sid, out var item) || !conv.Items.Contains(item))
            {
                bool isSummary = run.SummarySid == sid;
                string name = isSummary ? "指挥官" : run.Workers.FirstOrDefault(w => w.Sid == sid)?.Name ?? "Agent";
                item = MakeClusterBubble(run, sid, name, isSummary);
                _clusterStreamItems[sid] = item;
                conv.Items.Add(item);
                Emit(new { ev = "add", kind = 2, conv = conv.Sid, item = ItemJson(item) });
            }
            item.Text += delta;
            Emit(new { ev = "delta", kind = 2, conv = conv.Sid, id = item.Id, text = item.Text });
        }
    }
    // ------------------------------------------------------------------
    // 执行
    // ------------------------------------------------------------------

    private async Task RunClusterCoreAsync(int sid, string text, bool addUserBubble,
        List<(string role, string task)>? preset)
    {
        SetClusterState(sid, RunState.Running);
        AddClusterThinking(sid);
        var cts = new CancellationTokenSource();
        _clusterCts[sid] = cts;
        var run = new ClusterRun { UserText = text };
        _clusterRuns[sid] = run;
        var conv = _clusterConvs.First(c => c.Sid == sid);
        try
        {
            if (addUserBubble)
            {
                var bubble = new ChatItem
                {
                    IsSelf = true,
                    CanAccept = false,
                    Text = text,
                    Meta = "集群指令",
                    AvatarText = "AI",
                    SelfAvatarText = "你",
                    AvatarColor = "#4A90D9",
                    SessionId = sid,
                    TimeStr = Now(),
                    ShowTime = ShouldShowTime(),
                };
                conv.Items.Add(bubble);
                Emit(new { ev = "add", kind = 2, conv = sid, item = ItemJson(bubble) });
                MaybeAutoTitle(conv, text);
                Emit(new { ev = "convs", tab = _tab, convs = ConvListJson(_tab) });
            }

            List<(string role, string task)> plan;
            if (preset != null && preset.Count >= 2) plan = preset;
            else plan = await PlanAndRetryAsync(sid, text, cts.Token).ConfigureAwait(false);

            foreach (var (role, task) in plan.Take(10))
            {
                var w = new ClusterWorker { Name = role, Task = task, Sid = _nextClusterSid++ };
                _clusterAgent.NewSession(w.Sid);
                run.Workers.Add(w);
            }
            AddClusterSys(sid, $"🧠 指挥官已拆解为 {run.Workers.Count} 个子任务，并行执行中：{string.Join("、", run.Workers.Select(w => w.Name))}");
            EmitStatus(2);

            await Task.WhenAll(run.Workers.Select(w => RunClusterWorkerAsync(run, w, cts.Token))).ConfigureAwait(false);
            if (cts.IsCancellationRequested)
            {
                SetClusterState(sid, RunState.Stopped);
                AddClusterSys(sid, "⏸ 集群已停止，可点「恢复」重新执行。");
                return;
            }

            int done = run.Workers.Count(w => w.Status == "完成");
            int failed = run.Workers.Count(w => w.Status == "失败");
            AddClusterSys(sid, $"📊 并行执行完成：{done} 完成 / {failed} 失败，指挥官汇总中…");
            run.SummarySid = _nextClusterSid++;
            _clusterAgent.NewSession(run.SummarySid);
            var sb = new StringBuilder();
            foreach (var w in run.Workers)
            {
                sb.AppendLine();
                sb.AppendLine($"=== {w.Name}（{w.Status}）===");
                sb.AppendLine($"任务：{w.Task}");
                sb.AppendLine(Clip(w.LogText, 1200));
            }
            string summary = await _clusterAgent.SummarizeAsync(ClusterSummaryPrompt, sb.ToString(), cts.Token,
                d => OnClusterDelta(run.SummarySid, d)).ConfigureAwait(false);
            if (cts.IsCancellationRequested)
            {
                SetClusterState(sid, RunState.Stopped);
                AddClusterSys(sid, "⏸ 汇总已停止，可点「恢复」继续。");
                return;
            }
            if (!string.IsNullOrWhiteSpace(summary) && !_clusterStreamItems.ContainsKey(run.SummarySid))
                AddClusterBubble(run, run.SummarySid, summary, "指挥官", "指挥官汇总", true);
            else if (string.IsNullOrWhiteSpace(summary) && !_clusterStreamItems.ContainsKey(run.SummarySid))
                AddClusterSys(sid, "⚠️ 指挥官汇总失败（后端不可用或已限流）。");
            AddClusterSys(sid, "✅ 集群任务全部完成。");
            SetClusterState(sid, RunState.Idle);
        }
        catch (OperationCanceledException)
        {
            SetClusterState(sid, RunState.Stopped);
            AddClusterSys(sid, "⏸ 集群已停止，可点「恢复」重新执行。");
        }
        catch (Exception ex)
        {
            AddClusterSys(sid, "集群出错：" + ex.Message);
            SetClusterState(sid, RunState.Idle);
        }
        finally
        {
            _clusterCts.Remove(sid);
            RemoveClusterThinking(sid);
            EmitStatus(2);
            ScheduleSave();
        }
    }

    private async Task<List<(string role, string task)>> PlanAndRetryAsync(int sid, string instruction, CancellationToken ct)
    {
        string planText = "";
        var plan = new List<(string, string)>();
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            AddClusterSys(sid, $"🧠 指挥官拆解中（第 {attempt} 次尝试）…");
            string feedback = attempt == 1 ? "" :
                "上次输出无效：必须输出严格 JSON 数组 [{\"角色\":...,\"任务\":...}]，至少 3 个子任务。上次输出：" + Clip(planText, 500);
            planText = await _clusterAgent.PlanAsync(instruction, feedback).ConfigureAwait(false);
            plan = TryParsePlan(planText);
            if (plan.Count >= 3) break;
        }
        if (plan.Count < 2)
        {
            string brief = Clip(instruction, 80);
            AddClusterSys(sid,
                "⚠️ 指挥官拆分无效，已按「实现/测试/素材」保底拆成 3 个成员继续执行。\n\n指挥官输出：\n" + Clip(planText, 500));
            plan = new List<(string, string)>
            {
                ("实现", $"负责「{brief}」的具体实现，可写代码/脚本完成，产物保存到工作区。"),
                ("测试校验", $"对「{brief}」的产物进行测试与校验，报告问题与修复建议。"),
                ("素材与文档", $"为「{brief}」用「生成图片」准备图片素材（如需），并撰写说明文档。"),
            };
        }
        return plan.Take(10).ToList();
    }

    private async Task RunClusterWorkerAsync(ClusterRun run, ClusterWorker w, CancellationToken ct)
    {
        w.Status = "运行中";
        EmitStatus(2);
        try
        {
            string result = await _clusterAgent.RunAsync(w.Sid, "你的任务：" + w.Task, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) { w.Status = "已停止"; return; }
            w.LogText = string.IsNullOrWhiteSpace(result) ? "（无输出）" : result.Trim();
            w.Status = (result.Contains("后端不可用", StringComparison.Ordinal) ||
                        result.Contains("出错", StringComparison.Ordinal) ||
                        result.Contains("失败", StringComparison.Ordinal)) ? "失败" : "完成";
            if (!string.IsNullOrWhiteSpace(w.LogText) && !_clusterStreamItems.ContainsKey(w.Sid))
                AddClusterBubble(run, w.Sid, w.LogText, WorkerAvatar(w.Name), $"Agent「{w.Name}」", false);
        }
        catch (OperationCanceledException) { w.Status = "已停止"; }
        catch (Exception ex) { w.Status = "失败"; w.LogText = "[错误] " + ex.Message; }
        finally
        {
            EmitStatus(2);
            ScheduleSave();
        }
    }
    // ------------------------------------------------------------------
    // 恢复 / 重新生成 / 清空 / 模板
    // ------------------------------------------------------------------

    private async Task ResumeClusterAsync(int sid)
    {
        if (!_clusterRuns.TryGetValue(sid, out var run) || string.IsNullOrWhiteSpace(run.UserText))
        {
            AddClusterSys(sid, "没有可恢复的集群任务。");
            SetClusterState(sid, RunState.Idle);
            return;
        }
        TruncateAfterLastUserMessage(sid, run.UserText);
        foreach (var w in run.Workers) { _clusterAgent.ResetSession(w.Sid); _clusterStreamItems.Remove(w.Sid); }
        if (run.SummarySid != 0) _clusterStreamItems.Remove(run.SummarySid);
        AddClusterSys(sid, "🔄 已从断点重新执行集群任务…");
        await RunClusterCoreAsync(sid, run.UserText, false, run.Workers.Select(w => (w.Name, w.Task)).ToList()).ConfigureAwait(false);
    }

    private async Task ClusterRegenerateAsync()
    {
        int sid = _clusterCur.Sid;
        if (!_clusterRuns.TryGetValue(sid, out var run) || string.IsNullOrWhiteSpace(run.UserText)) return;
        TruncateAfterLastUserMessage(sid, run.UserText);
        foreach (var w in run.Workers) { _clusterAgent.ResetSession(w.Sid); _clusterStreamItems.Remove(w.Sid); }
        if (run.SummarySid != 0) _clusterStreamItems.Remove(run.SummarySid);
        AddClusterSys(sid, "↻ 重新生成集群结果…");
        await RunClusterCoreAsync(sid, run.UserText, false, null).ConfigureAwait(false);
    }

    private void TruncateAfterLastUserMessage(int sid, string userText)
    {
        var conv = _clusterConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv == null) return;
        int last = -1;
        for (int i = conv.Items.Count - 1; i >= 0; i--)
            if (conv.Items[i].IsSelf && !conv.Items[i].IsSys && conv.Items[i].Text == userText) { last = i; break; }
        while (conv.Items.Count > last + 1) conv.Items.RemoveAt(conv.Items.Count - 1);
    }

    /// <summary>清空当前集群会话（停掉任务、重置所有 worker 会话）。</summary>
    private void ClusterClear(int sid)
    {
        StopCluster(sid);
        if (_clusterRuns.TryGetValue(sid, out var run))
        {
            foreach (var w in run.Workers) _clusterAgent.ResetSession(w.Sid);
            foreach (var w in run.Workers) _clusterStreamItems.Remove(w.Sid);
            if (run.SummarySid != 0) _clusterStreamItems.Remove(run.SummarySid);
        }
        _clusterAgent.ResetSession(sid);
        _clusterRuns.Remove(sid);
        var conv = _clusterConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv != null) conv.Items.Clear();
        _clusterThinking.Remove(sid);
        _clusterStates[sid] = RunState.Idle;
        Emit(new { ev = "conv", tab = 1, convs = ConvListJson(1), conv = ConvJson(_clusterCur), items = new List<object>(), status = StatusJson(1) });
        ScheduleSave();
    }

    /// <summary>删除会话时顺带清理集群运行记录。</summary>
    private void ResetClusterConversation(int sid)
    {
        StopCluster(sid);
        _clusterAgent.ResetSession(sid);
        if (_clusterRuns.Remove(sid, out var run))
        {
            foreach (var w in run.Workers)
            {
                _clusterAgent.ResetSession(w.Sid);
                _clusterStreamItems.Remove(w.Sid);
            }
            if (run.SummarySid != 0) _clusterStreamItems.Remove(run.SummarySid);
        }
        _clusterStates.Remove(sid);
        _clusterCts.Remove(sid);
        _clusterThinking.Remove(sid);
    }

    private void StopCluster(int sid)
    {
        if (_clusterCts.TryGetValue(sid, out var cts)) cts.Cancel();
    }

    /// <summary>用内置分工模板直接开跑（界面把模板序号发过来）。</summary>
    private async Task RunClusterTemplateAsync(int index)
    {
        if (index < 0 || index >= ClusterTemplates.Length) return;
        int sid = _clusterCur.Sid;
        if (GetClusterState(sid) == RunState.Running) return;
        var t = ClusterTemplates[index];
        string text = $"使用分工模板「{t.Name}」：\n" + string.Join("\n", t.Members.Select(m => $"{m.Role}：{m.Task}"));
        await RunClusterCoreAsync(sid, text, true, t.Members.Select(m => (m.Role, m.Task)).ToList()).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // 指挥官输出解析
    // ------------------------------------------------------------------

    /// <summary>从指挥官输出里解析 [{"角色":...,"任务":...}] 计划。</summary>
    private static List<(string role, string task)> TryParsePlan(string text)
    {
        var list = new List<(string, string)>();
        int idx = text.IndexOf('[');
        while (idx >= 0)
        {
            int end = FindJsonArrayEnd(text, idx);
            if (end > idx)
            {
                try
                {
                    var candidate = new List<(string, string)>();
                    using var doc = System.Text.Json.JsonDocument.Parse(text[idx..end]);
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        string role = GetJsonStr(el, "角色") ?? GetJsonStr(el, "role") ?? GetJsonStr(el, "名称") ?? $"Agent {candidate.Count + 1}";
                        string task = GetJsonStr(el, "任务") ?? GetJsonStr(el, "task") ?? "";
                        if (!string.IsNullOrWhiteSpace(task)) candidate.Add((role, task));
                    }
                    if (candidate.Count > 0) { list = candidate; break; }
                }
                catch { /* 尝试下一个 [ */ }
            }
            idx = text.IndexOf('[', idx + 1);
        }
        return list;
    }

    private static int FindJsonArrayEnd(string s, int start)
    {
        int depth = 0;
        bool inStr = false, esc = false;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '[') depth++;
            else if (c == ']') { depth--; if (depth == 0) return i + 1; }
        }
        return -1;
    }

    private static string? GetJsonStr(System.Text.Json.JsonElement el, string name)
    {
        if (el.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (el.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
            return v.GetString();
        return null;
    }
}
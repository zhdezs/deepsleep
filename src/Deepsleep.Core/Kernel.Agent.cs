using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler.Core;

/// <summary>
/// 内核：AI 助手（会话状态、流式输出、思考占位、三态按钮、模式开关、权限确认）。
/// </summary>
public sealed partial class Kernel
{
    private sealed class AskResult
    {
        public bool Yes;
        public bool Remember;
    }

    // ------------------------------------------------------------------
    // 会话状态
    // ------------------------------------------------------------------

    private RunState GetAgentState(int sid)
        => _agentStates.TryGetValue(sid, out var s) ? s : RunState.Idle;

    private void SetAgentState(int sid, RunState state)
    {
        _agentStates[sid] = state;
        EmitStatus(0);
    }

    private string AgentBusy(int sid) => GetAgentState(sid) switch
    {
        RunState.Running => "● 思考中…",
        RunState.Stopped => "⏸ 已停止（可点恢复）",
        _ => "",
    };

    private string AgentStatusText()
    {
        string backend = _agent.Backend switch
        {
            "api" => $"外部 API（{_config.ApiModel}）",
            "llm" => $"本地 Ollama（{_config.OllamaModel}）",
            "local" => "纯本地",
            _ => !string.IsNullOrWhiteSpace(_config.ApiKey)
                ? $"自动 · API（{_config.ApiModel}）"
                : "自动 · 本地 Ollama",
        };
        string runMode = _agent.RunMode switch
        {
            "chat" => "chat · 聊天（可用网络搜索 / 深度研究）",
            "boom" => "boom · 全自动（命令免确认）",
            _ => "work · 命令需确认",
        };
        string tools = _agent.RunMode == "chat"
            ? "工具：网络搜索 / 深度研究"
            : "工具：命令 / Python / 生成图片 / 看图 / 抓取网页 / 网络搜索 / 深度研究 / 读写文件";
        string search = _agent.WebSearchEnabled ? "🌐 网络搜索开" : "网络搜索关";
        string fast = _agent.FastMode ? "⚡ 极速" : "";
        return $"模式：{runMode}　后端：{backend}　{search}　🧠 记忆开　{fast}　{tools}";
    }

    /// <summary>推一条状态事件（kind 0 = AI 助手 / 2 = 集群）。</summary>
    private void EmitStatus(int kind)
    {
        var conv = kind == 2 ? _clusterCur : _agentCur;
        if (conv == null) return;
        var st = kind == 2 ? GetClusterState(conv.Sid) : GetAgentState(conv.Sid);
        Emit(new
        {
            ev = "status",
            kind,
            conv = conv.Sid,
            state = StateName(st),
            busy = kind == 2 ? ClusterBusy(conv.Sid) : AgentBusy(conv.Sid),
            sendLabel = SendLabel(st),
            canRegenerate = st == RunState.Idle,
            status = kind == 2 ? ClusterStatusText() : AgentStatusText(),
        });
    }

    // ------------------------------------------------------------------
    // 开关
    // ------------------------------------------------------------------

    private void SetAgentMode(string mode)
    {
        _agent.RunMode = mode is "chat" or "boom" ? mode : "work";
        Emit(new { ev = "mode", mode = _agent.RunMode });
        EmitStatus(0);
    }

    private void SetFast(bool on)
    {
        _agent.FastMode = on;
        _clusterAgent.FastMode = on;
        Emit(new { ev = "fast", on });
        EmitStatus(0);
        EmitStatus(2);
    }

    private void SetLocal(bool on)
    {
        if (on)
        {
            _agentBackendBefore = _agent.Backend;
            _agent.Backend = "local";
            _clusterAgent.Backend = "local";
        }
        else
        {
            _agent.Backend = _agentBackendBefore;
            _clusterAgent.Backend = _agentBackendBefore;
        }
        Emit(new { ev = "local", on });
        EmitStatus(0);
        EmitStatus(2);
    }

    private void SetSearch(bool on)
    {
        _agent.WebSearchEnabled = on;
        _clusterAgent.WebSearchEnabled = on;
        _config.WebSearch = on;
        _config.Save();
        Emit(new { ev = "search", on });
        EmitStatus(0);
        EmitStatus(2);
    }

    // ------------------------------------------------------------------
    // Agent 事件
    // ------------------------------------------------------------------

    private void OnAgentMessage(int sid, AgentMessage m)
    {
        lock (_gate)
        {
            Conversation? conv = _agentConvs.FirstOrDefault(c => c.Sid == sid);
            if (conv == null) return;
            // 若上一轮流式残留了气泡（如工具调用前说了半句话），先清掉再展示正式消息
            if (_streamItems.TryGetValue(sid, out var streamedItem) && conv.Items.Contains(streamedItem))
                RemoveRaw(conv, streamedItem);
            _streamItems.Remove(sid);
            if (m.Role == "tool") RemoveAgentWorking(sid);

            bool isUser = m.Role == "user";
            bool isTool = m.Role == "tool";
            bool isToolCallNotice = !isUser && !isTool && !string.IsNullOrEmpty(m.Meta);
            bool looksLikeToolJson = !isUser && !isTool && !isToolCallNotice &&
                                     m.Content.Contains("\"tool\"", StringComparison.Ordinal);

            // 看图识别结果：作为 AI 气泡展示（带图片 + 识别文字），而不是系统消息
            bool isVisionResult = isTool && m.Meta == Agent.ToolVision;
            string? visionImagePath = null;
            string visionText = m.Content;
            if (isVisionResult)
            {
                foreach (var ln in m.Content.Split('\n'))
                    if (ln.StartsWith("图片：", StringComparison.Ordinal))
                        visionImagePath = ln["图片：".Length..].Trim();
                int mark = m.Content.IndexOf("识别结果：", StringComparison.Ordinal);
                string result = mark >= 0 ? m.Content[(mark + "识别结果：".Length)..].Trim() : m.Content;
                visionText = string.IsNullOrWhiteSpace(result) ? m.Content : result;
            }

            bool isToolCard = isTool && !isVisionResult;
            var item = new ChatItem
            {
                IsSys = false,
                IsSelf = isUser,
                CanAccept = false,
                ToolName = isToolCard ? m.Meta : "",
                ToolSummary = isToolCard ? ToolCardSummary(m.Meta, m.Content) : "",
                ToolDetail = isToolCard ? m.Content : "",
                Text = isToolCard ? m.Content
                    : isVisionResult ? visionText
                    : isToolCallNotice ? m.Meta
                    : looksLikeToolJson ? "（AI 的工具调用 JSON 未解析成功，已忽略，正在继续…）"
                    : m.Content,
                Meta = isTool ? (isVisionResult ? "看图 · 识别结果" : "工具") : (isUser ? "你" : "AI"),
                AvatarText = "AI",
                SelfAvatarText = "你",
                AvatarColor = "#4A90D9",
                ImagePath = isVisionResult ? visionImagePath : m.ImagePath,
                SessionId = sid,
                TimeStr = Now(),
                ShowTime = ShouldShowTime(),
            };
            conv.Items.Add(item);
            Emit(new { ev = "add", kind = 0, conv = sid, item = ItemJson(item) });
            // 工具结果回灌后模型仍要继续决策 → 在最下面重新显示「思考中」
            if (isTool && GetAgentState(sid) == RunState.Running) AddAgentThinking(sid);
            if (m.Role == "user") MaybeAutoTitle(conv, m.Content);
            Emit(new { ev = "convs", tab = _tab, convs = ConvListJson(_tab) });
            ScheduleSave();
        }
    }

    /// <summary>真·流式输出：增量文本实时追加到 AI 气泡（同一 id 反复下发全量文本，前端直接替换）。</summary>
    private void OnAgentDelta(int sid, string delta)
    {
        lock (_gate)
        {
            var conv = _agentConvs.FirstOrDefault(c => c.Sid == sid);
            if (conv == null) return;
            if (_agentThinking.TryGetValue(sid, out var th) && conv.Items.Contains(th))
            {
                RemoveRaw(conv, th);
                _agentThinking.Remove(sid);
            }
            if (!_streamItems.TryGetValue(sid, out var item) || !conv.Items.Contains(item))
            {
                item = new ChatItem
                {
                    IsSelf = false,
                    CanAccept = false,
                    Text = "",
                    Meta = "AI",
                    AvatarText = "AI",
                    SelfAvatarText = "你",
                    AvatarColor = "#4A90D9",
                    SessionId = sid,
                    TimeStr = Now(),
                    ShowTime = ShouldShowTime(),
                };
                _streamItems[sid] = item;
                conv.Items.Add(item);
                Emit(new { ev = "add", kind = 0, conv = sid, item = ItemJson(item) });
            }
            item.Text += delta;
            Emit(new { ev = "delta", kind = 0, conv = sid, id = item.Id, text = item.Text });
        }
    }

    /// <summary>工具开始执行时，在用户提问下方显示「正在…」状态气泡。</summary>
    private void OnAgentToolStarted(int sid, string tool)
    {
        lock (_gate)
        {
            var conv = _agentConvs.FirstOrDefault(c => c.Sid == sid);
            if (conv == null) return;
            RemoveAgentWorking(sid);
            // 「深度研究·正在搜索 2/4」这类带进度的工具名：直接把进度文字当状态显示
            string label = tool.StartsWith(Agent.ToolResearch + "·", StringComparison.Ordinal)
                ? tool[(Agent.ToolResearch.Length + 1)..]
                : tool switch
            {
                Agent.ToolRunCommand => "正在运行命令",
                Agent.ToolPython => "正在执行 Python 脚本",
                Agent.ToolImage => "正在生成图片",
                Agent.ToolVision => "正在识别图片",
                Agent.ToolSearch => "正在网络搜索",
                Agent.ToolResearch => "正在进行深度研究",
                Agent.ToolFetch => "正在抓取网页",
                Agent.ToolOpenFile => "正在打开文件",
                Agent.ToolReadFile => "正在读取文件",
                Agent.ToolWriteFile => "正在写入文件",
                Agent.ToolRemember => "正在记录记忆",
                Agent.ToolForget => "正在更新记忆",
                _ => $"正在执行「{tool}」",
            };
            var item = new ChatItem
            {
                IsSelf = false,
                Thinking = true,
                CanAccept = false,
                Text = label,
                Meta = "工具",
                AvatarText = "AI",
                SelfAvatarText = "你",
                AvatarColor = "#4A90D9",
                SessionId = sid,
                TimeStr = Now(),
                ShowTime = false,
            };
            _agentWorking[sid] = item;
            conv.Items.Add(item);
            Emit(new { ev = "add", kind = 0, conv = sid, item = ItemJson(item) });
        }
    }

    /// <summary>
    /// 工具结果卡折叠时显示的一句话：搜到几篇 / 第一行是什么。
    /// 展开后的明细就是工具原始返回，所以折叠态必须短、能一眼看出干了什么。
    /// </summary>
    private static string ToolCardSummary(string tool, string result)
    {
        if (tool == Agent.ToolSearch)
        {
            int n = Regex.Matches(result ?? "", @"(?m)^\s*\d+[.、]\s").Count;
            return n > 0 ? $"搜到 {n} 篇资料（点开看标题 / 链接 / 摘要）" : "网络搜索 · 没有搜到结果";
        }
        if (tool == Agent.ToolResearch)
            return "深度研究报告已生成（点开看正文与来源）";
        string first = "";
        foreach (string ln in (result ?? "").Split('\n'))
            if (!string.IsNullOrWhiteSpace(ln)) { first = ln.Trim(); break; }
        if (first.Length > 78) first = first[..78] + "…";
        return first.Length > 0 ? first : $"已执行「{tool}」";
    }

    /// <summary>技能调用链：技能被自动匹配 / 补救启用时，在对应会话里显示一条系统提示。</summary>
    private void OnSkillUsed(int sid, string name)
    {
        lock (_gate)
        {
            var agentConv = _agentConvs.FirstOrDefault(c => c.Sid == sid);
            if (agentConv != null)
            {
                var item0 = new ChatItem
                {
                    IsSys = true,
                    Text = $"🧩 技能调用链：自动匹配并启用「{name}」",
                    SessionId = sid,
                    TimeStr = Now(),
                    ShowTime = ShouldShowTime(),
                };
                agentConv.Items.Add(item0);
                Emit(new { ev = "add", kind = 0, conv = sid, item = ItemJson(item0) });
                ScheduleSave();
                return;
            }
            var clusterConv = _clusterConvs.FirstOrDefault(c => c.Sid == sid);
            if (clusterConv != null)
            {
                var item1 = new ChatItem
                {
                    IsSys = true,
                    Text = $"🧩 技能调用链：自动匹配并启用「{name}」",
                    SessionId = sid,
                    TimeStr = Now(),
                    ShowTime = ShouldShowTime(),
                };
                clusterConv.Items.Add(item1);
                Emit(new { ev = "add", kind = 2, conv = clusterConv.Sid, item = ItemJson(item1) });
                ScheduleSave();
            }
        }
    }

    // ------------------------------------------------------------------
    // 会话内小助手
    // ------------------------------------------------------------------

    private void AddAgentSys(int sid, string text)
    {
        var conv = _agentConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv == null) return;
        var item = new ChatItem { IsSys = true, Text = text, SessionId = sid, TimeStr = Now(), ShowTime = ShouldShowTime() };
        conv.Items.Add(item);
        Emit(new { ev = "add", kind = 0, conv = sid, item = ItemJson(item) });
    }

    private void AddAgentThinking(int sid)
    {
        var conv = _agentConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv == null) return;
        if (_agentThinking.TryGetValue(sid, out var prev) && conv.Items.Contains(prev))
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
            AvatarColor = "#4A90D9",
            SessionId = sid,
            TimeStr = Now(),
            ShowTime = false,
        };
        _agentThinking[sid] = item;
        conv.Items.Add(item);
        Emit(new { ev = "add", kind = 0, conv = sid, item = ItemJson(item) });
    }

    private void RemoveAgentThinking(int sid)
    {
        if (!_agentThinking.TryGetValue(sid, out var item)) return;
        var conv = _agentConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv != null && conv.Items.Contains(item)) RemoveRaw(conv, item);
        _agentThinking.Remove(sid);
    }

    private void RemoveAgentWorking(int sid)
    {
        if (!_agentWorking.TryGetValue(sid, out var item)) return;
        var conv = _agentConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv != null && conv.Items.Contains(item)) RemoveRaw(conv, item);
        _agentWorking.Remove(sid);
    }

    /// <summary>从会话里撤掉一条气泡并通知前端（按 id 删，前端不用重绘整页）。</summary>
    private void RemoveRaw(Conversation conv, ChatItem item)
    {
        if (!conv.Items.Remove(item)) return;
        Emit(new { ev = "msgRemove", kind = conv.Kind, conv = conv.Sid, id = item.Id });
    }

    // ------------------------------------------------------------------
    // 权限确认（work 模式执行命令前）
    // ------------------------------------------------------------------

    private async Task<bool> ConfirmCommandAsync(int sid, string detail)
    {
        var conv = _agentConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv?.AutoApprove == true) return true;   // 已提权：本对话内直接放行

        string askId = Guid.NewGuid().ToString("N")[..8];
        var tcs = new TaskCompletionSource<AskResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _asks[askId] = tcs;
        Emit(new
        {
            ev = "ask",
            askId,
            conv = sid,
            title = "允许执行命令？",
            detail,
            check = "本次对话内始终允许执行命令/脚本（提高权限）",
            yes = "允许",
            no = "拒绝",
        });
        AskResult res;
        try { res = await tcs.Task.ConfigureAwait(false); }
        finally { _asks.Remove(askId); }
        if (res.Yes && res.Remember && conv != null)
        {
            conv.AutoApprove = true;
            ScheduleSave();
        }
        return res.Yes;
    }

    private void Answer(string askId, bool yes, bool remember)
    {
        if (_asks.TryGetValue(askId, out var tcs))
            tcs.TrySetResult(new AskResult { Yes = yes, Remember = remember });
        Emit(new { ev = "askClose", askId });
    }
    // ------------------------------------------------------------------
    // 发送 / 停止 / 恢复 / 重新生成
    // ------------------------------------------------------------------

    /// <summary>发送（三态）：空闲=发送，运行中=停止，已停止=没输入则恢复、有输入则当新一轮。</summary>
    private async Task SendAsync(int kind, string text, string target)
    {
        text = (text ?? "").Trim();
        if (kind == 2)
        {
            int csid = _clusterCur.Sid;
            switch (GetClusterState(csid))
            {
                case RunState.Running:
                    StopCluster(csid);
                    return;
                case RunState.Stopped:
                    if (text.Length > 0)
                    {
                        SetClusterState(csid, RunState.Idle);
                        await RunClusterCoreAsync(csid, text, true, null).ConfigureAwait(false);
                        return;
                    }
                    await ResumeClusterAsync(csid).ConfigureAwait(false);
                    return;
                default:
                    if (text.Length == 0) return;
                    await RunClusterCoreAsync(csid, text, true, null).ConfigureAwait(false);
                    return;
            }
        }

        int sid = _agentCur.Sid;
        switch (GetAgentState(sid))
        {
            case RunState.Running:
                StopAgent(sid);
                return;
            case RunState.Stopped:
                // 中止后如果发了新消息：当作全新一轮，不并入旧任务的恢复
                if (text.Length > 0)
                {
                    SetAgentState(sid, RunState.Idle);
                    _agent.TruncateToLastUser(sid);   // 清掉被中止轮的残留上下文
                    await RunAgentAsync(sid, text).ConfigureAwait(false);
                    return;
                }
                await ResumeAgentAsync(sid).ConfigureAwait(false);
                return;
            default:
                if (text.Length == 0) return;
                await RunAgentAsync(sid, text).ConfigureAwait(false);
                return;
        }
    }

    private void StopRun(int kind)
    {
        if (kind == 2) StopCluster(_clusterCur.Sid);
        else StopAgent(_agentCur.Sid);
    }

    private async Task ResumeAsync(int kind)
    {
        if (kind == 2) await ResumeClusterAsync(_clusterCur.Sid).ConfigureAwait(false);
        else await ResumeAgentAsync(_agentCur.Sid).ConfigureAwait(false);
    }

    private async Task RegenerateAsync(int kind)
    {
        if (kind == 2) { await ClusterRegenerateAsync().ConfigureAwait(false); return; }
        int sid = _agentCur.Sid;
        if (GetAgentState(sid) != RunState.Idle) return;
        var conv = _agentCur;
        int lastUser = -1;
        for (int i = conv.Items.Count - 1; i >= 0; i--)
            if (conv.Items[i].IsSelf && !conv.Items[i].IsSys) { lastUser = i; break; }
        if (lastUser < 0) return;
        while (conv.Items.Count > lastUser + 1)
            conv.Items.RemoveAt(conv.Items.Count - 1);
        Emit(new { ev = "conv", tab = _tab, convs = ConvListJson(_tab), conv = ConvJson(conv), items = conv.Items.Select(ItemJson).ToList(), status = StatusJson(_tab) });
        await RegenerateAgentAsync(sid).ConfigureAwait(false);
    }

    private void StopAgent(int sid)
    {
        if (_agentCts.TryGetValue(sid, out var cts)) cts.Cancel();
    }

    private async Task RunAgentAsync(int sid, string text)
    {
        SetAgentState(sid, RunState.Running);
        AddAgentThinking(sid);
        var cts = new CancellationTokenSource();
        _agentCts[sid] = cts;
        try
        {
            await _agent.RunAsync(sid, text, cts.Token).ConfigureAwait(false);
            if (_agent.LastRunStopped)
            {
                SetAgentState(sid, RunState.Stopped);
                AddAgentSys(sid, "⏸ 已停止，可点「恢复」从断点继续。");
            }
            else
            {
                SetAgentState(sid, RunState.Idle);
            }
        }
        catch (OperationCanceledException)
        {
            SetAgentState(sid, RunState.Stopped);
            AddAgentSys(sid, "⏸ 已停止，可点「恢复」从断点继续。");
        }
        catch (Exception ex)
        {
            AddAgentSys(sid, "AI 助手出错：" + ex.Message);
            SetAgentState(sid, RunState.Idle);
        }
        finally
        {
            _agentCts.Remove(sid);
            _streamItems.Remove(sid);
            RemoveAgentThinking(sid);
            RemoveAgentWorking(sid);
            EmitStatus(0);
            ScheduleSave();
        }
    }

    private async Task ResumeAgentAsync(int sid)
    {
        SetAgentState(sid, RunState.Running);
        AddAgentThinking(sid);
        var cts = new CancellationTokenSource();
        _agentCts[sid] = cts;
        try
        {
            await _agent.ResumeAsync(sid, cts.Token).ConfigureAwait(false);
            if (_agent.LastRunStopped)
            {
                SetAgentState(sid, RunState.Stopped);
            }
            else
            {
                SetAgentState(sid, RunState.Idle);
                AddAgentSys(sid, "✅ 已从断点完成。");
            }
        }
        catch (OperationCanceledException)
        {
            SetAgentState(sid, RunState.Stopped);
        }
        catch (Exception ex)
        {
            AddAgentSys(sid, "AI 助手出错：" + ex.Message);
            SetAgentState(sid, RunState.Idle);
        }
        finally
        {
            _agentCts.Remove(sid);
            _streamItems.Remove(sid);
            RemoveAgentThinking(sid);
            RemoveAgentWorking(sid);
            EmitStatus(0);
            ScheduleSave();
        }
    }

    private async Task RegenerateAgentAsync(int sid)
    {
        SetAgentState(sid, RunState.Running);
        AddAgentThinking(sid);
        var cts = new CancellationTokenSource();
        _agentCts[sid] = cts;
        try
        {
            await _agent.RerunLastAsync(sid, cts.Token).ConfigureAwait(false);
            if (_agent.LastRunStopped) SetAgentState(sid, RunState.Stopped);
            else SetAgentState(sid, RunState.Idle);
        }
        catch (OperationCanceledException)
        {
            SetAgentState(sid, RunState.Stopped);
        }
        catch (Exception ex)
        {
            AddAgentSys(sid, "重新生成出错：" + ex.Message);
            SetAgentState(sid, RunState.Idle);
        }
        finally
        {
            _agentCts.Remove(sid);
            _streamItems.Remove(sid);
            RemoveAgentThinking(sid);
            RemoveAgentWorking(sid);
            EmitStatus(0);
            ScheduleSave();
        }
    }
}
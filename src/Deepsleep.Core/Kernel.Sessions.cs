using System;
using System.Collections.Generic;
using System.Linq;

namespace TrollWrangler.Core;

public sealed partial class Kernel
{
    /// <summary>推 Session 索引（可按 kind / state 过滤）。前端与 Router、Watchdog 共用这一份。</summary>
    private void EmitSessions(string? kind, string? state)
    {
        var list = Sessions.List(
            string.IsNullOrWhiteSpace(kind) ? null : kind,
            string.IsNullOrWhiteSpace(state) ? null : state);
        Emit(new
        {
            ev = "sessions",
            items = list.Select(e => (object)new
            {
                sid = e.Sid,
                kind = e.Kind,
                parentSid = e.ParentSid,
                title = e.Title,
                state = e.State,
                messageCount = e.MessageCount,
                createdUtc = e.CreatedUtc.ToString("O"),
                updatedUtc = e.UpdatedUtc.ToString("O"),
                summary = e.Summary,
            }).ToList(),
        });
    }

    /// <summary>推单个 Session 的完整消息，供"查看会话上下文"用。</summary>
    private void EmitSession(int sid)
    {        var r = Sessions.Load(sid);
        if (r == null)
        {
            Emit(new { ev = "session", sid, found = false });
            return;
        }
        Emit(new
        {
            ev = "session",
            sid,
            found = true,
            record = new
            {
                sid = r.Sid,
                kind = r.Kind,
                parentSid = r.ParentSid,
                title = r.Title,
                state = r.State,
                summary = r.Summary,
                createdUtc = r.CreatedUtc.ToString("O"),
                updatedUtc = r.UpdatedUtc.ToString("O"),
                messages = r.Messages.Select(m => (object)new
                {
                    role = m.Role, content = m.Content, meta = m.Meta,
                    imagePath = m.ImagePath, videoPath = m.VideoPath,
                }).ToList(),
            },
        });
    }

    // ------------------------------------------------------------------
    // 第 2 期：Router / Worker 会话树
    // ------------------------------------------------------------------

    /// <summary>
    /// 推会话树：Router（路由层）→ Worker（上下文隔离的执行会话）。
    /// 界面「设置 → SuperAgent 架构」直接用这份数据画树；Watchdog（第 4 期）也读同一份索引。
    /// </summary>
    private void EmitSessionTree()
    {
        var routers = Sessions.List("router");
        var items = new List<object>();
        foreach (var r in routers.Take(20))
        {
            var workers = Sessions.List("worker", null, r.Sid);
            items.Add(new
            {
                sid = r.Sid,
                parentSid = r.ParentSid,
                title = r.Title,
                state = r.State,
                summary = r.Summary,
                createdUtc = r.CreatedUtc.ToString("O"),
                updatedUtc = r.UpdatedUtc.ToString("O"),
                workers = workers.Select(w => (object)new
                {
                    sid = w.Sid,
                    role = w.Title,
                    state = w.State,
                    summary = w.Summary,
                    messages = w.MessageCount,
                }).ToList(),
            });
        }
        // 顺手统计：用户能一眼看出这套架构到底有没有在干活
        Emit(new
        {
            ev = "sessionTree",
            routers = items,
            stats = new
            {
                routerCount = Sessions.List("router").Count,
                workerCount = Sessions.List("worker").Count,
                activeWorkers = Sessions.List("worker", "active").Count,
                stoppedWorkers = Sessions.List("worker", "stopped").Count,
                mainCount = Sessions.List("main").Count,
            },
        });
    }

    /// <summary>路由诊断：对一段任务文本跑一遍判定，把「为什么拆 / 拆几路」原样告诉用户。</summary>
    private void EmitRouteDecision(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Emit(new { ev = "routeDecision", error = "请给一段任务文本再诊断。" });
            return;
        }
        var d = AgentRouter.Decide(text, _agent.RunMode);
        Emit(new
        {
            ev = "routeDecision",
            text,
            path = d.Path == RoutePath.Slow ? "slow" : "fast",
            workers = d.Workers,
            roles = d.Roles,
            reasons = d.Reasons,
            confidence = d.Confidence,
            explain = d.ToText(),
        });
    }

    /// <summary>
    /// 上下文用量估算：把当前会话里会进模型的内容（正文 + 工具产出）按中英文字符折算成 token，
    /// 再除以模型窗口。不是精确计数（各家 tokenizer 不同），但足够让用户看出"快满了"。
    /// </summary>
    private void EmitCtxUsage()
    {
        var conv = _agentCur;
        long chars = 0, cjk = 0;
        int msgs = 0;
        if (conv != null)
        {
            foreach (var it in conv.Items)
            {
                string t = (it.Text ?? "") + "\n" + (it.ToolDetail ?? "");
                chars += t.Length;
                foreach (char c in t) if (c >= 0x2E80) cjk++;
                if (!string.IsNullOrWhiteSpace(it.Text)) msgs++;
            }
        }
        // 中文 1 字≈1 token，英文/符号 ≈4 字符 1 token
        long tokens = cjk + (chars - cjk) / 4;
        string model = _config.UseOllama ? _config.OllamaModel : _config.ApiModel;
        int auto = ContextWindowFor(model);
        int overrideWin = _config.CtxWindowOverride;
        int window = overrideWin > 0 ? overrideWin : auto;
        double percent = window > 0 ? Math.Min(100.0, tokens * 100.0 / window) : 0;
        Emit(new
        {
            ev = "ctxUsage",
            used = tokens,
            window,
            autoWindow = auto,        // 模型推断值，面板里用来对比"手动指定"改了多少
            overrideWindow = overrideWin,
            percent = Math.Round(percent, 1),
            messages = msgs,
            model,
        });
    }

    /// <summary>
    /// 手动指定上下文窗口（0 = 恢复跟随模型）。用量环立刻按新上限重算。
    /// 上限做了钳制：小于 4k 没意义，大于 8M 只可能是手滑。
    /// </summary>
    private void SetCtxWindow(int window)
    {
        int w = Math.Max(0, Math.Min(8_000_000, window));
        if (w > 0 && w < 4096) w = 4096;
        _config.CtxWindowOverride = w;
        _config.Save();
        EmitCtxUsage();
    }

    /// <summary>模型窗口：看名字里的 32k / 64k / 128k / 200k / 1m；认不出按 128k，本地模型按 32k 保守估。</summary>
    private int ContextWindowFor(string model)
    {
        string m = (model ?? "").ToLowerInvariant();
        try
        {
            var mt = System.Text.RegularExpressions.Regex.Match(m, @"(\d+(?:\.\d+)?)\s*([km])\b");
            if (mt.Success)
            {
                double n = double.Parse(mt.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                int unit = mt.Groups[2].Value == "m" ? 1000000 : 1000;
                int w = (int)(n * unit);
                if (w >= 8000) return w;
            }
        }
        catch { }
        return _config.UseOllama ? 32768 : 128000;
    }

    /// <summary>
    /// 启动时把 Router / Worker 的 sid 计数器推到已有最大值之后。
    /// 不这么做的话，重启后新 Router 会复用旧 sid，把上一条会话的记录覆盖掉。
    /// </summary>
    private void SyncSessionSidCounters()
    {
        try
        {
            foreach (var e in Sessions.List("router"))
                if (e.Sid >= _nextRouterSid) _nextRouterSid = e.Sid + 1;
            foreach (var e in Sessions.List("worker"))
                if (e.Sid >= _nextWorkerSid) _nextWorkerSid = e.Sid + 1;
        }
        catch { /* 索引读不出来就按默认段起步，最多浪费几个 id */ }
    }
}

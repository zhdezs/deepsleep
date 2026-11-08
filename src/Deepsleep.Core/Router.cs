using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TrollWrangler.Core;

/// <summary>路由判定的两条路径。</summary>
public enum RoutePath
{
    /// <summary>快路径：单一路径，主助手直接做，不额外起 Worker（短、单意图的请求走这里）。</summary>
    Fast,
    /// <summary>慢路径：拆给多个上下文隔离的 Worker 并行做，再由 Router 汇总。</summary>
    Slow,
}

/// <summary>Router 的路由决策（可解释：为什么拆 / 拆几路 / 每路什么角色）。</summary>
public sealed class RouteDecision
{
    public RoutePath Path { get; set; } = RoutePath.Fast;
    public int Workers { get; set; }
    public List<string> Roles { get; set; } = new();
    /// <summary>判定依据（人话），界面与日志直接用这条，不做二次解释。</summary>
    public List<string> Reasons { get; set; } = new();
    /// <summary>0-1，规则命中越多越确定；低于 0.5 时不擅自拆分，交回主助手。</summary>
    public double Confidence { get; set; }

    public bool ShouldSplit => Path == RoutePath.Slow && Workers >= 2;

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.Append(Path == RoutePath.Slow ? "慢路径（拆分并行）" : "快路径（单个助手直做）");
        if (Path == RoutePath.Slow && Workers > 0) sb.Append("，规划 " + Workers + " 路 Worker");
        sb.Append("，置信度 ").Append(Confidence.ToString("0.00"));
        if (Reasons.Count > 0)
        {
            sb.AppendLine();
            foreach (string r in Reasons) sb.AppendLine("· " + r);
        }
        if (Roles.Count > 0) sb.AppendLine("角色：" + string.Join(" / ", Roles));
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Router 持有的共享上下文（对应 SuperAgent 架构里 Router 的「共享上下文」职责）。
/// <para>
/// 只放**可以共享**的东西：全局目标、权限边界、约束、同批角色、已产出的产物路径、上一轮的组织记忆。
/// Worker 的私有对话历史绝不进这里 —— Worker 之间靠这份摘要协作，而不是互相偷看上下文。
/// </para>
/// </summary>
public sealed class SharedContext
{
    public string Goal { get; set; } = "";
    /// <summary>chat / work / boom，决定所有 Worker 的权限上限（子智能体不能越权）。</summary>
    public string Mode { get; set; } = "work";
    public List<string> Constraints { get; set; } = new();
    public List<string> Roles { get; set; } = new();
    public List<string> Artifacts { get; set; } = new();
    /// <summary>同一会话里上一次 Router 跑完留下的组织记忆（摘要），新任务接着用。</summary>
    public string PriorMemory { get; set; } = "";

    /// <summary>发给每个 Worker 的共享简报：它们看不到主对话，这份就是它们的"公共知识"。</summary>
    public string ToBrief()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 共享上下文（由 Router 下发）");
        sb.AppendLine("全局目标：" + (string.IsNullOrWhiteSpace(Goal) ? "（见你自己的子任务）" : Goal.Trim()));
        sb.AppendLine("权限：" + (Mode == "chat"
            ? "只读（chat）——禁止写文件、改系统、执行会改动电脑的命令。"
            : Mode == "work"
                ? "可执行，但涉及不可逆操作要谨慎；没有人为你确认，拿不准就只做只读部分并说明。"
                : "全自动（boom）——可以做完整改动，产物要真实落在磁盘上。"));
        if (Constraints.Count > 0)
        {
            sb.AppendLine("约束：");
            foreach (string c in Constraints) sb.AppendLine("- " + c);
        }
        if (Roles.Count > 1)
            sb.AppendLine("同批并行角色（各做各的，别重复别人的活，也不要等别人）：" + string.Join(" / ", Roles));
        if (Artifacts.Count > 0)
        {
            sb.AppendLine("已确认的产物路径（可直接复用，别重复生成）：");
            foreach (string a in Artifacts.Take(12)) sb.AppendLine("- " + a);
        }
        if (!string.IsNullOrWhiteSpace(PriorMemory))
            sb.AppendLine("同会话历史结论（仅作背景，不要照抄）：" + OneLine(PriorMemory, 400));
        return sb.ToString().TrimEnd();
    }

    /// <summary>落进 Session.Summary 的压缩版（Router 只读这个，不碰 Worker 私有历史）。</summary>
    public string ToSummary()
    {
        var sb = new StringBuilder();
        sb.Append("目标：").Append(OneLine(Goal, 120));
        if (Roles.Count > 0) sb.Append("；角色：").Append(string.Join("/", Roles));
        if (Artifacts.Count > 0) sb.Append("；产物：").Append(string.Join(" , ", Artifacts.Take(6).Select(a => OneLine(a, 60))));
        return sb.ToString();
    }

    private static string OneLine(string s, int max)
    {
        string t = Regex.Replace(s ?? "", @"\s+", " ").Trim();
        return t.Length > max ? t.Substring(0, max) + "…" : t;
    }
}

/// <summary>
/// 第 2 期：Router（共享上下文 + 路由决策）。
/// <para>
/// 判定是**确定性规则**，不额外花一次模型调用 —— 路由本身要在毫秒级出结果，
/// 而且规则可解释（用户能看见"为什么拆成 3 路"），失败时也有确定的回退（不拆）。
/// 真正需要语义理解的部分仍然交给主助手：它自己调「拆分任务」时，Router 只负责
/// 分配 Worker 会话、下发共享上下文、收口结果与状态。
/// </para>
/// </summary>
public static class AgentRouter
{
    /// <summary>一次最多几路（与「拆分任务」执行器的上限一致）。</summary>
    public const int MaxWorkers = 5;

    private static readonly string[] MultiSignals =
        { "同时", "并且", "另外", "分别", "顺便", "然后再", "接着把", "各自", "并行", "一起做" };

    /// <summary>领域 → 角色名。命中两个以上领域才值得并行，否则一路做完更快。</summary>
    private static readonly (string Key, string Role)[] Domains =
    {
        ("前端", "前端"), ("界面", "前端"), ("样式", "前端"), ("html", "前端"), ("css", "前端"), ("vue", "前端"), ("react", "前端"),
        ("后端", "后端"), ("接口", "后端"), ("api", "后端"), ("服务端", "后端"), ("数据库", "后端"), ("sql", "后端"),
        ("文档", "文档"), ("说明", "文档"), ("readme", "文档"), ("注释", "文档"), ("手册", "文档"),
        ("测试", "测试"), ("用例", "测试"), ("回归", "测试"), ("单测", "测试"),
        ("设计", "设计"), ("配色", "设计"), ("图标", "设计"), ("视觉", "设计"),
        ("调研", "调研"), ("查一下", "调研"), ("对比", "调研"), ("资料", "调研"),
        ("部署", "部署"), ("打包", "部署"), ("发布", "部署"), ("运维", "部署"), ("脚本", "部署"),
        ("数据", "数据"), ("统计", "数据"), ("分析", "数据"), ("报表", "数据"),
    };

    /// <summary>
    /// 给一个任务文本做路由判定。mode 参与判定：chat 模式下会稍保守（只读任务拆分收益低）。
    /// </summary>
    public static RouteDecision Decide(string task, string mode = "work", int minWorkers = 2)
    {
        var d = new RouteDecision();
        string t = (task ?? "").Trim();
        if (t.Length == 0)
        {
            d.Reasons.Add("任务为空，按快路径直做");
            return d;
        }

        double score = 0;
        string lower = t.ToLowerInvariant();

        // ① 多步骤结构：编号列表（1. 2. 3.）说明本来就是多件事
        int numbered = Regex.Matches(t, @"(?m)^\s*(?:\d+[.、)]|[-*]\s)").Count;
        if (numbered >= 2)
        {
            score += 1.5;
            d.Reasons.Add("任务里有 " + numbered + " 条并列步骤");
        }

        // ② 并列信号词
        int signals = MultiSignals.Count(w => lower.Contains(w));
        if (signals >= 1)
        {
            score += 0.8 + Math.Min(0.6, (signals - 1) * 0.3);
            d.Reasons.Add("出现并列/顺序信号词（" + signals + " 处）");
        }

        // ③ 跨领域：不同角色才值得并行
        var roles = Domains.Where(x => lower.Contains(x.Key)).Select(x => x.Role).Distinct().ToList();
        if (roles.Count >= 2)
        {
            score += 0.9 + Math.Min(1.2, (roles.Count - 2) * 0.4);
            d.Reasons.Add("涉及 " + roles.Count + " 个不同领域：" + string.Join(" / ", roles));
        }

        // ④ 体量：长任务通常包含多个子目标
        if (t.Length >= 240)
        {
            score += 0.6;
            d.Reasons.Add("任务描述较长（" + t.Length + " 字），大概率含多个子目标");
        }

        // ⑤ 明确要求并行 / 分工
        if (lower.Contains("并行") || lower.Contains("同时进行") || lower.Contains("分头") || lower.Contains("多线程"))
        {
            score += 1.2;
            d.Reasons.Add("用户明确要求并行/分工");
        }

        d.Confidence = Math.Min(1.0, Math.Round(score / 2.4, 2));

        // chat 模式：只读任务并行收益有限，门槛抬高半档（宁可不拆，也别把纯聊天拆成一堆子智能体）
        double threshold = mode == "chat" ? 2.0 : 1.6;
        if (score < threshold)
        {
            d.Path = RoutePath.Fast;
            d.Workers = 1;
            if (d.Reasons.Count == 0) d.Reasons.Add("短请求、单一意图，单助手直做更快");
            return d;
        }

        d.Path = RoutePath.Slow;
        var picked = roles.Count >= 2 ? roles : new List<string> { "执行", "复核" };
        d.Roles = picked.Take(MaxWorkers).ToList();
        int n = Math.Max(minWorkers, Math.Min(picked.Count, MaxWorkers));
        d.Workers = Math.Max(2, Math.Min(n, MaxWorkers));
        d.Confidence = Math.Max(d.Confidence, 0.6);
        return d;
    }

    /// <summary>把「拆分任务」里模型给定的角色与 Router 的领域识别合并，去重后按原顺序返回。</summary>
    public static List<string> MergeRoles(IEnumerable<string> given, RouteDecision decision)
    {
        var list = new List<string>();
        foreach (string r in given)
        {
            string v = (r ?? "").Trim();
            if (v.Length == 0) continue;
            if (!list.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase))) list.Add(v);
        }
        foreach (string r in decision.Roles)
            if (!list.Any(x => string.Equals(x, r, StringComparison.OrdinalIgnoreCase))) list.Add(r);
        return list;
    }
}

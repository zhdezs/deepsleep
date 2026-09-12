# -*- coding: utf-8 -*-
"""
对话数据分析与辩论知识库学习。

自动学习规则：归档会话时，比较对方首条消息与最后一条消息的攻击强度——
若结束时明显比开始时平和，判定本场策略"有效"，计入知识库。
"""

from .detector import CATEGORY_NAMES
from .strategies import STRATEGIES


def estimated_wasted_seconds(troll_count):
    """估算消耗对方的时间：假设对方输入一条约 60 秒、阅读我方回复约 30 秒。"""
    return troll_count * 90


def format_duration(seconds):
    seconds = max(0, int(seconds))
    h, rem = divmod(seconds, 3600)
    m, s = divmod(rem, 60)
    if h:
        return f"{h} 小时 {m} 分钟"
    if m:
        return f"{m} 分钟 {s} 秒"
    return f"{s} 秒"


def _bar(value, max_value, width=20):
    if not max_value:
        return "─" * width
    filled = max(1, round(width * value / max_value))
    return "█" * filled + "░" * (width - filled)


def report(storage):
    """生成统计报告文本。"""
    stats = storage.stats()
    lines = []
    lines.append("· 处理会话数：%d" % stats["sessions"])
    lines.append("· 我方回复数：%d" % (stats["ai_msgs"] or 0))
    lines.append("· 对方消息数：%d" % (stats["troll_msgs"] or 0))
    lines.append(
        "· 估算已消耗对方时间：%s"
        % format_duration(estimated_wasted_seconds(stats["troll_msgs"] or 0))
    )
    lines.append("")

    lines.append("【攻击类型分布】")
    cats = stats["categories"]
    max_c = max([c["n"] for c in cats] or [0])
    if cats:
        for c in cats:
            name = CATEGORY_NAMES.get(c["category"], c["category"])
            lines.append("  %s：%d 次  %s" % (name, c["n"], _bar(c["n"], max_c)))
    else:
        lines.append("  （暂无数据）")
    lines.append("")

    lines.append("【破防策略使用分布】")
    strs = stats["strategies"]
    max_s = max([s["n"] for s in strs] or [0])
    if strs:
        for s in strs:
            name = STRATEGIES.get(s["strategy"], {}).get("name", s["strategy"])
            lines.append("  %s：%d 次  %s" % (name, s["n"], _bar(s["n"], max_s)))
    else:
        lines.append("  （暂无数据）")
    lines.append("")

    lines.append("【实战话术库（学习的话术，非策略）】")
    try:
        from . import phrase_memory
        ph = phrase_memory.summary()
        lines.append("  · 已沉淀实战话术：%d 段" % ph["n"])
        if ph["categories"]:
            for c, n in sorted(ph["categories"].items(), key=lambda kv: -kv[1]):
                name = CATEGORY_NAMES.get(c, c)
                lines.append("  · %s：%d 段" % (name, n))
        else:
            lines.append("  · （暂无，跑完有效对线后会自动学习，或执行 py -3 train_model.py --learn-phrases）")
    except Exception:
        lines.append("  · （话术库不可用）")
    return "\n".join(lines)


def learn_from_session(storage, sid):
    """归档会话时自动学习：比较首尾攻击强度，更新知识库，并把有效话术沉淀进话术库。

    返回 1 表示本场有效（对方明显降温），0 表示无效/数据不足。
    """
    from . import phrase_memory
    msgs = storage.session_messages(sid)
    if len(msgs) < 2:
        return 0
    win = phrase_memory.win_for_session(storage, sid)
    for i, m in enumerate(msgs):
        if m["role"] == "ai" and m.get("strategy"):
            cat = "general"
            for j in range(i - 1, -1, -1):
                if msgs[j]["role"] == "troll":
                    cat = msgs[j].get("category") or "general"
                    break
            storage.record_knowledge(cat, m["strategy"], win)
    # 话术学习：仅沉淀「实战验证过有效」的回复段落（策略知识库含失败样本，话术只学好招）
    if win:
        phrase_memory.learn_from_session(storage, sid, True)
    return 1 if win else 0

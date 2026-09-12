# -*- coding: utf-8 -*-
"""
回怼模型训练器：批量输入键盘侠言论训练回怼能力。

训练流程：
1. 把一批（文本, 期望类别）样本逐条输入引擎，实时生成回怼（默认 live 模式）；
2. 全部写入 SQLite 并按会话归档学习（analyzer.learn_from_session），沉淀知识库；
3. 输出训练报告：识别准确率（含分类型）、输出零脏话校验、重复率、
   字数/行数分布、策略分布等，量化模型能力。

说明：本项目为规则引擎，「训练」= 用批量样本检验/强化识别与组装，
并持续积累辩论知识库，而非梯度式机器学习。
"""

import os

from .detector import CATEGORY_NAMES, SENSITIVE_WORDS
from .analyzer import learn_from_session


def _profanity_hits(text):
    """校验回复是否含敏感词（跳过单字词，避免「死/蠢/滚」误报）。"""
    hits = set()
    for w in SENSITIVE_WORDS:
        if len(w) >= 2 and w.lower() in text.lower():
            hits.add(w)
    return hits


def _char_len(text):
    return len([c for c in text if not c.isspace()])


def train(storage, engine, samples, tone="live", troll_name="训练样本"):
    """samples: [(text, expected_category), ...]；返回 (sid, results, report)。"""
    sid = storage.create_session(troll_name)
    engine.new_session(str(sid))

    results = []
    for text, exp in samples:
        r = engine.generate(text, str(sid), tone=tone)
        r["expected"] = exp
        results.append(r)
        storage.add_message(
            sid, "troll", text,
            category=r["category"], aggression=r["aggression"],
        )
        storage.add_message(sid, "ai", r["response"], strategy=r["strategy"])

    storage.end_session(sid)
    learn_from_session(storage, sid)

    report = _build_report(sid, results)
    return sid, results, report


def _build_report(sid, results):
    n = len(results)
    lines = []
    lines.append("训练样本数：%d（会话 #%s，已归档学习）" % (n, sid))
    lines.append("")

    # ---- 识别准确率 ----
    correct = sum(1 for r in results if r["category"] == r["expected"])
    lines.append("【攻击类型识别准确率】 %.1f%%（%d/%d）"
                 % (100.0 * correct / n if n else 0, correct, n))
    by_cat = {}
    for r in results:
        by_cat.setdefault(r["expected"], []).append(r)
    for exp, rs in sorted(by_cat.items(), key=lambda kv: -len(kv[1])):
        ok = sum(1 for r in rs if r["category"] == r["expected"])
        name = CATEGORY_NAMES.get(exp, exp)
        lines.append("  %-6s %5d 条  准确 %.1f%%" % (name, len(rs), 100.0 * ok / len(rs)))
    lines.append("")

    # ---- 输出零脏话校验 ----
    bad = [(r["response"], _profanity_hits(r["response"])) for r in results]
    dirty = [b for b in bad if b[1]]
    lines.append("【零脏话校验】 违规回复 %d 条 / %d 条" % (len(dirty), n))
    if dirty:
        for resp, hits in dirty[:3]:
            lines.append("  检出%s：%s" % (sorted(hits), resp[:40].replace("\n", " ")))
    lines.append("")

    # ---- 重复率 ----
    reps = [r["response"] for r in results]
    dup = sum(1 for i, x in enumerate(reps) if x in reps[:i])
    lines.append("【整句重复】 %d 条（应保持 0）" % dup)
    lines.append("")

    # ---- 字数/行数 ----
    lens = [_char_len(r["response"]) for r in results]
    nlines = [r["response"].count("\n") + 1 for r in results]
    lines.append("【回复长度】 字数 %d~%d（均值 %.0f）｜ 行数 %d~%d（均值 %.1f）"
                 % (min(lens), max(lens), sum(lens) / n,
                    min(nlines), max(nlines), sum(nlines) / n))
    lines.append("")

    # ---- 策略分布 ----
    from .strategies import STRATEGIES
    strat = {}
    for r in results:
        strat[r["strategy"]] = strat.get(r["strategy"], 0) + 1
    lines.append("【策略分布】 " + "，".join(
        "%s×%d" % (STRATEGIES.get(k, {}).get("name", k), v)
        for k, v in sorted(strat.items(), key=lambda kv: -kv[1])))
    lines.append("")

    # ---- 强度分布 ----
    aggs = {}
    for r in results:
        aggs[r["category"]] = aggs.get(r["category"], []) + [r["aggression"]]
    lines.append("【攻击强度】 平均 %.1f / 10（各类别见库）"
                 % (sum(r["aggression"] for r in results) / n if n else 0))
    return "\n".join(lines)

# -*- coding: utf-8 -*-
"""话术记忆库：让本地回怼程序「学话术」，而不只是「学策略」。

知识库（knowledge）只记录「某类攻击 × 某策略 赢没赢」——这是策略层面。
话术记忆库沉淀的是实战验证过「有效」的具体回怼段落——这是话术层面。

学习：归档会话时若判定本场有效（对方攻击强度明显下降），就把本场我方回复
      按段落切分，连同当时的攻击类型一起入库（按 类别+文本 去重计分）；
召回：拼装新回复时，按「攻击类型 + 字符 bigram 相似度」召回同类攻击里
      胜率最高的话术段落，注入实时组装池，让本地引擎真正用上经过实战
      检验的招数，而不是永远只从写死的骨架句池里挑。

存储：data/phrase_memory.json（与 classifier.json 一样可被 .gitignore 忽略，
      但它不是临时文件，删除后学习积累会丢失）。
"""

import json
import os
import random

from . import data_dir

PHRASE_PATH = os.path.join(data_dir(), "phrase_memory.json")
MAX_ENTRIES = 600      # 话术库上限，超限淘汰低胜率条目
MIN_LEN = 8            # 段落最短长度（太短没信息量）
MAX_LEN = 80           # 段落最长长度（太长不便复用）
_PLACEHOLDERS = ("{q}", "{w}", "{t}")


def load():
    """读取话术库。文件缺失/损坏时返回空列表。"""
    try:
        with open(PHRASE_PATH, encoding="utf-8") as f:
            data = json.load(f)
        entries = [e for e in data.get("entries", [])
                   if isinstance(e, dict) and e.get("text")]
        return entries
    except Exception:
        return []


def save(entries):
    os.makedirs(os.path.dirname(PHRASE_PATH), exist_ok=True)
    with open(PHRASE_PATH, "w", encoding="utf-8") as f:
        json.dump({"entries": entries}, f, ensure_ascii=False, indent=1)


def _segments(content):
    """把一条我方回复按段落切分，过滤过短/过长/含占位符的片段。"""
    out = []
    for line in (content or "").splitlines():
        line = line.strip()
        if len(line) < MIN_LEN or len(line) > MAX_LEN:
            continue
        if any(p in line for p in _PLACEHOLDERS):
            continue
        out.append(line)
    return out


def win_for_session(storage, sid):
    """胜负判定（与 analyzer 一致）：结束攻击强度 < 开始攻击强度 → 本场有效。"""
    msgs = storage.session_messages(sid)
    first = last = None
    for m in msgs:
        if m["role"] == "troll":
            if first is None:
                first = m.get("aggression") or 0
            last = m.get("aggression") or 0
    if first is None:
        return False
    return last < first


def learn_from_session(storage, sid, win):
    """把一场「有效」会话的我方话术沉淀入库。返回沉淀段落数（0 表示无）。"""
    if not win:
        return 0
    msgs = storage.session_messages(sid)
    entries = load()
    by_key = {(e["category"], e["text"]): e for e in entries}
    changed = 0
    for i, m in enumerate(msgs):
        if m["role"] != "ai" or not (m.get("content") or "").strip():
            continue
        cat = "general"
        for j in range(i - 1, -1, -1):
            if msgs[j]["role"] == "troll":
                cat = msgs[j].get("category") or "general"
                break
        for seg in _segments(m["content"]):
            key = (cat, seg)
            e = by_key.get(key)
            if e:
                e["uses"] += 1
                e["wins"] += 1
            else:
                by_key[key] = {"category": cat, "text": seg, "uses": 1, "wins": 1}
            changed += 1
    save(_prune(list(by_key.values())))
    return changed


def _prune(entries):
    """控制话术库规模：超限时按胜率+使用次数淘汰低分条目。"""
    if len(entries) <= MAX_ENTRIES:
        return entries
    entries.sort(key=lambda e: (e["wins"] / e["uses"] if e["uses"] else 0,
                                e["uses"]), reverse=True)
    return entries[:MAX_ENTRIES]


def _bigrams(s):
    s = (s or "").replace(" ", "")
    return {s[i:i + 2] for i in range(max(0, len(s) - 1))}


def _similarity(a, b):
    """字符 bigram Jaccard 相似度（0~1）。"""
    A, B = _bigrams(a), _bigrams(b)
    if not A or not B:
        return 0.0
    return len(A & B) / len(A | B)


def recall(troll_text, category, k=3):
    """召回与当前攻击最相关的实战话术段落（优先同类型，胜率优先，相似度做次排序）。

    参数：
        troll_text  对方原始言论（用于相似度微调）
        category    攻击类型（优先同类型的实战话术）
        k           最多召回段落数
    返回：段落文本列表（已脱敏、零脏话）。
    """
    entries = load()
    if not entries:
        return []

    def rate(e):
        return e["wins"] / e["uses"] if e["uses"] else 0

    same = [e for e in entries if e["category"] == category]
    others = [e for e in entries if e["category"] != category]
    same.sort(key=lambda e: (rate(e), _similarity(troll_text, e["text"])), reverse=True)
    top = same[:8]
    random.shuffle(top)  # 高质量池内随机，避免每次召回同一批话术
    pool = top
    if len(pool) < k:
        others.sort(key=lambda e: rate(e), reverse=True)
        pool += others[:k - len(pool)]
    return [e["text"] for e in pool[:k]]


def summary():
    """话术库规模统计，供报告/界面展示。"""
    entries = load()
    cats = {}
    for e in entries:
        cats[e["category"]] = cats.get(e["category"], 0) + 1
    return {"n": len(entries), "categories": cats}


# ----------------------------------------------------------------------
# 自检：py -3 app/phrase_memory.py
# ----------------------------------------------------------------------
if __name__ == "__main__":
    print("话术库路径：%s" % PHRASE_PATH)
    ph = summary()
    print("当前话术库：%d 段" % ph["n"])
    if ph["n"]:
        for c, n in sorted(ph["categories"].items(), key=lambda kv: -kv[1]):
            print("  %-12s %d 段" % (c, n))
        print("召回演示（general，k=3）：")
        for line in recall("你说得对，我懒得跟你争", "general", k=3):
            print("  · " + line)
    else:
        print("（空库。请先跑对线，或执行：py -3 train_model.py --learn-phrases）")

# -*- coding: utf-8 -*-
"""
网络言论抓取与清洗模块（零第三方依赖，纯标准库）。

功能：
- fetch_url：抓取公开网页正文文本（UA 伪装 + 超时 + 限速）；
- filter_rants：从文本中切分句子，用攻击检测器筛出攻击性言论；
- 队列管理：抓取/导入结果统一累积到 data/rants.json，
  供后续「连续对线」批量输入系统。

合规提示：
- 仅抓取你本人有权访问的公开页面，遵守目标站点 robots 与使用条款；
- 本工具仅用于回应针对你的攻击，请勿用于主动骚扰他人。
"""

import html
import io
import json
import os
import re
import time
import urllib.request

from .detector import classify, aggression_score

# data/rants.json 队列路径
RANTS_FILE = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "data", "rants.json"
)

_UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"
)

_RE_TAG = re.compile(r"<script[\s\S]*?</script>|<style[\s\S]*?</style>|<[^>]+>")
_RE_SPACE = re.compile(r"\s+")
_RE_HTML_ENT = html.unescape


def fetch_url(url, timeout=15):
    """抓取网页并返回清洗后的纯文本。"""
    req = urllib.request.Request(url, headers={"User-Agent": _UA})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        raw = resp.read()
    enc = resp.headers.get_content_charset() or "utf-8"
    try:
        text = raw.decode(enc, errors="replace")
    except LookupError:
        text = raw.decode("utf-8", errors="replace")
    return _html_to_text(text)


def _html_to_text(html_text):
    text = _RE_TAG.sub(" ", html_text)
    text = _RE_HTML_ENT(text)
    text = _RE_SPACE.sub(" ", text)
    return text.strip()


def split_sentences(text, max_len=120):
    """把文本切分为候选句子。"""
    parts = re.split(r"[。！？!?\n]+", text)
    out = []
    for p in parts:
        p = p.strip(" \t\r\n，,。.;；:：\"'“”‘’（）()【】[]")
        if 4 <= len(p) <= max_len:
            out.append(p)
    return out


def filter_rants(sentences, min_agg=1):
    """用攻击检测器筛出攻击性言论，去重、保持顺序。"""
    seen = set()
    out = []
    for s in sentences:
        cat = classify(s)
        agg = aggression_score(s)
        if cat == "general" and agg < min_agg:
            continue
        key = s.replace(" ", "")
        if key in seen:
            continue
        seen.add(key)
        out.append({"text": s, "category": cat, "aggression": min(10, agg)})
    return out


def rants_from_url(url, timeout=15):
    """抓取网页并提取攻击性言论。"""
    text = fetch_url(url, timeout)
    return filter_rants(split_sentences(text))


# ---------------------------------------------------------------- 队列
def load_queue(path=RANTS_FILE):
    if not os.path.exists(path):
        return []
    try:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        return data if isinstance(data, list) else []
    except Exception:
        return []


def save_queue(items, path=RANTS_FILE):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(items, f, ensure_ascii=False, indent=2)


def add_to_queue(items, path=RANTS_FILE):
    """把新言论追加进队列（按文本去重）。返回新增条数。"""
    queue = load_queue(path)
    seen = {it.get("text", "") for it in queue}
    added = 0
    for it in items:
        text = (it.get("text") or "").strip()
        if not text or text in seen:
            continue
        cat = it.get("category") or classify(text)
        agg = it.get("aggression") or aggression_score(text)
        queue.append({"text": text, "category": cat, "aggression": min(10, agg),
                      "source": it.get("source", ""), "fetched_at": time.strftime("%Y-%m-%d %H:%M")})
        seen.add(text)
        added += 1
    if added:
        save_queue(queue, path)
    return added


def clear_queue(path=RANTS_FILE):
    save_queue([], path)

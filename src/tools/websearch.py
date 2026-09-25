#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""deepsleep 内置联网搜索爬虫（不需要任何 API Key）。

直接抓取搜索引擎结果页并解析标题 / 链接 / 摘要，多引擎依次尝试：
Bing 国内 -> 搜狗 -> 360 -> DuckDuckGo(HTML)，全部失败才算失败。

用法：
    python websearch.py "关键词"                  # 人类可读
    python websearch.py --json --limit 8 "关键词"  # JSON（deepsleep 内核调用）
    python websearch.py --engine bing "关键词"     # 只用指定引擎
"""

import argparse
import gzip
import html as H
import io
import json
import re
import ssl
import sys
import urllib.error
import urllib.parse
import urllib.request

UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
HEADERS = {
    "User-Agent": UA,
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
    "Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
    "Connection": "close",
}

try:
    _CTX = ssl.create_default_context()
    _CTX.check_hostname = False
    _CTX.verify_mode = ssl.CERT_NONE
except Exception:
    _CTX = None


def fetch(url, referer=None, timeout=12):
    """下载网页，自动解 gzip / 识别 charset，返回 (正文, 最终URL)。"""
    headers = dict(HEADERS)
    if referer:
        headers["Referer"] = referer
    req = urllib.request.Request(url, headers=headers)
    resp = urllib.request.urlopen(req, timeout=timeout, context=_CTX)
    raw = resp.read()
    if (resp.headers.get("Content-Encoding") or "").lower() == "gzip":
        try:
            raw = gzip.decompress(raw)
        except Exception:
            pass
    charset = None
    ctype = (resp.headers.get("Content-Type") or "").lower()
    m = re.search(r"charset=([\w-]+)", ctype)
    if m:
        charset = m.group(1)
    text = None
    if charset and charset.lower() not in ("utf-8", "utf8"):
        try:
            text = raw.decode(charset, "ignore")
        except Exception:
            text = None
    if text is None or text.count("\ufffd") > 8:
        text = raw.decode("utf-8", "ignore")
    return text, resp.geturl()


def strip_html(s):
    s = re.sub(r"(?is)<(script|style|noscript)[^>]*>.*?</\1>", " ", s or "")
    s = re.sub(r"(?s)<[^>]+>", " ", s)
    s = H.unescape(s)
    return re.sub(r"\s+", " ", s).strip()


def truncate(s, n):
    return s if len(s) <= n else s[:n] + "..."


def search_host(url):
    try:
        host = urllib.parse.urlsplit(url).netloc.lower()
    except Exception:
        return False
    return any(host.endswith(h) for h in
               ("so.com", "sogou.com", "baidu.com", "bing.com", "duckduckgo.com"))
def parse_pairs(html, link_pat, snippet_pat):
    """按「标题链接 + 摘要」两套正则解析结果页。"""
    out = []
    links = list(re.finditer(link_pat, html or "", re.S | re.I))
    snips = [strip_html(m.group(1)) for m in re.finditer(snippet_pat, html or "", re.S | re.I)]
    for i, m in enumerate(links):
        title = strip_html(m.group(2))
        url = H.unescape(m.group(1)).strip()
        if not title or not url:
            continue
        out.append({"title": title, "url": url,
                    "snippet": snips[i] if i < len(snips) else ""})
    return out


def parse_bing(html):
    return parse_pairs(
        html,
        r'<h2[^>]*>\s*<a[^>]*href="(https?://[^"]+)"[^>]*>(.*?)</a>',
        r'<p[^>]*class="[^"]*b_lineclamp[^"]*"[^>]*>(.*?)</p>')


def parse_sogou(html):
    return parse_pairs(
        html,
        r'<h3[^>]*>\s*<a[^>]*href="([^"]+)"[^>]*>(.*?)</a>',
        r'<p[^>]*class="[^"]*str_info[^"]*"[^>]*>(.*?)</p>')


def parse_so360(html):
    return parse_pairs(
        html,
        r'<h3[^>]*>\s*<a[^>]*href="([^"]+)"[^>]*>(.*?)</a>',
        r'<p[^>]*class="[^"]*res-desc[^"]*"[^>]*>(.*?)</p>')


def parse_ddg(html):
    return parse_pairs(
        html,
        r'<a[^>]*class="result__a"[^>]*href="([^"]+)"[^>]*>(.*?)</a>',
        r'<a[^>]*class="result__snippet"[^>]*>(.*?)</a>')


ENGINES = [
    ("Bing", "https://cn.bing.com/search?q={q}", "https://cn.bing.com", parse_bing),
    ("搜狗", "https://www.sogou.com/web?query={q}", "https://www.sogou.com", parse_sogou),
    ("360", "https://www.so.com/s?q={q}", "https://www.so.com", parse_so360),
    ("DuckDuckGo", "https://html.duckduckgo.com/html/?q={q}", "https://duckduckgo.com", parse_ddg),
]


def normalize(items, origin, limit):
    """补全相对链接、去重、过滤无意义结果。"""
    seen = set()
    out = []
    for it in items:
        url = it["url"]
        if url.startswith("//"):
            url = "https:" + url
        elif url.startswith("/"):
            url = origin + url
        url = re.sub(r"[?&](utm_[^&]*|from|fr|spm|src)=[^&]*", "", url)
        title = it["title"].strip()
        if len(title) < 4:
            continue
        if "百度安全验证" in title or "点击继续访问" in title:
            continue
        key = title[:24]
        if key in seen:
            continue
        seen.add(key)
        out.append({"title": title, "url": url, "snippet": it["snippet"]})
        if len(out) >= limit:
            break
    return out


def needs_resolve(url):
    try:
        parts = urllib.parse.urlsplit(url)
    except Exception:
        return False
    host = parts.netloc.lower()
    if not any(host.endswith(h) for h in ("so.com", "sogou.com", "baidu.com", "duckduckgo.com")):
        return False
    return "link" in parts.path or "uddg=" in parts.query or "url=" in parts.query


_BAD_RESOLVE_HOSTS = set()


def resolve(url, timeout=6):
    """把搜索引擎跳转链接换成真实 URL；失败就原样返回（同一次搜索内不重复试）。"""
    m = re.search(r"[?&]uddg=([^&]+)", url)
    if m:
        dec = urllib.parse.unquote(m.group(1))
        if dec.startswith("http"):
            return dec
    try:
        parts = urllib.parse.urlsplit(url)
        if parts.netloc.lower() in _BAD_RESOLVE_HOSTS:
            return url
        text, final = fetch(url, referer=parts.scheme + "://" + parts.netloc + "/", timeout=timeout)
        if final != url and not search_host(final):
            return final
        m = re.search(r"(?:URL=|location\.(?:replace|href)\s*=\s*['\"])(https?://[^'\"\s>]+)",
                      text or "", re.I)
        if m:
            return H.unescape(m.group(1))
    except Exception:
        try:
            _BAD_RESOLVE_HOSTS.add(urllib.parse.urlsplit(url).netloc.lower())
        except Exception:
            pass
    return url


def search(query, limit=8, engine=None):
    q = urllib.parse.quote(query)
    for name, url_tpl, origin, parser in ENGINES:
        if engine and name.lower() != engine.lower():
            continue
        try:
            text, _ = fetch(url_tpl.format(q=q))
            items = normalize(parser(text), origin, limit)
            if not items:
                continue
            _BAD_RESOLVE_HOSTS.clear()
            for idx, it in enumerate(items):
                if idx < 3 and needs_resolve(it["url"]):
                    it["url"] = resolve(it["url"])
            return {"engine": name, "results": items}
        except Exception:
            continue
    return {"engine": "", "results": []}


def main():
    ap = argparse.ArgumentParser(description="deepsleep 内置联网搜索爬虫")
    ap.add_argument("query", help="搜索关键词")
    ap.add_argument("--json", action="store_true", help="输出 JSON")
    ap.add_argument("--limit", type=int, default=8, help="结果条数上限")
    ap.add_argument("--engine", default=None, help="只用指定引擎（Bing/搜狗/360/DuckDuckGo）")
    args = ap.parse_args()

    result = search(args.query, limit=max(1, min(args.limit, 16)), engine=args.engine)
    if args.json:
        json.dump(result, sys.stdout, ensure_ascii=False)
        sys.stdout.write("\n")
        return 0
    if not result["results"]:
        print("搜索失败：所有引擎都没有返回结果。", file=sys.stderr)
        return 1
    print("搜索结果（%s｜来源：%s）：" % (args.query, result["engine"]))
    for i, it in enumerate(result["results"], 1):
        print("%d. %s" % (i, it["title"]))
        print("   %s" % it["url"])
        if it["snippet"]:
            print("   %s" % truncate(it["snippet"], 160))
    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    sys.exit(main())

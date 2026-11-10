#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 ui/logos/*.svg 打包成 ui/logos.js（window.DSL_LOGOS）。

为什么要内联成 JS 而不是 <img src>：
- 单色 logo（gpt）写的是 fill="currentColor"，只有内联进 DOM 才能跟着主题色变；
- 客户端走 app.local 的 HTTP 缓存，多 14 个请求不如一个脚本省事；
- 内联后可以直接用 CSS 控制尺寸，不用为每张图算比例。

用法：python tools/build-logos.py
"""
import json
import os
import re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "ui", "logos")
OUT = os.path.join(ROOT, "ui", "logos.js")

DROP_ATTRS = [
    r'\s+height="[^"]*"',
    r'\s+width="[^"]*"',
    r'\s+style="[^"]*"',
    r'\s+xmlns="[^"]*"',
    r'\s+xmlns:xlink="[^"]*"',
]


def clean(svg: str, key: str) -> str:
    svg = re.sub(r"<title>.*?</title>", "", svg, flags=re.S)
    for pat in DROP_ATTRS:
        svg = re.sub(pat, "", svg)
    # id 全部加前缀：同一页面里可能有同一个 logo 出现多次（列表 + 药丸 + 头像），
    # 重复 id 会让渐变引用指向第一个，虽然颜色一样，但先避开更稳。
    def rid(m):
        return 'id="dsl-%s-%s"' % (key, m.group(1))
    svg = re.sub(r'id="([^"]+)"', rid, svg)
    svg = re.sub(r'url\(#([^)]+)\)', lambda m: "url(#dsl-%s-%s)" % (key, m.group(1)), svg)
    svg = re.sub(r'(xlink:href|href)="#([^"]+)"',
                 lambda m: '%s="#dsl-%s-%s"' % (m.group(1), key, m.group(2)), svg)
    svg = re.sub(r">\s+<", "><", svg).strip()
    return svg


def main():
    items = []
    for fn in sorted(os.listdir(SRC)):
        if not fn.endswith(".svg"):
            continue
        key = fn[:-4]
        with open(os.path.join(SRC, fn), encoding="utf-8") as f:
            items.append((key, clean(f.read(), key)))
    body = ",\n".join("%s:%s" % (json.dumps(k), json.dumps(v, ensure_ascii=False))
                      for k, v in items)
    js = ("/* 由 tools/build-logos.py 生成，别手改。%d 个品牌图标，源文件在 ui/logos/*.svg。 */\n"
          "window.DSL_LOGOS={%s};\n" % (len(items), body))
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(js)
    print("wrote %s (%d logos, %.1f KB)" % (OUT, len(items), len(js.encode("utf-8")) / 1024))


if __name__ == "__main__":
    main()

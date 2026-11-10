# -*- coding: utf-8 -*-
r"""内部构建号 / 代号的唯一入口：改 src\Directory.Build.props，别手改。

同一条版本线（<Version> 不动）每发一次包就跑一次：

    python tools/bump-build.py                # 构建号 +1，代号不变
    python tools/bump-build.py --code orion   # 换代号（换线时用）
    python tools/bump-build.py --set 12       # 直接指定构建号，不 +1
    python tools/bump-build.py --version 3.2.4  # 进位到新版本线，构建号重置为 1
    python tools/bump-build.py --show         # 只看现在是什么，不改

构建号写在 <FileVersion> 第 4 段（3.2.3.4）。它对内的意义只有一个：
让已经装在用户机器上的老客户端知道「版本号一样，但这是新包」——
OTA 检查更新时先比 <Version>，相同再比第 4 段。
"""
import argparse
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROPS = os.path.join(ROOT, "src", "Directory.Build.props")


def read(path):
    return io.open(path, encoding="utf-8-sig").read()


def write(path, text):
    # 保留 UTF-8 BOM：这个文件原本就带 BOM，Windows 上有些工具（记事本、XmlPeek 之类）
    # 见到无 BOM 的中文会按 ANSI 解，改成无 BOM 会让人以为注释乱码了。
    io.open(path, "w", encoding="utf-8-sig", newline="\n").write(text)


def current(text):
    v = re.search(r"<Version>([^<]+)</Version>", text)
    f = re.search(r"<FileVersion>([^<]+)</FileVersion>", text)
    c = re.search(r"<DeepSleepCode>([^<]+)</DeepSleepCode>", text)
    if not v or not f:
        sys.exit("× 读不到 <Version> / <FileVersion>：" + PROPS)
    version = v.group(1).strip()
    parts = f.group(1).strip().split(".")
    build = int(parts[3]) if len(parts) >= 4 and parts[3].strip().isdigit() else 0
    return version, build, (c.group(1).strip() if c else ""), parts


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--set", type=int, default=0, help="直接指定构建号")
    ap.add_argument("--code", default="", help="内部代号（小写字母/数字）")
    ap.add_argument("--version", default="", help="进位到新版本号（构建号重置为 1）")
    ap.add_argument("--show", action="store_true", help="只看当前值")
    a = ap.parse_args()

    text = read(PROPS)
    version, build, code, parts = current(text)
    print("当前：v%s  build %d  代号 %s" % (version, build, code or "(无)"))

    if a.show:
        return

    new_version = a.version.strip() or version
    new_code = (a.code.strip() or code)
    if a.set > 0:
        new_build = a.set
    elif a.version.strip():
        new_build = 1                      # 换版本线：构建号从 1 重新数
    else:
        new_build = build + 1

    if new_build <= build and new_version == version:
        print("× 新构建号 %d 不大于当前的 %d —— 客户端会认成「没有新版」" % (new_build, build))

    file_version = "%s.%d" % (new_version, new_build)
    text = re.sub(r"<Version>[^<]+</Version>", "<Version>%s</Version>" % new_version, text, count=1)
    text = re.sub(r"<FileVersion>[^<]+</FileVersion>",
                  "<FileVersion>%s</FileVersion>" % file_version, text, count=1)
    text = re.sub(r"<DeepSleepCode>[^<]+</DeepSleepCode>",
                  "<DeepSleepCode>%s</DeepSleepCode>" % new_code, text, count=1)
    write(PROPS, text)

    build, code, _ = (new_build, new_code, None)
    print("现在：v%s  build %d  代号 %s" % (new_version, build, code or "(无)"))
    print("      FileVersion=%s（写在 %s）" % (file_version, PROPS))
    print("下一步：重新构建，然后等我确认再发布。")


if __name__ == "__main__":
    main()

# -*- coding: utf-8 -*-
"""组装「万能安装程序」+ 补齐 macOS / Linux 的内核版包。

为什么需要这个脚本：
  publish.py 一直只产出 **Windows** 的内核版（deepsleep-core-*-win-x64.zip），
  macOS / Linux 那边只有桌面版（Photino）。而 macOS 桌面版在 Apple 芯片上会被
  Gatekeeper 判成「已损坏」打不开 —— 所以 macOS 改走内核版，就必须先把 macOS 的
  内核版包造出来，否则万能安装程序下到的是 404。

用法：
    python release/tools/mk-universal.py            # 缺什么补什么 + 组装
    python release/tools/mk-universal.py --skip-build   # 只组装，不重新编译
"""
import argparse
import base64
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
REL = os.path.join(ROOT, "release")
UNI_SRC = os.path.join(ROOT, "installer", "universal")

# macOS / Linux 内核版要覆盖的架构。x64 与 arm64 都要，
# 因为 Apple 芯片（arm64）和 Intel 老 Mac（x64）都得能用。
CORE_RIDS = ["osx-arm64", "osx-x64", "linux-x64", "linux-arm64"]

# 打进压缩包的可执行文件（tar 里必须带上可执行位，否则装完还要 chmod）
EXEC_BITS = {"deepsleep", "deepsleep-core", "deepsleep.sh", "tools/websearch.py"}


def say(t=""):
    print(t, flush=True)


def version():
    p = os.path.join(ROOT, "src", "Directory.Build.props")
    with open(p, encoding="utf-8-sig") as f:
        m = re.search(r"<Version>\s*([^<]+?)\s*</Version>", f.read())
    if not m:
        sys.exit("读不到版本号：" + p)
    return m.group(1)


def core_tgz(v, rid):
    return os.path.join(REL, "deepsleep-core-%s-%s.tar.gz" % (v, rid))


def build_core(v, rid):
    """交叉编译任意平台的内核版（.NET 的 self-contained 发布可以直接跨 OS 出包）。"""
    out = core_tgz(v, rid)
    say("  编译内核版 %s …" % rid)
    tmp = tempfile.mkdtemp(prefix="core-" + rid + "-")
    try:
        rc = subprocess.call([
            "dotnet", "publish",
            os.path.join(ROOT, "src", "DeepSleepCore", "DeepSleepCore.csproj"),
            "-c", "Release", "-f", "net10.0",
            "-r", rid, "--self-contained", "true",
            "-o", tmp, "--nologo", "-v", "q",
        ])
        if rc != 0:
            say("  × 编译失败：" + rid)
            return False

        # 打成 tar.gz。权限位不在这里处理：跨 OS 编译出来的文件位在 Windows 上不可靠，
        # 安装脚本里已经统一 chmod +x，比在这里伪造 mode 更实在。
        with tarfile.open(out, "w:gz") as tar:
            for name in sorted(os.listdir(tmp)):
                tar.add(os.path.join(tmp, name), arcname=name)
        say("  ✓ %s  %.1f MB" % (os.path.basename(out), os.path.getsize(out) / 1048576))
        return True
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def make_scripts(v, dest):
    """生成**单个**跨平台安装文件：deepsleep-install.cmd

    文件结构（一个文件，Windows 和 Linux/macOS 共用）：

        : #!/bin/sh                        ← cmd 当成标签忽略；sh 当成空命令
        : << 'DEEPSLEEP_BATCH'             ← sh 从这里开始丢弃，一直丢到分隔符
        @echo off
        set "DEEPSLEEP_HOME=%~dp0"
        powershell ... -EncodedCommand <base64>   ← Windows 的全部逻辑（中文在 base64 里）
        exit /b %ERRORLEVEL%               ← Windows 到此结束，后面的 shell 代码它永远读不到
        DEEPSLEEP_BATCH
        <install.sh 的正文>                 ← Linux / macOS 走这里

    为什么要 base64 这一层：
        .cmd 里的中文会被 cmd 按系统 OEM 代码页（中文机器是 GBK）解读，
        UTF-8 的中文直接写进去会变成乱码，更糟的是**括号块会被解错**，
        于是一路跑进下面的 shell 段（实测踩过）。改成把 PowerShell 脚本
        以 UTF-16LE + base64 塞进去，文件本身只剩纯 ASCII，
        中文在 PowerShell 内存里才是正确的。
        而 shell 段是 UTF-8，cmd 根本不会读到它，两边互不影响。

    换行必须是 LF：CRLF 会让 shell 把行尾 \r 当命令的一部分；cmd 对 LF 是容忍的。
    """
    with open(os.path.join(UNI_SRC, "install.ps1"), encoding="utf-8") as f:
        ps = f.read().replace("__VERSION__", v)
    with open(os.path.join(UNI_SRC, "install.sh"), encoding="utf-8") as f:
        sh = f.read().replace("__VERSION__", v)

    # ================= 关键约束（实测得出的，别改） =================
    # 整个文件必须是 **纯 ASCII**。
    # 只要文件里出现任何非 ASCII 字节（哪怕在 cmd 根本不会执行的行里），
    # cmd.exe 的逐行解析就会错位：会把 `setlocal EnableExtensions` 拆成
    # `setlocal Exten` + `sions` 并当成命令去执行，连 goto 的标签也会被拆断。
    # 所以中文不能直接写进这个文件 —— 两个分支各自把自己的正文 base64 塞进来，
    # 运行时再解出来：Windows 交给 PowerShell，Unix 交给 base64 解码后管道给 sh。
    # =================
    ps_clean = ps.replace("#PS_BEGIN", "").replace("#PS_END", "")
    # PowerShell 用 UTF-16LE 的 base64，逐行折一下（单行太长不利于排查，功能上无所谓）
    ps_b64 = base64.b64encode(ps_clean.encode("utf-16-le")).decode("ascii")
    ps_lines = [ps_b64[i:i + 200] for i in range(0, len(ps_b64), 200)]
    # Unix 分支：UTF-8 的 base64，一整行塞进变量
    sh_b64 = base64.b64encode(sh.encode("utf-8")).decode("ascii")

    launcher = (
        "powershell -NoProfile -ExecutionPolicy Bypass -Command \""
        "$p='%~f0';"
        "$t=[IO.File]::ReadAllText($p,[Text.Encoding]::UTF8);"
        # 标记拆开拼，避免这行自身也命中标记、把定位弄错（踩过）
        "$m='#PS'+'_BEGIN';$e='#PS'+'_END';"
        "$i=$t.IndexOf($m);$j=$t.IndexOf($e);"
        "if($j -le $i){Write-Host 'installer payload missing';exit 1};"
        "$b=$t.Substring($i+9,$j-$i-9) -replace '\\s','';"
        "$f=Join-Path $env:TEMP 'deepsleep-install.ps1';"
        "[IO.File]::WriteAllText($f,[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($b)),(New-Object Text.UTF8Encoding $true));"
        "& $f %*\""
    )

    out = []
    out.append(": #!/bin/sh")
    out.append(": << 'DEEPSLEEP_BATCH'")
    out.append("@echo off")
    out.append("setlocal EnableExtensions")
    out.append('set "DEEPSLEEP_HOME=%~dp0"')
    out.append(launcher)
    out.append("exit /b %ERRORLEVEL%")
    out.append("#PS_BEGIN")
    out.extend(ps_lines)
    out.append("#PS_END")
    out.append("DEEPSLEEP_BATCH")
    # Unix 分支：解出自己的正文再交给 sh。
    # macOS 的 base64 老版本只认 -D，新版本和 GNU 认 -d，所以两种都试一遍。
    out.append("DEEPSLEEP_HOME=${DEEPSLEEP_HOME:-$(CDPATH= cd -- \"$(dirname -- \"$0\")\" && pwd)}")
    out.append("export DEEPSLEEP_HOME")
    out.append("DEPSLEEP_B64='" + sh_b64 + "'")
    out.append("printf '%s' \"$DEPSLEEP_B64\" | (base64 -d 2>/dev/null || base64 -D 2>/dev/null || openssl base64 -d) | sh -")

    with open(os.path.join(dest, "deepsleep-install.cmd"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(out) + "\n")

    # 早期那三个「分开做」的脚本不再随包发出（用户要求单文件）
    for old in ("install.sh", "install.ps1", "install.cmd"):
        p = os.path.join(dest, old)
        if os.path.exists(p):
            os.remove(p)


README = """deepsleep 安装程序 v{ver}
========================================

就一个文件：deepsleep-install.cmd
Windows / Linux / macOS 都用它，装的时候选「桌面版」还是「内核版」。

  Windows     双击 deepsleep-install.cmd
  macOS       sh deepsleep-install.cmd      （只有内核版，见下）
  Linux       sh deepsleep-install.cmd      （桌面版 / 内核版都能装）

也可以带参数跳过菜单：
  deepsleep-install.cmd -core        （Windows 内核版）
  deepsleep-install.cmd -desktop     （Windows 桌面版）
  sh deepsleep-install.cmd --core    （Linux / macOS）

为什么一个文件能在两个系统上跑？
  它同时是一份批处理脚本和一份 shell 脚本：
    第 1、2 行在 Windows 眼里是「标签」，忽略掉；在 Unix 眼里是「空命令 + 丢弃式
    heredoc」，于是 Windows 那段被整段跳过。两边各走各的，互不干扰。
  Windows 那一半的逻辑（含中文提示）是 base64 编码后放在文件里的，运行时解出来
  交给 PowerShell 执行 —— 因为这文件必须保持纯 ASCII：只要里面出现一个非 ASCII
  字节，cmd.exe 的逐行解析就会错位，把正常命令拆成两半去执行。

内核版是什么？
  没有窗口。启动后它会打印一个本机地址和一个配对令牌，用浏览器打开那个地址
  就能用完整的 AI 助手（界面、工具、文件操作全都有）。适合服务器、SSH、容器。

为什么 macOS 没有桌面版？
  macOS 桌面版是 Photino + ad-hoc 签名，在 Apple 芯片上会被 Gatekeeper 判成
  「已损坏」直接打不开（签名只覆盖了 fat 包的一半）。所以 macOS 一律给内核版，
  它是纯命令行 + 浏览器界面，不碰 .app / 签名这套。安装时还会自动清掉
  quarantine 属性，否则系统会拦下下载来的二进制。

要不要联网？
  先试 GitHub；如果下载速度低于 500 KB/s 持续 3 秒，自动改从 Gitee 下载；
  Gitee 也不行就退到 GitHub 加速镜像（ghproxy 等），全都不行才报错。
  之所以按速度而不是按「失败」切换：GitHub 在国内经常连得上但只有几十 KB/s，
  傻等十分钟不如 3 秒判一次。

想离线装：把这个文件旁边的包放好即可（脚本优先用本地文件，完全不联网）：
    Windows   deepsleep-{ver}-win-x64.zip / deepsleep-core-{ver}-win-x64.zip
    macOS     deepsleep-core-{ver}-osx-arm64.tar.gz（Apple 芯片）
              deepsleep-core-{ver}-osx-x64.tar.gz（Intel）
    Linux     deepsleep-{ver}-linux-x64.tar.gz 或 deepsleep-core-{ver}-linux-x64.tar.gz
               （arm64 把 x64 换成 arm64）

装到哪 / 怎么卸载
  Windows 桌面版  解压到 %LOCALAPPDATA%\\Programs\\deepsleep（旁边有 Setup.exe 时交给它装）
  Windows 内核版  %LOCALAPPDATA%\\Programs\\deepsleep-core，删目录即卸载
  macOS / Linux    ~/.local/opt/deepsleep(-core)，启动器在 ~/.local/bin
                   卸载：rm -rf ~/.local/opt/deepsleep-core ~/.local/bin/deepsleep-core

不放心这个文件？
  纯文本，可以直接打开看结构：开头十来行是 Windows 入口，后面是 base64 数据
  和 Unix 入口。它只做四件事：认系统和 CPU → 问你装哪个 → 找/下载包 → 解压并建启动器。
"""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--skip-build", action="store_true", help="不重新编译内核版，用现成产物")
    a = ap.parse_args()

    v = version()
    say("")
    say("deepsleep 万能安装程序 · v" + v)
    say("-" * 60)

    if not a.skip_build:
        say("")
        say("补齐 macOS / Linux 内核版：")
        for rid in CORE_RIDS:
            if os.path.isfile(core_tgz(v, rid)):
                say("  已有 %s（跳过编译）" % os.path.basename(core_tgz(v, rid)))
                continue
            if not build_core(v, rid):
                say("  ! %s 编译失败，安装程序在那台机器上会回退为下载（可能 404）" % rid)

    dest = os.path.join(REL, "deepsleep-universal-" + v)
    shutil.rmtree(dest, ignore_errors=True)
    os.makedirs(dest, exist_ok=True)
    make_scripts(v, dest)
    with open(os.path.join(dest, "README.txt"), "w", encoding="utf-8") as f:
        f.write(README.replace("{ver}", v))

    # 故意**不**把 Setup.exe（130 MB）或各平台安装包塞进这个 zip：
    # 万能安装程序的定位是「一个很小的入口，按需取对应的包」，把它做成 130 MB
    # 反而没人愿意下。离线场景由用户自己把包放到脚本旁边（脚本优先用本地文件）。
    zpath = os.path.join(REL, "deepsleep-universal-%s.zip" % v)
    if os.path.exists(zpath):
        os.remove(zpath)
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as z:
        for name in sorted(os.listdir(dest)):
            z.write(os.path.join(dest, name), name)

    say("")
    say("✓ 组装完成")
    say("  目录 %s" % dest)
    for name in sorted(os.listdir(dest)):
        p = os.path.join(dest, name)
        say("    %-26s %8.1f KB" % (name, os.path.getsize(p) / 1024))
    say("  压缩包 %s  (%.1f MB)" % (os.path.basename(zpath), os.path.getsize(zpath) / 1048576))
    say("")


if __name__ == "__main__":
    main()

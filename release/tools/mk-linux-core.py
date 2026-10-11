#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""给 Linux 虚拟机用的 deepsleep 内核包（鸿蒙电脑版路线）。

背景（为什么需要这个包）：
  鸿蒙 PC 跑不了 .NET —— HarmonyOS 5.0.0(12) 起禁止映射可执行匿名内存，
  CoreCLR 的 JIT 起不来；而走 NativeAOT 又会撞上鸿蒙 seccomp 白名单里
  没有 get_mempolicy（@returnValue TRAP，直接 SIGSYS 杀进程）。
  所以鸿蒙上要跑内核，只能把它放进一个真正的 Linux 虚拟机里。
  这条路完全绕开了上面两个坑：VM 里是标准 Linux，.NET 的现成 linux-arm64
  构建直接就能跑，JIT 也不受限。

  鸿蒙 PC 上有现成的 Linux 环境可用：
    · 官方「融合开发引擎」（基于 StratoVirt，openEuler）—— 推荐，性能好
    · 自建 QEMU（社区 harmony-qemu + Alpine）—— 自包含，但只有 TCG，慢
  两者都支持端口转发，所以内核监听 0.0.0.0 即可被鸿蒙侧访问。

产出：
  release/deepsleep-core-<版本>-<rid>.tar.gz
    ├── deepsleep-core        自包含单文件（约 37MB，目标机不用装 .NET）
    ├── deepsleep-core.sh     启动/停止/看状态
    ├── install.sh            一键装到用户目录并可选开机自启
    ├── ui/                   界面（内核自己服务，离线可用）
    ├── web/                  网页版 + 内核版 + 超级连接
    ├── tools/websearch.py    网络搜索工具
    └── README.txt

用法：
  python release/tools/mk-linux-core.py                   # 默认 glibc + musl 两种 arm64
  python release/tools/mk-linux-core.py --rids linux-arm64
  python release/tools/mk-linux-core.py --skip-build      # 用已有 publish 输出重打包
"""

import argparse
import gzip
import io
import os
import re
import shutil
import subprocess
import sys
import tarfile

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
REL = os.path.join(ROOT, "release")
TOOLS = os.path.join(ROOT, "release", "tools")
WEBTOOL = os.path.join(ROOT, "src", "tools", "websearch.py")

# 哪些 RID 要出包。宿主 libc 决定选哪个：
#   openEuler / Debian / Ubuntu / 麒麟  → glibc  → linux-arm64
#   Alpine / 其它 musl 发行版           → musl   → linux-musl-arm64
DEFAULT_RIDS = ["linux-arm64", "linux-musl-arm64", "linux-x64"]

LAUNCH_SH = """#!/bin/sh
# deepsleep 内核 · 启动脚本（Linux / 鸿蒙虚拟机的 Linux 环境）
#
#   ./deepsleep-core.sh start     后台起服务（默认端口 8756）
#   ./deepsleep-core.sh stop      停
#   ./deepsleep-core.sh status    看状态与配对链接
#   ./deepsleep-core.sh run       前台跑（Ctrl+C 退出，适合调试）
#   ./deepsleep-core.sh token     只打印配对令牌
#
# 环境变量：
#   DS_PORT    端口，默认 8756
#   DS_DATA    数据目录，默认 $HOME/.local/share/deepsleep
#   DS_BIND    监听地址，默认 0.0.0.0（虚拟机里要让宿主机转发进来）
set -eu

HERE="$(cd "$(dirname "$0")" && pwd)"
BIN="$HERE/deepsleep-core"
PORT="${DS_PORT:-8756}"
DATA="${DS_DATA:-$HOME/.local/share/deepsleep}"
BIND="${DS_BIND:-0.0.0.0}"
PIDFILE="$DATA/core.pid"
LOGFILE="$DATA/core.log"

[ -x "$BIN" ] || { echo "找不到可执行文件：$BIN" >&2; exit 1; }

port_busy() {
  # 端口被占了吗（不依赖 lsof / netstat —— 精简发行版里不一定有）
  if command -v ss >/dev/null 2>&1; then ss -ltn 2>/dev/null | grep -q ":$PORT "; return $?; fi
  if command -v netstat >/dev/null 2>&1; then netstat -ltn 2>/dev/null | grep -q ":$PORT "; return $?; fi
  # 都没有就靠 PID 文件判断，退一步
  [ -f "$PIDFILE" ] && kill -0 "$(cat "$PIDFILE")" 2>/dev/null
}

alive() { [ -f "$PIDFILE" ] && kill -0 "$(cat "$PIDFILE")" 2>/dev/null; }

print_links() {
  TOKEN="$(cat "$DATA/data/core-token.txt" 2>/dev/null || echo '')"
  echo
  echo "  内核地址   http://127.0.0.1:$PORT/"
  echo "  配对令牌   $TOKEN"
  echo "  数据目录   $DATA/data"
  echo
  echo "  ── 在鸿蒙电脑版上怎么连 ──"
  echo "  ① 官方「融合开发引擎」：它把虚拟机端口映射到鸿蒙本机，"
  echo "     所以鸿蒙里的浏览器/客户端直接开 http://127.0.0.1:$PORT/ 就能连上。"
  echo "  ② 自建 QEMU：启动参数里加端口转发 ——"
  echo "     -netdev user,id=n0,hostfwd=tcp::${PORT}-:${PORT} -device virtio-net-device,netdev=n0"
  echo "     然后鸿蒙侧同样开 http://127.0.0.1:$PORT/"
  echo "  ③ 也可以让内核自己建公网隧道（需要 VM 里装了 ssh 客户端）："
  echo "     DS_BIND=0.0.0.0 ./deepsleep-core.sh run --tunnel"
  echo
  echo "  完整控制台（在鸿蒙里打开，或虚拟机内本机浏览器）："
  echo "      http://127.0.0.1:$PORT/web/core/#t=$TOKEN"
  echo
}

cmd_start() {
  if alive; then echo "已经在跑了（pid $(cat "$PIDFILE")）"; print_links; return 0; fi
  if port_busy; then
    echo "端口 $PORT 已被别的程序占用。换个端口：DS_PORT=8757 ./deepsleep-core.sh start" >&2
    return 1
  fi
  mkdir -p "$DATA/data"
  "$BIN" --port "$PORT" --data "$DATA/data" --host "$BIND" --headless \
    >>"$LOGFILE" 2>&1 &
  echo $! >"$PIDFILE"
  sleep 1
  if alive; then echo "已启动（pid $(cat "$PIDFILE")），日志 $LOGFILE"; print_links
  else echo "启动失败，看日志：$LOGFILE" >&2; tail -20 "$LOGFILE" >&2; return 1; fi
}

cmd_stop() {
  if ! alive; then echo "没在跑"; return 0; fi
  PID="$(cat "$PIDFILE")"
  kill "$PID" 2>/dev/null || true
  sleep 1
  kill -0 "$PID" 2>/dev/null && kill -9 "$PID" 2>/dev/null || true
  rm -f "$PIDFILE"
  echo "已停止"
}

cmd_status() {
  if alive; then echo "运行中（pid $(cat "$PIDFILE")）"; print_links
  else echo "未运行"; fi
}

case "${1:-start}" in
  start)  cmd_start ;;
  stop)   cmd_stop ;;
  restart) cmd_stop; cmd_start ;;
  status) cmd_status ;;
  token)  cat "$DATA/data/core-token.txt" 2>/dev/null || echo "（还没有令牌，先 start）" ;;
  run)
    shift || true
    mkdir -p "$DATA/data"
    exec "$BIN" --port "$PORT" --data "$DATA/data" --host "$BIND" "$@" ;;
  *) echo "用法：$0 {start|stop|restart|status|token|run}"; exit 1 ;;
esac
"""

INSTALL_SH = """#!/bin/sh
# deepsleep 内核 · 一键安装（Linux，含鸿蒙电脑版的 Linux 虚拟机环境）
#
#   ./install.sh            装到 ~/.local/share/deepsleep（不用 root）
#   sudo ./install.sh --sys 装到 /opt/deepsleep（全机可用）
#   ./install.sh --autostart 顺便注册开机自启
#
# 装完会打印配对链接。卸载：rm -rf ~/.local/share/deepsleep（或 /opt/deepsleep）
set -eu

HERE="$(cd "$(dirname "$0")" && pwd)"
SYS=0
AUTOSTART=0
for a in "$@"; do
  case "$a" in
    --sys) SYS=1 ;;
    --autostart) AUTOSTART=1 ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
  esac
done

ARCH="$(uname -m)"
case "$ARCH" in
  aarch64|arm64) : ;;
  *) echo "这个包是 aarch64 的，当前机器是 $ARCH。" >&2
     echo "（鸿蒙 PC 是新麒麟 ARM64；x64 的 Linux 请用 linux-x64 那个包。）" >&2
     exit 1 ;;
esac

if [ "$SYS" = 1 ]; then
  DST="/opt/deepsleep"
  [ "$(id -u)" = 0 ] || { echo "--sys 需要 root：sudo ./install.sh --sys" >&2; exit 1; }
else
  DST="$HOME/.local/share/deepsleep"
fi

echo "安装到 $DST …"
mkdir -p "$DST"
cp -f "$HERE/deepsleep-core" "$DST/deepsleep-core"
chmod +x "$DST/deepsleep-core"
cp -f "$HERE/deepsleep-core.sh" "$DST/deepsleep-core.sh"
chmod +x "$DST/deepsleep-core.sh"
rm -rf "$DST/ui" "$DST/web" "$DST/tools"
cp -R "$HERE/ui" "$DST/ui"
cp -R "$HERE/web" "$DST/web"
[ -d "$HERE/tools" ] && cp -R "$HERE/tools" "$DST/tools" || true

# 数据目录（内核把对话、令牌都放这儿）
mkdir -p "$DST/data"

# 建一个能在任何地方敲的命令（软链到 PATH 里已有的目录；没有就退到 DST 自己）
LINK="/usr/local/bin/deepsleep-core"
if [ "$SYS" = 1 ] && [ -w /usr/local/bin ]; then
  ln -sf "$DST/deepsleep-core.sh" "$LINK"
  echo "命令：deepsleep-core start|stop|status"
else
  echo "命令：$DST/deepsleep-core.sh start|stop|status"
fi

if [ "$AUTOSTART" = 1 ]; then
  # 能认几种认几种：systemd（openEuler/Debian）→ OpenRC（Alpine）→ 兜底给一条 crontab
  if command -v systemctl >/dev/null 2>&1; then
    UNIT="$HOME/.config/systemd/user/deepsleep.service"
    mkdir -p "$(dirname "$UNIT")"
    cat >"$UNIT" <<EOF
[Unit]
Description=deepsleep 内核

[Service]
ExecStart=$DST/deepsleep-core --port 8756 --data $DST/data --host 0.0.0.0 --headless
Restart=on-failure
Environment=HOME=$HOME

[Install]
WantedBy=default.target
EOF
    systemctl --user daemon-reload 2>/dev/null || true
    systemctl --user enable --now deepsleep 2>/dev/null \
      && echo "已注册 systemd 用户服务（systemctl --user status deepsleep）" \
      || echo "systemd 用户服务注册失败，改用 ./deepsleep-core.sh start 手动起"
  elif command -v rc-update >/dev/null 2>&1; then
    cat >/etc/local.d/deepsleep.start <<EOF
#!/bin/sh
DS_DATA=$DST/data DS_BIND=0.0.0.0 $DST/deepsleep-core.sh start
EOF
    chmod +x /etc/local.d/deepsleep.start
    rc-update add local default 2>/dev/null || true
    echo "已注册 OpenRC 开机自启（/etc/local.d/deepsleep.start）"
  else
    echo "没找到 systemd / OpenRC。想让内核开机自起，把下面一行加进 crontab -e："
    echo "  @reboot DS_DATA=$DST/data $DST/deepsleep-core.sh start"
  fi
fi

echo
echo "装好了。下一步："
echo "  $DST/deepsleep-core.sh start      起服务并打印配对链接"
echo "  $DST/deepsleep-core.sh status     随时看状态"
echo
echo "鸿蒙侧就开这个地址（端口转发由虚拟机负责）：http://127.0.0.1:8756/"
"""

README_TXT = """deepsleep 内核 · Linux / 鸿蒙电脑版（虚拟机路线）
================================================

为什么要在虚拟机里跑
--------------------
鸿蒙 PC 跑不了 .NET：
  · HarmonyOS 5.0.0(12) 起禁止应用映射可执行匿名内存 → CoreCLR 的 JIT 起不来；
  · 改走 NativeAOT 又会撞鸿蒙 seccomp 白名单里没有 get_mempolicy
    （app.seccomp.policy 开头写着 @returnValue TRAP —— 直接 SIGSYS 杀进程）。
所以内核得放进一个真正的 Linux 虚拟机：那里面是标准 Linux，
下面这个自包含的 deepsleep-core 直接就能跑，不需要装 .NET，JIT 也不受限。

三分钟跑起来
------------
  tar xzf deepsleep-core-<版本>-<你的架构>.tar.gz
  cd deepsleep-core-<版本>-<你的架构>
  ./install.sh                 # 装到 ~/.local/share/deepsleep
  ~/.local/share/deepsleep/deepsleep-core.sh start
  # 会打印一行配对链接；在鸿蒙里打开它

选哪个包
--------
  linux-arm64        glibc 发行版：openEuler（官方融合开发引擎就是它）、
                     Debian、Ubuntu、麒麟、统信
  linux-musl-arm64   musl 发行版：Alpine 等精简系统
  linux-x64          普通 x64 Linux（少数鸿蒙 x86 环境 / 服务器）
  不确定是哪个 libc，敲：  ldd --version   （或  ls /lib/ld-musl-* ）

鸿蒙上怎么连进来
----------------
内核默认监听 0.0.0.0:8756，靠虚拟机的端口转发被鸿蒙访问：

  · 官方「融合开发引擎」（推荐）
      它基于 StratoVirt，虚拟机端口自动映射到鸿蒙本机，性能接近原生。
      在里面跑起来后，鸿蒙侧直接开 http://127.0.0.1:8756/

  · 自建 QEMU（社区 harmony-qemu + Alpine）
      启动参数里加端口转发：
        -netdev user,id=n0,hostfwd=tcp::8756-:8756 \\
        -device virtio-net-device,netdev=n0
      注意：第三方应用拿不到 HMV/KVM 加速，只能 TCG 纯软件模拟，
      .NET 内核在这种环境下会比较卡，属于「能用但别指望流畅」。

  · 都不方便时，让内核自己建公网隧道（VM 里要有 ssh 客户端）：
        DS_BIND=0.0.0.0 ./deepsleep-core.sh run --tunnel
      它会打印一条公网配对链接，鸿蒙里打开即可。

能做什么 / 不能做什么
--------------------
  能用：AI 对话、联网搜索、深度研究、文件读写（虚拟机内）、命令执行（虚拟机内）、
        长期记忆、技能、超级连接（远程桌面/对话）
  受限：远程桌面截的是「虚拟机」的屏，不是鸿蒙本机的屏 —— 因为内核跑在 VM 里，
        看不到宿主机桌面。要控鸿蒙本机得另做（鸿蒙自己的截屏/输入 API）。

数据在哪
--------
  ~/.local/share/deepsleep/data
    core-token.txt   配对令牌（别外传）
    conversations.json、记忆、技能等

常用命令
--------
  ./deepsleep-core.sh start|stop|restart|status|token|run
  DS_PORT=8757 ./deepsleep-core.sh start     换个端口
  ./deepsleep-core.sh run --help             看内核自己的全部参数
"""


def say(s):
    print(s, flush=True)


def mb(n):
    return n / 1048576.0


def version():
    """从 src/Directory.Build.props 读版本（唯一真源）。"""
    p = os.path.join(ROOT, "src", "Directory.Build.props")
    with io.open(p, encoding="utf-8-sig") as f:
        m = re.search(r"<Version>([^<]+)</Version>", f.read())
    if not m:
        raise SystemExit("读不到版本号：" + p)
    return m.group(1).strip()


def publish(rid, out):
    """编译自包含内核（普通 publish，不需要原生工具链，Windows 上也能跨平台产出）。"""
    if os.path.isdir(out):
        shutil.rmtree(out)
    cmd = ["dotnet", "publish", os.path.join(ROOT, "src", "DeepSleepCore", "DeepSleepCore.csproj"),
           "-c", "Release", "-f", "net10.0", "-r", rid, "--self-contained", "true",
           "-o", out, "--nologo"]
    r = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True, errors="replace")
    if r.returncode != 0:
        tail = "\n".join([l for l in (r.stdout or "").splitlines()
                          if "error" in l.lower()][-12:]) or (r.stderr or "")[-800:]
        raise SystemExit("编译失败（%s）：\n%s" % (rid, tail))


def write_text(path, text):
    """按 LF 写文本 —— 脚本带 CRLF 在 Linux 上会报 bad interpreter。"""
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def tar_gz_dir(src, dst, exec_names=()):
    """打 tar.gz；Windows 上没有执行位，必须显式写 mode，否则解包后跑不起来。"""
    with open(dst, "wb") as raw:
        with gzip.GzipFile(fileobj=raw, mode="wb", compresslevel=6, mtime=0) as gz:
            with tarfile.open(fileobj=gz, mode="w", format=tarfile.GNU_FORMAT) as t:
                for dirpath, dirnames, filenames in os.walk(src):
                    dirnames.sort()
                    for name in sorted(filenames):
                        full = os.path.join(dirpath, name)
                        rel = os.path.relpath(full, src).replace("\\", "/")
                        ti = t.gettarinfo(full, arcname=rel)
                        ti.mode = 0o755 if name in exec_names else 0o644
                        ti.uid = ti.gid = 0
                        ti.uname = ti.gname = ""
                        with open(full, "rb") as f:
                            t.addfile(ti, f)


def build(rid, ver, skip_build):
    say("── " + rid)
    pub = os.path.join(os.environ.get("TEMP", "/tmp"), "ds-core-" + rid)
    if not skip_build:
        publish(rid, pub)
    if not os.path.isfile(os.path.join(pub, "deepsleep-core")):
        raise SystemExit("没有编译产物：" + pub)

    name = "deepsleep-core-%s-%s" % (ver, rid)
    stage = os.path.join(pub, "..", "stage-" + rid)
    if os.path.isdir(stage):
        shutil.rmtree(stage)
    root = os.path.join(stage, name)
    os.makedirs(root)

    # 可执行文件（去掉 pdb，VM 里用不到）
    for f in os.listdir(pub):
        if f.lower().endswith(".pdb"):
            continue
        s, d = os.path.join(pub, f), os.path.join(root, f)
        if os.path.isdir(s):
            shutil.copytree(s, d)
        else:
            shutil.copy2(s, d)

    # 网络搜索工具（内核会去 tools/ 找）
    if os.path.isfile(WEBTOOL):
        os.makedirs(os.path.join(root, "tools"), exist_ok=True)
        shutil.copy2(WEBTOOL, os.path.join(root, "tools", "websearch.py"))

    write_text(os.path.join(root, "deepsleep-core.sh"), LAUNCH_SH)
    write_text(os.path.join(root, "install.sh"), INSTALL_SH)
    write_text(os.path.join(root, "README.txt"), README_TXT)

    tar = os.path.join(REL, name + ".tar.gz")
    # 传 stage（而不是 root）：这样包内有 <name>/ 顶层目录，
    # 解开是规规矩矩一个文件夹，不会把 8 个文件散一地
    # （README / install.sh 里的 `cd deepsleep-core-<版本>-<架构>` 也是照这个写的）。
    # websearch.py 原来就是可执行的（内核可能直接 exec 它），别让它掉成 644。
    tar_gz_dir(stage, tar, exec_names=("deepsleep-core", "deepsleep-core.sh", "install.sh", "websearch.py"))
    say("   %-46s %.2f MB" % (os.path.basename(tar), mb(os.path.getsize(tar))))
    return tar


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rids", default=",".join(DEFAULT_RIDS))
    ap.add_argument("--skip-build", action="store_true", help="用已有 publish 输出重打包")
    a = ap.parse_args()

    ver = version()
    rids = [r.strip() for r in a.rids.split(",") if r.strip()]
    say("deepsleep 内核 · Linux/鸿蒙虚拟机包  版本 %s" % ver)
    say("目标：" + "、".join(rids))
    say("")

    made = []
    for rid in rids:
        try:
            made.append(build(rid, ver, a.skip_build))
        except SystemExit as e:
            say("   ✗ %s" % e)

    say("")
    say("完成 %d 个包，都在 release/ 下：" % len(made))
    for t in made:
        say("   " + os.path.basename(t))
    return 0 if made else 1


if __name__ == "__main__":
    sys.exit(main())

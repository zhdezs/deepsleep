#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""给鸿蒙电脑版做一个「小 Linux 虚拟机」。

为什么需要：鸿蒙 PC 跑不了 .NET（禁 JIT + seccomp 里没有 get_mempolicy，
详见 AGENTS.md「鸿蒙电脑版」一节），所以内核得放进真 Linux 里。
官方「融合开发引擎」自带 openEuler，直接解压内核包就行、不需要本脚本；
本脚本是给**自建 QEMU**（社区 harmony-qemu 那类）准备的：一个约 40MB 的
最小 Alpine + deepsleep，配好自启动和 DHCP，解开就能起。

产出：
  release/deepsleep-vm-<版本>-arm64/
    ├── rootfs.tar.gz     Alpine 最小根文件系统 + deepsleep（这就是「小 linux」）
    ├── run-qemu.sh       QEMU 启动脚本（自动把 rootfs 转成 ext4 磁盘、带端口转发）
    └── README.txt
  并打成 release/deepsleep-vm-<版本>-arm64.tar.gz

用法：
  python release/tools/mk-harmony-vm.py
  python release/tools/mk-harmony-vm.py --core "C:/.../deepsleep-core-linux-musl-arm64.tar.gz"
"""

import argparse
import gzip
import io
import os
import re
import shutil
import sys
import tarfile
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
REL = os.path.join(ROOT, "release")

ALPINE_MIRROR = "https://dl-cdn.alpinelinux.org/alpine"
MINIROOTFS_DIR = ALPINE_MIRROR + "/latest-stable/releases/aarch64/"

# Alpine 里 deepsleep 装哪
DS_DIR = "opt/deepsleep"

# ---- Alpine 内部要加/改的东西 ----------------------------------------

BOOT_SH = """#!/bin/sh
# 开机脚本。⚠ 这个最小系统里**没有 openrc** —— Alpine 的 minirootfs 是给
# chroot / 容器用的，原始 inittab 调的那三行 /sbin/openrc 在这份包里根本不存在
# （/sbin/ 下那些全是 busybox 的 applet）。所以挂载、配网、起内核全自己来，
# 由 /etc/inittab 的 sysinit 直接调本脚本。
export PATH=/sbin:/usr/sbin:/bin:/usr/bin
exec >/dev/console 2>&1

echo ""
echo "[boot] deepsleep 最小 Linux 启动中…"

# 找一张网卡（QEMU virt 通常是 eth0；网卡命名规则不同的话兜底扫一遍）
IFACE=""
i=0
while [ $i -lt 10 ]; do
  for c in eth0 enp0s1 ens3 ens18; do
    if [ -e "/sys/class/net/$c" ]; then IFACE=$c; break; fi
  done
  if [ -z "$IFACE" ]; then
    for p in /sys/class/net/*; do
      n=$(basename "$p")
      if [ "$n" = "lo" ]; then continue; fi
      IFACE=$n
      break
    done
  fi
  if [ -n "$IFACE" ]; then break; fi
  i=$((i + 1))
  sleep 1
done

if [ -n "$IFACE" ]; then
  echo "[boot] 网卡 $IFACE，跑 udhcpc 拿地址…"
  udhcpc -i "$IFACE" -b -q -t 5 >/dev/null 2>&1 || echo "[boot] DHCP 没成（没网也能在本机用）"
else
  echo "[boot] 没找到网卡（没网也能在本机用）"
fi

echo "[boot] 起 deepsleep 内核…"
DS_BIND=0.0.0.0 DS_DATA=/opt/deepsleep/data \\
  /opt/deepsleep/deepsleep-core.sh start >>/var/log/deepsleep.log 2>&1

# 等内核把令牌文件写出来
j=0
while [ $j -lt 30 ]; do
  if [ -f /opt/deepsleep/data/core-token.txt ]; then break; fi
  sleep 0.5
  j=$((j + 1))
done

TOKEN=$(cat /opt/deepsleep/data/core-token.txt 2>/dev/null || echo "")
echo ""
echo "┌────────────────────────────────────────────────────────────┐"
echo "│  deepsleep 内核已经在这个小 Linux 里跑起来了              │"
echo "└────────────────────────────────────────────────────────────┘"
echo "  在宿主机（鸿蒙）里打开：  http://127.0.0.1:8756/"
if [ -z "$TOKEN" ]; then
  echo "  配对令牌：               还没生成，看日志：tail /var/log/deepsleep.log"
else
  echo "  配对令牌：               $TOKEN"
fi
echo "  管理命令：               /opt/deepsleep/deepsleep-core.sh start|stop|status|token"
echo ""
echo "  下面就是一个 root shell（apk add 装东西也行）。"
echo ""
"""

INITTAB = """# /etc/inittab
#
# ⚠ 不调 openrc：Alpine 的 minirootfs 是为 chroot / 容器准备的，里面**没有
#   /sbin/openrc**（原始 inittab 的那三行在这份包上必然失败）。这里只用
#   busybox 内建：挂完文件系统就直接跑我们的 boot.sh。
::sysinit:/bin/mkdir -p /proc /sys /dev /tmp /run /var/log
::sysinit:/bin/mount -t proc proc /proc
::sysinit:/bin/mount -t sysfs sysfs /sys
::sysinit:/bin/mount -t devtmpfs devtmpfs /dev
::sysinit:/bin/mount -t tmpfs tmpfs /tmp
::sysinit:/bin/mount -t tmpfs tmpfs /run
::sysinit:/bin/hostname -F /etc/hostname
::sysinit:/opt/deepsleep/boot.sh

# 串口控制台直接进 root shell（这是给虚拟机用的，别挪到公网）。
# 只在串口上开 —— QEMU -nographic 没有 tty1~6，开了会 getty 失败后无限 respawn 刷屏。
ttyAMA0::respawn:/sbin/getty -n -l /bin/sh 115200 ttyAMA0 vt100

::ctrlaltdel:/sbin/reboot
::shutdown:/bin/umount -a -r
::restart:/sbin/init
"""

ISSUE = r"""
  deepsleep · 最小 Linux（Alpine + deepsleep 内核）
  -------------------------------------------------
  内核已经在跑。要在鸿蒙/宿主机里连它，开：  http://127.0.0.1:8756/
  令牌：cat /opt/deepsleep/data/core-token.txt
  停/起/看状态：/opt/deepsleep/deepsleep-core.sh {stop|start|status}
"""

RUN_QEMU = """#!/bin/sh
# 用 QEMU 起 deepsleep 的「小 Linux」。
#
# 适合鸿蒙电脑版的「自建 QEMU」路线（社区 harmony-qemu 那类移植）。
# 需要宿主机装了 qemu-system-aarch64；首次运行会把 rootfs 转成 ext4 磁盘，
# 那一步需要 mkfs.ext4（e2fsprogs，大多数 Linux 都有）。
#
# 用法：
#   ./run-qemu.sh                       默认 2GB 内存 / 4 核 / 端口 8756
#   MEM=4096 CPU=8 ./run-qemu.sh        调资源
#   PORT=9000 ./run-qemu.sh             换转发端口
#   ./run-qemu.sh -kernel /path/vmlinuz-virt -initrd /path/initramfs-virt
#                                       指定自己的内核（不指定就用 Alpine 官方 netboot 的）
#
# 起来后在宿主机浏览器打开 http://127.0.0.1:<PORT>/
# 串口控制台是自动进 root shell 的（这个镜像是给虚拟机用的）。
set -eu

HERE="$(cd "$(dirname "$0")" && pwd)"
PORT="${PORT:-8756}"
MEM="${MEM:-2048}"
CPU="${CPU:-4}"
ROOTFS="$HERE/rootfs"
IMG="$HERE/rootfs.img"
DISK="${DISK:-8G}"
KERNEL=""
INITRD=""

# 允许命令行覆盖内核
while [ $# -gt 0 ]; do
  case "$1" in
    -kernel) KERNEL="$2"; shift 2 ;;
    -initrd) INITRD="$2"; shift 2 ;;
    *) echo "未知参数：$1" >&2; exit 2 ;;
  esac
done

command -v qemu-system-aarch64 >/dev/null 2>&1 || {
  echo "没找到 qemu-system-aarch64。先装 QEMU（鸿蒙上是社区移植版 harmony-qemu）。" >&2
  exit 1
}

# 1) 把 rootfs 目录准备出来
if [ ! -d "$ROOTFS" ]; then
  echo "解压 rootfs …"
  mkdir -p "$ROOTFS"
  tar xzf "$HERE/rootfs.tar.gz" -C "$ROOTFS"
fi

# 2) 转成 ext4 磁盘镜像（只做一次；之后改了 rootfs 想重来就删掉 rootfs.img）
if [ ! -f "$IMG" ]; then
  echo "生成磁盘镜像 $IMG（${DISK}）…"
  command -v mkfs.ext4 >/dev/null 2>&1 || {
    echo "需要 mkfs.ext4（装 e2fsprogs）。或者自己用别的办法把 rootfs 做成镜像。" >&2
    echo "也可以跳过磁盘、直接用 9p 共享：见 README.txt。" >&2
    exit 1
  }
  # seek 的单位是 bs（1M），所以 8G → 8192。原来写 tr -d 'G' 再拼 000，
  # 碰上 DISK=8GB 这种就变成 "8B000" 直接报错；这里只留数字再乘 1024。
  SZ="$(printf '%s' "$DISK" | tr -dc '0-9')"
  [ -n "$SZ" ] || SZ=8
  dd if=/dev/zero of="$IMG" bs=1M count=0 seek=$(( SZ * 1024 )) 2>/dev/null \\
    || { echo "创建镜像文件失败" >&2; exit 1; }
  mkfs.ext4 -q -F -d "$ROOTFS" "$IMG"
  echo "镜像已生成。"
fi

# 3) 内核：优先用命令行给的，其次找本目录的。
#    ⚠ 下面全用 if 而不是 `[ -z X ] && ...`：后者在条件为假时返回非零，
#    配 set -e 会让脚本在这里直接退出（本仓库在 macOS 打包脚本上踩过同一个坑）。
if [ -z "$KERNEL" ]; then
  for c in "$HERE/vmlinuz-virt" "$HERE/kernel_aarch64" "$HERE/Image"; do
    if [ -f "$c" ]; then KERNEL="$c"; break; fi
  done
fi
if [ -z "$KERNEL" ]; then
  echo "本目录没有内核。请下 Alpine 的 netboot 内核，或自己指定：" >&2
  echo "  curl -L -o vmlinuz-virt  https://dl-cdn.alpinelinux.org/alpine/latest-stable/releases/aarch64/netboot/vmlinuz-virt" >&2
  echo "  curl -L -o initramfs-virt https://dl-cdn.alpinelinux.org/alpine/latest-stable/releases/aarch64/netboot/initramfs-virt" >&2
  echo "  ./run-qemu.sh" >&2
  exit 1
fi
if [ -z "$INITRD" ]; then
  for c in "$HERE/initramfs-virt" "$HERE/initramfs"; do
    if [ -f "$c" ]; then INITRD="$c"; break; fi
  done
fi

echo "启动 QEMU（端口 $PORT，内存 ${MEM}MB，${CPU} 核）…"
echo "起来后开： http://127.0.0.1:$PORT/"
echo

set -- \\
  -M virt -cpu cortex-a72 -smp "$CPU" -m "$MEM" \\
  -kernel "$KERNEL" \\
  -append "console=ttyAMA0 root=/dev/vda rw" \\
  -drive "file=$IMG,format=raw,if=none,id=d0" \\
  -device virtio-blk-device,drive=d0 \\
  -netdev "user,id=n0,hostfwd=tcp::$PORT-:8756" \\
  -device virtio-net-device,netdev=n0 \\
  -nographic

if [ -n "$INITRD" ]; then set -- -initrd "$INITRD" "$@"; fi

exec qemu-system-aarch64 "$@"
"""

VM_README = """deepsleep · 最小 Linux（给鸿蒙电脑版的「小虚拟机」）
======================================================

这里面是什么
------------
rootfs.tar.gz   Alpine 最小根文件系统 + deepsleep 内核，已经配好开机自启和 DHCP
run-qemu.sh     用 QEMU 把它跑起来（自动做磁盘镜像、带 8756 端口转发）
vmlinuz-virt    （如果带了）Alpine 的 aarch64 内核
initramfs-virt  （如果带了）对应的 initramfs

两条路怎么选
------------
【推荐】官方「融合开发引擎」
  鸿蒙 PC 应用市场里的官方应用，基于 StratoVirt，一键起 openEuler，
  虚拟机端口自动映射到鸿蒙本机，性能接近原生。
  用法：在里面直接解压 release/deepsleep-core-<版本>-linux-arm64.tar.gz，
        然后 ./install.sh && ~/.local/share/deepsleep/deepsleep-core.sh start
  → 不需要本目录里的东西（那里面已经是完整的 openEuler）。

【备选】自建 QEMU（本目录就是给这条路的）
  · 第三方应用拿不到 HMV/KVM 加速，只能 TCG 纯软件模拟，.NET 内核会明显卡，
    属于「能用但别指望流畅」。
  · 鸿蒙上要用社区移植的 harmony-qemu（或 HiSH 那个项目的 QEMU 库）。
  · 用法：
      tar xzf deepsleep-vm-<版本>-arm64.tar.gz
      cd deepsleep-vm-<版本>-arm64
      ./run-qemu.sh
    然后在鸿蒙里开 http://127.0.0.1:8756/

怎么连
------
内核在这个小 Linux 里监听 0.0.0.0:8756，靠 QEMU 的端口转发被宿主机访问：
    -netdev user,id=n0,hostfwd=tcp::8756-:8756
所以宿主机（鸿蒙）开 http://127.0.0.1:8756/ 就是它。
第一次会要配对令牌，在虚拟机里敲：
    cat /opt/deepsleep/data/core-token.txt

虚拟机里还有什么
----------------
  这是个精简到极限的 Alpine：**没有 openrc / systemd**（Alpine 官方的 minirootfs
  是给 chroot 和容器用的，里面只有 busybox）。所以开机流程是：
    /etc/inittab（busybox init）→ 挂 proc/sys/dev → /opt/deepsleep/boot.sh
  boot.sh 负责配网（udhcpc）和把内核拉起来，日志在 /var/log/deepsleep.log。
  想加开机动作就改 boot.sh。

  busybox 自带 sh、vi、wget、nslookup、tar、gzip 等；装东西用 apk：
      apk add openssh-client        # 装了它，内核才能自己建公网隧道（--tunnel）
  管理 deepsleep：
      /opt/deepsleep/deepsleep-core.sh start|stop|restart|status|token
  日志：
      /var/log/deepsleep.log

没带内核怎么办
--------------
本目录可能没带 vmlinuz / initramfs（那两份是 Alpine 官方构建，体积不小）。
自己下一下，或者让 run-qemu.sh 自己提示你：
  curl -L -o vmlinuz-virt   https://dl-cdn.alpinelinux.org/alpine/latest-stable/releases/aarch64/netboot/vmlinuz-virt
  curl -L -o initramfs-virt https://dl-cdn.alpinelinux.org/alpine/latest-stable/releases/aarch64/netboot/initramfs-virt
也可以用自己的内核（比如 harmony-qemu 项目带的那份）：./run-qemu.sh -kernel 路径

安全提醒
--------
这个镜像是**给虚拟机的**，串口控制台自动进 root shell、root 无密码。
别把它挪到公网上。要暴露就只在 QEMU 的 hostfwd 里转发 8756，别转发别的。
"""


def say(s):
    print(s, flush=True)


def mb(n):
    return n / 1048576.0


def version():
    """从 src/Directory.Build.props 读版本（唯一真源）。"""
    with io.open(os.path.join(ROOT, "src", "Directory.Build.props"), encoding="utf-8-sig") as f:
        m = re.search(r"<Version>([^<]+)</Version>", f.read())
    if not m:
        raise SystemExit("读不到版本号")
    return m.group(1).strip()


def fetch(url, what):
    say("   下载 %s …" % what)
    req = urllib.request.Request(url, headers={"User-Agent": "deepsleep-build"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return r.read()


def latest_minirootfs():
    """从目录列表里找出 minirootfs 文件名（不写死版本号，Alpine 升版也不会坏）。"""
    html = fetch(MINIROOTFS_DIR, "Alpine 目录列表").decode("utf-8", "replace")
    m = re.findall(r"alpine-minirootfs-([0-9.]+)-aarch64\.tar\.gz", html)
    if not m:
        raise SystemExit("在 %s 里找不到 minirootfs" % MINIROOTFS_DIR)
    ver = sorted(set(m), key=lambda s: [int(x) for x in s.split(".")])[-1]
    return "alpine-minirootfs-%s-aarch64.tar.gz" % ver


# ---------------- tar 里加东西的小工具（Windows 上也得能加软链） ----------------

def _ti(name, mode):
    ti = tarfile.TarInfo(name)
    ti.mode = mode
    ti.mtime = 0
    ti.uid = ti.gid = 0
    ti.uname = ti.gname = ""
    return ti


def add_file(tf, name, data, mode=0o644):
    if isinstance(data, str):
        data = data.encode("utf-8")
    ti = _ti(name, mode)
    ti.size = len(data)
    tf.addfile(ti, io.BytesIO(data))


def add_dir(tf, name, mode=0o755):
    ti = _ti(name, mode)
    ti.type = tarfile.DIRTYPE
    tf.addfile(ti)


def add_symlink(tf, name, target, mode=0o777):
    ti = _ti(name, mode)
    ti.type = tarfile.SYMTYPE
    ti.linkname = target
    tf.addfile(ti)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--core", help="deepsleep-core-linux-musl-arm64.tar.gz 的路径（默认去 release/ 找）")
    ap.add_argument("--no-kernel", action="store_true",
                    help="不带 Alpine 的 vmlinuz-virt / initramfs-virt。"
                         "默认会带 —— 这样解开就能直接 run-qemu.sh，不用再自己下内核。")
    a = ap.parse_args()

    ver = version()
    say("deepsleep · 最小 Linux 虚拟机  版本 %s" % ver)

    # 1) 找到 musl 版内核（Alpine 是 musl，必须用 musl 那一份）
    core_tar = a.core
    if not core_tar:
        cand = os.path.join(REL, "deepsleep-core-%s-linux-musl-arm64.tar.gz" % ver)
        if not os.path.isfile(cand):
            raise SystemExit("先跑一次 release/tools/mk-linux-core.py，或者用 --core 指定内核包。\n缺：" + cand)
        core_tar = cand
    say("  内核包：%s" % os.path.basename(core_tar))

    # 2) 下 Alpine 最小根文件系统
    mr = latest_minirootfs()
    alpine_bytes = fetch(MINIROOTFS_DIR + mr, mr)
    say("   %s  %.2f MB" % (mr, mb(len(alpine_bytes))))

    # 3) 把内核包里的东西读进内存
    want = {}
    with tarfile.open(core_tar, "r:gz") as t:
        for m in t.getmembers():
            if m.isfile():
                # 包内是 <name>/... 形式，剥掉顶层目录
                rel = m.name.split("/", 1)[1] if "/" in m.name else m.name
                if rel.startswith("ui/") or rel.startswith("web/") or rel.startswith("tools/") \
                        or rel in ("deepsleep-core",):
                    want[rel] = (t.extractfile(m).read(), 0o755 if rel == "deepsleep-core" else 0o644)

    # 4) 合成新的 rootfs tar.gz
    name = "deepsleep-vm-%s-arm64" % ver
    stage = os.path.join(os.environ.get("TEMP", "/tmp"), name)
    if os.path.isdir(stage):
        shutil.rmtree(stage)
    os.makedirs(stage)

    rootfs_tar = os.path.join(stage, "rootfs.tar.gz")
    say("  合成 rootfs …")
    # 这几个 Alpine 自带的文件我们下面要换成自己的版本。tar 是「后写的覆盖先写的」，
    # 追加也能work，但留两份同名条目会让解包工具行为不一致、排查时容易看错 —— 直接跳过。
    overridden = {"./etc/inittab", "./etc/issue"}
    with tarfile.open(rootfs_tar, "w:gz", compresslevel=6, format=tarfile.GNU_FORMAT) as out:
        # 4a) Alpine 原有内容原样搬过来（tar 里套 tar，不落盘，绕开 Windows 权限/软链问题）
        with tarfile.open(fileobj=io.BytesIO(alpine_bytes), mode="r:gz") as src:
            for m in src:
                if m.name in overridden:
                    continue
                data = src.extractfile(m) if m.isfile() else None
                out.addfile(m, data)

        # 4b) 我们的东西
        for d in (DS_DIR, DS_DIR + "/data", DS_DIR + "/ui", DS_DIR + "/web", DS_DIR + "/tools",
                  "var/log"):
            add_dir(out, d)

        for rel, (data, mode) in want.items():
            # deepsleep-core 单独加（要 755），这里跳过免得同一个文件在 tar 里出现两次
            if rel == "deepsleep-core":
                continue
            add_file(out, DS_DIR + "/" + rel, data, mode)
        if "deepsleep-core" in want:
            add_file(out, DS_DIR + "/deepsleep-core", want["deepsleep-core"][0], 0o755)

        # 4c) 启动脚本与系统配置
        add_file(out, "opt/deepsleep/boot.sh", BOOT_SH, 0o755)
        add_file(out, "etc/inittab", INITTAB)
        add_file(out, "etc/issue", ISSUE)
        add_file(out, "opt/deepsleep/deepsleep-core.sh", _launch_sh(), 0o755)

    # 5) 启动脚本 + 说明
    with io.open(os.path.join(stage, "run-qemu.sh"), "w", encoding="utf-8", newline="\n") as f:
        f.write(RUN_QEMU)
    with io.open(os.path.join(stage, "README.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write(VM_README)

    # 6) 内核（默认带；解开就能直接跑）
    if not a.no_kernel:
        for fn in ("vmlinuz-virt", "initramfs-virt"):
            try:
                b = fetch(MINIROOTFS_DIR + "netboot/" + fn, fn)
                with open(os.path.join(stage, fn), "wb") as f:
                    f.write(b)
                say("   %s  %.2f MB" % (fn, mb(len(b))))
            except Exception as e:
                say("   ✗ %s 没下下来（%s）—— 不影响，run-qemu.sh 会提示怎么单独下" % (fn, e))

    # 7) 打包
    out_tar = os.path.join(REL, name + ".tar.gz")
    with open(out_tar, "wb") as raw:
        with gzip.GzipFile(fileobj=raw, mode="wb", compresslevel=6, mtime=0) as gz:
            with tarfile.open(fileobj=gz, mode="w", format=tarfile.GNU_FORMAT) as t:
                for fn in sorted(os.listdir(stage)):
                    full = os.path.join(stage, fn)
                    ti = t.gettarinfo(full, arcname=name + "/" + fn)
                    ti.uid = ti.gid = 0
                    ti.uname = ti.gname = ""
                    ti.mode = 0o755 if fn.endswith(".sh") else 0o644
                    with open(full, "rb") as f:
                        t.addfile(ti, f)

    say("")
    say("   %s  %.2f MB" % (os.path.basename(out_tar), mb(os.path.getsize(out_tar))))
    say("   rootfs.tar.gz 解出来约 %.1f MB（Alpine + 内核）" %
        (mb(os.path.getsize(rootfs_tar))))
    return 0


def _launch_sh():
    """复用 mk-linux-core.py 里的启动脚本，两处保持一致（只有一份实现）。"""
    sys.path.insert(0, os.path.join(REL, "tools"))
    import importlib.util
    spec = importlib.util.spec_from_file_location(
        "mklc", os.path.join(REL, "tools", "mk-linux-core.py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod.LAUNCH_SH


if __name__ == "__main__":
    sys.exit(main())

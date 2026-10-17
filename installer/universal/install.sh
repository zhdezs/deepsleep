#!/bin/sh
# deepsleep 万能安装程序（macOS / Linux）
#
#   sh install.sh                 交互选择版本
#   sh install.sh --edition core  直接装内核版
#   sh install.sh --edition desktop
#   sh install.sh --dir ~/opt     换安装目录
#   sh install.sh --yes           全部默认，不问
#
# 设计取舍：
#   · macOS 只提供「内核版」。桌面版是 Photino + ad-hoc 签名，在 Apple 芯片上会被
#     Gatekeeper 判成「已损坏」直接打不开（签名只覆盖了 fat 包的一半），
#     所以 macOS 一律走内核版 —— 它是纯命令行 + 浏览器界面，不碰 .app / 签名这套。
#   · Linux 两个都能装：桌面版（有窗口）/ 内核版（服务器、SSH、想用浏览器访问时）。
#   · 包里自带 .NET 运行时（self-contained），用户不需要装 .NET。
set -eu

VERSION="__VERSION__"
BASE="https://github.com/zhdezs/deepsleep/releases/download/v${VERSION}"
# 本脚本在单文件安装包里是被 base64 解出来、用 `sh -` 管道执行的，
# 那种情况下 $0 不是文件路径，所以由外层把目录放进 DEEPSLEEP_HOME。
if [ -n "${DEEPSLEEP_HOME:-}" ]; then
  SRC_DIR=$DEEPSLEEP_HOME
else
  SRC_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
fi

EDITION=""
INSTALL_DIR=""
ASSUME_YES=0

while [ $# -gt 0 ]; do
  case "$1" in
    --edition) EDITION=${2:-}; shift 2 ;;
    --dir)     INSTALL_DIR=${2:-}; shift 2 ;;
    -y|--yes)  ASSUME_YES=1; shift ;;
    -h|--help)
      echo "用法: sh install.sh [--edition desktop|core] [--dir 安装目录] [--yes]"
      exit 0 ;;
    *) echo "未知参数：$1"; exit 2 ;;
  esac
done

say()  { printf '%s\n' "$*"; }
die()  { printf '\n× %s\n' "$*" >&2; exit 1; }

# ---------- 1. 认系统与架构 ----------
OS_RAW=$(uname -s)
ARCH_RAW=$(uname -m)

case "$OS_RAW" in
  Darwin) OS=macos ;;
  Linux)  OS=linux ;;
  *) die "这个文件只支持 macOS 和 Linux（当前：$OS_RAW）。Windows 上直接双击本文件即可。" ;;
esac

case "$ARCH_RAW" in
  arm64|aarch64) ARCH=arm64 ;;
  x86_64|amd64)  ARCH=x64 ;;
  *) die "不认识的 CPU 架构：$ARCH_RAW" ;;
esac

say ""
say "  deepsleep 安装程序 · v${VERSION}"
say "  系统 $OS_RAW ($ARCH_RAW) → 目标包 ${OS}-${ARCH}"
say ""

# ---------- 2. 选版本 ----------
if [ "$OS" = "macos" ] && [ "$EDITION" = "desktop" ]; then
  say "  macOS 没有桌面版（签名问题，装上也会被系统判成「已损坏」）。改为安装内核版。"
  EDITION="core"
fi

if [ -z "$EDITION" ]; then
  if [ "$OS" = "macos" ]; then
    # macOS 只有一个选项，不必问
    EDITION="core"
  else
    say "  请选择要安装的版本："
    say "    1) 桌面版  —— 有窗口，双击就能聊（默认）"
    say "    2) 内核版  —— 无窗口，命令行启动、用浏览器访问（适合服务器 / SSH）"
    printf "  输入 1 或 2 [1]: "
    if [ "$ASSUME_YES" = "1" ]; then
      EDITION="desktop"; say "1（默认）"
    else
      read -r ans || ans=""
      case "$ans" in
        2) EDITION="core" ;;
        *) EDITION="desktop" ;;
      esac
    fi
  fi
fi

[ "$EDITION" = "core" ] || [ "$EDITION" = "desktop" ] || die "版本只能是 desktop 或 core"

# ---------- 3. 找安装包 ----------
if [ "$EDITION" = "core" ]; then
  PKG="deepsleep-core-${VERSION}-${OS}-${ARCH}.tar.gz"
  APP_NAME="deepsleep-core"
else
  PKG="deepsleep-${VERSION}-${OS}-${ARCH}.tar.gz"
  APP_NAME="deepsleep"
fi

if [ -z "$INSTALL_DIR" ]; then
  INSTALL_DIR="$HOME/.local/opt/$APP_NAME"
fi

TMP=$(mktemp -d 2>/dev/null || mktemp -d -t deepsleep)
trap 'rm -rf "$TMP"' EXIT INT TERM

# ---------- 下载：GitHub 优先，太慢自动换 Gitee ----------
# 门槛 500 KB/s，连续 3 秒低于它就切源。
# GitHub 在国内经常「连得上但只有几十 KB/s」，傻等十分钟不如早点换。
MIN_SPEED=524288        # 字节/秒（curl --speed-limit 的单位）；524288 = 512 KiB ≈ 500 KB
SPEED_WINDOW=3

GH_BASE="https://github.com/zhdezs/deepsleep/releases/download/v${VERSION}"
GITEE_BASE="https://gitee.com/zhdezs/deepsleep/releases/download/v${VERSION}"
# 可以用环境变量覆盖下载源（内网镜像 / 自建 CDN / 测试用）
: "${DEEPSLEEP_GH_BASE:=$GH_BASE}"
: "${DEEPSLEEP_GITEE_BASE:=$GITEE_BASE}"
GH_BASE=$DEEPSLEEP_GH_BASE
GITEE_BASE=$DEEPSLEEP_GITEE_BASE
# Gitee 也下不动时，再退到 GitHub 加速镜像（和应用内更新器同一批）
MIRRORS="https://ghproxy.net/ https://gh-proxy.com/ https://hub.gitmirror.com/"

has_curl=0; command -v curl >/dev/null 2>&1 && has_curl=1
has_wget=0; command -v wget >/dev/null 2>&1 && has_wget=1

# 下到 $2；太慢或失败返回非 0，并清掉半截文件（免得好包被垃圾覆盖）
fetch_one() {
  _url=$1; _out=$2
  rm -f "$_out"
  if [ "$has_curl" = "1" ]; then
    if curl -fL --retry 0 --connect-timeout 20 \
            --speed-limit "$MIN_SPEED" --speed-time "$SPEED_WINDOW" \
            -o "$_out" "$_url" 2>/dev/null; then
      return 0
    fi
    rm -f "$_out"
    return 1
  fi
  # wget 没有「速度低于 x 就放弃」的参数，自己盯着：
  # 后台下，等 SPEED_WINDOW+1 秒看下了多少，不够就杀掉换源。
  wget -q --tries=1 --timeout=20 -O "$_out" "$_url" >/dev/null 2>&1 &
  _pid=$!
  sleep $((SPEED_WINDOW + 1))
  if kill -0 "$_pid" 2>/dev/null; then
    _got=$(wc -c < "$_out" 2>/dev/null || echo 0)
    if [ "${_got:-0}" -lt $((MIN_SPEED * SPEED_WINDOW)) ]; then
      kill "$_pid" 2>/dev/null || true
      wait "$_pid" 2>/dev/null || true
      rm -f "$_out"
      return 1
    fi
  fi
  wait "$_pid" 2>/dev/null
  if [ -s "$_out" ]; then return 0; fi
  rm -f "$_out"
  return 1
}

# 优先用安装包旁边的本地文件（离线安装 / U 盘拷贝），没有再下载
LOCAL_PKG="$SRC_DIR/$PKG"
WORK="$TMP/$PKG"
if [ -f "$LOCAL_PKG" ]; then
  say "  使用本地安装包：$LOCAL_PKG"
  cp "$LOCAL_PKG" "$WORK"
else
  if [ "$has_curl" = "0" ] && [ "$has_wget" = "0" ]; then
    die "这台机器没有 curl 也没有 wget。请把 $PKG 放到本文件旁边再运行（离线安装）。"
  fi
  say "  从 GitHub 下载：$GH_BASE/$PKG"
  got=0
  if fetch_one "$GH_BASE/$PKG" "$WORK"; then
    got=1
  else
    say "  GitHub 速度低于 500 KB/s 或连不上，改从 Gitee 下载…"
    if fetch_one "$GITEE_BASE/$PKG" "$WORK"; then
      got=1
      say "  已从 Gitee 下载完成"
    else
      for m in $MIRRORS; do
        say "  Gitee 也不行，试加速镜像：$m"
        if fetch_one "$m$GH_BASE/$PKG" "$WORK"; then
          got=1
          say "  已从加速镜像下载完成"
          break
        fi
      done
    fi
  fi
  if [ "$got" != "1" ]; then
    die "下载失败（GitHub / Gitee / 镜像都试过了）。可以手动下载后放到本文件旁边：
    $GH_BASE/$PKG"
  fi
fi

# ---------- 4. 解压安装 ----------
say "  安装到 $INSTALL_DIR"
mkdir -p "$INSTALL_DIR"
tar -xzf "$WORK" -C "$INSTALL_DIR" || die "解压失败，包可能不完整"

# macOS：下载来的文件带 quarantine 属性，不清掉会被 Gatekeeper 拦下来
# （用户看到的是「无法打开，因为无法验证开发者」，甚至直接闪退）。
if [ "$OS" = "macos" ] && command -v xattr >/dev/null 2>&1; then
  xattr -dr com.apple.quarantine "$INSTALL_DIR" 2>/dev/null || true
fi

BIN_DIR="$HOME/.local/bin"
mkdir -p "$BIN_DIR"

if [ "$EDITION" = "core" ]; then
  CORE_BIN="$INSTALL_DIR/deepsleep-core"
  [ -f "$CORE_BIN" ] || die "包里没找到 deepsleep-core，安装包可能不对"
  chmod +x "$CORE_BIN" 2>/dev/null || true

  LAUNCHER="$BIN_DIR/deepsleep-core"
  cat > "$LAUNCHER" <<EOF
#!/bin/sh
cd "$INSTALL_DIR" || exit 1
exec "$CORE_BIN" "\$@"
EOF
  chmod +x "$LAUNCHER"

  say ""
  say "  ✓ 内核版已装好"
  say "    启动：$LAUNCHER"
  say ""
  say "  第一次启动会打印本机地址和配对令牌，浏览器打开就能用。"
  if [ "$OS" = "macos" ]; then
    say "  想双击启动：在「终端」里运行上面那行，或把它存成 .command 文件。"
  fi
else
  DESK_BIN="$INSTALL_DIR/deepsleep"
  [ -f "$DESK_BIN" ] || die "包里没找到 deepsleep，安装包可能不对"
  chmod +x "$DESK_BIN" 2>/dev/null || true
  chmod +x "$INSTALL_DIR/deepsleep.sh" 2>/dev/null || true

  LAUNCHER="$BIN_DIR/deepsleep"
  cat > "$LAUNCHER" <<EOF
#!/bin/sh
cd "$INSTALL_DIR" || exit 1
exec "$DESK_BIN" "\$@"
EOF
  chmod +x "$LAUNCHER"

  # Linux 桌面：加一个应用菜单项，省得每次都开终端
  if [ "$OS" = "linux" ] && [ -d "$HOME/.local/share/applications" ]; then
    cat > "$HOME/.local/share/applications/deepsleep.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=deepsleep
Comment=AI 助手
Exec=$DESK_BIN
Icon=$INSTALL_DIR/pet.png
Terminal=false
Categories=Utility;
EOF
    say "  ✓ 已添加应用菜单项"
  fi

  say ""
  say "  ✓ 桌面版已装好"
  say "    启动：$LAUNCHER"
fi

case ":$PATH:" in
  *":$BIN_DIR:"*) ;;
  *) say ""
     say "  提示：$BIN_DIR 不在 PATH 里。把这一行加到 ~/.zshrc 或 ~/.bashrc："
     say "        export PATH=\"\$HOME/.local/bin:\$PATH\"" ;;
esac

say ""
say "  卸载：rm -rf \"$INSTALL_DIR\" \"$LAUNCHER\""
say ""

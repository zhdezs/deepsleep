#!/usr/bin/env bash
# 把 deepsleep 的 macOS 发布目录打成 deepsleep.app，再用 hdiutil 压成 .dmg。
# 只能在 macOS 上跑（Windows / Linux 没有 hdiutil 和 codesign），所以由
# .github/workflows/macos-dmg.yml 在 macos-14 runner 上调用；本机要打就自己拿 Mac 跑。
#
# 用法：mk-macos-dmg.sh <输入 tar.gz> <osx-arm64|osx-x64> <版本> <输出目录> [图标 ico]
#
# ⚠ 这个脚本以前"能跑完但可能打了个坏包"：签名那句带了 `|| true`，签失败也照样出 .dmg；
#   而且签完之后又 `cp -R` 了一遍（复制有可能丢签名相关元数据）。
#   现在：架构先核对 → 在最终位置上签名 → 逐个校验签名，任何一步不达标就直接失败，
#         宁可这次发布不出 macOS 包，也不给用户一个双击没反应的 .app。
set -euo pipefail

TAR="${1:?输入 tar.gz}"
RID="${2:?osx-arm64 或 osx-x64}"
VER="${3:?版本号}"
OUT="${4:?输出目录}"
ICO="${5:-}"

case "$RID" in
  osx-arm64) WANT_CPU="arm64" ;;
  osx-x64)   WANT_CPU="x86_64" ;;
  *) echo "未知 RID：$RID（只认 osx-arm64 / osx-x64）" >&2; exit 2 ;;
esac

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/root" "$OUT"
tar -xzf "$TAR" -C "$WORK/root"

APP="$WORK/deepsleep.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$WORK/root/." "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/deepsleep" 2>/dev/null || true
chmod +x "$APP/Contents/MacOS/deepsleep.sh" 2>/dev/null || true
rm -f "$APP/Contents/MacOS/Deepsleep.Core.pdb"

# ---------------------------------------------------------------- 1. 架构核对
# 交叉编译出来的东西最容易出的岔子就是"名字写着 arm64、里面其实是 x64"（或反过来）。
# 这种包在对应机器上就是双击没反应，而且现场极难看出来。这里直接读 Mach-O 头核对，
# 顺便确认 Photino.Native.dylib 真的含目标架构（它是 fat 二进制，两种都该有）。
if ! python3 - "$APP/Contents/MacOS/deepsleep" "$APP/Contents/MacOS/Photino.Native.dylib" "$WANT_CPU" <<'PYEOF'
import struct, sys

CPU = {7: "x86", 0x01000007: "x86_64", 12: "arm", 0x0100000C: "arm64"}
exe, dylib, want = sys.argv[1], sys.argv[2], sys.argv[3]

def archs(path):
    """返回 Mach-O 里的架构名列表（thin 一个，fat 多个）。"""
    b = open(path, "rb").read(4096)
    magic_be = struct.unpack(">I", b[:4])[0]
    if magic_be in (0xcafebabe, 0xcafebabf):                 # fat（大端）
        n = struct.unpack(">I", b[4:8])[0]
        return [CPU.get(struct.unpack(">i", b[8 + 20 * i:12 + 20 * i])[0], "?") for i in range(n)]
    magic_le = struct.unpack("<I", b[:4])[0]
    if magic_le in (0xfeedface, 0xfeedfacf):                # thin（小端）
        return [CPU.get(struct.unpack("<i", b[4:8])[0], "?")]
    return ["?"]

e = archs(exe)
d = archs(dylib)
print("    可执行文件架构 %s / Photino.Native.dylib 架构 %s" % (", ".join(e), ", ".join(d)))
bad = False
if e != [want]:
    print("  ✗ 可执行文件的架构是 %s，但这份包应该只有 %s" % (", ".join(e), want))
    bad = True
if want not in d:
    print("  ✗ Photino.Native.dylib 里没有 %s（只有 %s）—— 窗口根本起不来" % (want, ", ".join(d)))
    bad = True
sys.exit(1 if bad else 0)
PYEOF
then
  echo "  ✗ $RID 包的架构不对，停在这里（打出来也没法用）" >&2
  exit 1
fi

# ---------------------------------------------------------------- 2. 图标
ICON_LINE=""
if [ -n "$ICO" ] && [ -f "$ICO" ]; then
  if python3 - "$ICO" "$WORK/icon.png" <<'PYEOF'
import struct, sys
b = open(sys.argv[1], "rb").read()
n = struct.unpack("<HHH", b[:6])[2]
best = None
for i in range(n):
    w, _h, _c, _r, _p, _b, sz, off = struct.unpack("<BBBBHHII", b[6 + 16 * i:22 + 16 * i])
    if b[off:off + 8] != b"\x89PNG\r\n\x1a\n":
        continue
    px = w or 256
    if best is None or px > best[0]:
        best = (px, off, sz)
if not best:
    raise SystemExit("ico 里没有 PNG")
open(sys.argv[2], "wb").write(b[best[1]:best[1] + best[2]])
PYEOF
  then
    SET="$WORK/deepsleep.iconset"
    mkdir -p "$SET"
    for s in 16 32 64 128 256 512; do
      sips -z $s $s "$WORK/icon.png" --out "$SET/icon_${s}x${s}.png" >/dev/null
    done
    cp "$SET/icon_32x32.png" "$SET/icon_16x16@2x.png"
    cp "$SET/icon_64x64.png" "$SET/icon_32x32@2x.png"
    cp "$SET/icon_256x256.png" "$SET/icon_128x128@2x.png"
    cp "$SET/icon_512x512.png" "$SET/icon_256x256@2x.png"
    sips -z 1024 1024 "$WORK/icon.png" --out "$SET/icon_512x512@2x.png" >/dev/null
    if iconutil -c icns "$SET" -o "$APP/Contents/Resources/deepsleep.icns"; then
      ICON_LINE='  <key>CFBundleIconFile</key><string>deepsleep</string>'
    fi
  fi
fi

# ---------------------------------------------------------------- 3. Info.plist
# LSMinimumSystemVersion 必须写 **14.0**，这是实测出来的真实下限，不是拍的：
#   用 Mach-O 的 LC_BUILD_VERSION 读出来——
#     · deepsleep（.NET apphost）     minos = 12.0
#     · Photino.Native.dylib（两份）  minos = 14.0   ← 真正的瓶颈
#     · Info.plist 原来写的          LSMinimumSystemVersion = 11.0
#   dyld 会拒绝加载"为更高版本系统构建"的 dylib，所以写在 11/12 上跑：
#   系统以为没问题、照常安装，实际窗口库加载不了 —— 又是一次"装上了但起不来"。
#   写高一点只是挡住装不上的机器，写低了才是真的害人。
cat > "$APP/Contents/Info.plist" <<PLISTEOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>deepsleep</string>
  <key>CFBundleDisplayName</key><string>deepsleep</string>
  <key>CFBundleIdentifier</key><string>io.github.zhdezs.deepsleep</string>
  <key>CFBundleExecutable</key><string>deepsleep</string>
  <key>CFBundleVersion</key><string>$VER</string>
  <key>CFBundleShortVersionString</key><string>$VER</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSRequiresAquaSystemAppearance</key><false/>
  <!-- 界面是本机页面 + 内核服务跑在 http://127.0.0.1:<端口>；
       WKWebView 受 ATS 管，明确放行本机明文访问，免得界面白屏。 -->
  <key>NSAppTransportSecurity</key>
  <dict>
    <key>NSAllowsLocalNetworking</key><true/>
  </dict>
$ICON_LINE
</dict>
</plist>
PLISTEOF

# ---------------------------------------------------------------- 4. 摆进 DMG 暂存目录
# 顺序很重要：先摆到位、**在最终位置上**签名，中间不再复制。
# 以前是"签完再 cp -R"，复制有可能把签名相关元数据丢掉，而且没人检查结果。
DMGROOT="$WORK/dmg"
mkdir -p "$DMGROOT"
cp -R "$APP" "$DMGROOT/"
ln -s /Applications "$DMGROOT/Applications"

# 用户第一次打开最常见的拦路虎是 Gatekeeper：从浏览器下载的 .dmg 带隔离标记，
# 而这个包只有临时签名（没有 Apple 开发者证书），macOS 会直接说"已损坏"。
# 把解决办法放在 dmg 里跟 app 并排，用户不用回官网翻。
cat > "$DMGROOT/使用说明.txt" <<'TXTEOF'
deepsleep · macOS 版

安装：把左边的 deepsleep 拖进右边的「应用程序」文件夹，然后到「启动台」或
「应用程序」里打开它。

需要 macOS 14 或更高版本（这个下限来自界面窗口库本身，系统低了加载不了它）。

────────────────────────────────────────────────────────────
第一次打不开怎么办？两种提示要分开对待。

【情况一】提示「无法验证开发者」/「Apple 无法检查其是否包含恶意软件」

这个版本是免费发布的，没有买 Apple 的开发者证书（一年 $99），所以只有临时签名。
macOS 从网上下的 App 一律先拦一道，属于正常现象，不是包坏了。任选一种办法（一次就好）：

  办法一：在「访达」里按住 Control 点（或右键）deepsleep 图标 → 选「打开」→
        弹出提示里再点一次「打开」。
  办法二：先双击一次让它被拦，然后到「系统设置 → 隐私与安全性」，
        往下拉到「安全性」，点「仍要打开」。

【情况二】提示「已损坏，无法打开。你应该将它移到废纸篓」

这个不是包坏了，是签名无效 —— 而且这种提示**右键打开也没用**，别白试。
最省事的是重新签一次名（复制下面四行到「终端」一次执行）：

    APP=/Applications/deepsleep.app
    sudo xattr -cr "$APP"
    sudo codesign --force --sign - "$APP/Contents/MacOS/Photino.Native.dylib"
    sudo codesign --force --sign - "$APP"

然后 open "$APP" 或直接在访达里双击就能打开了。

────────────────────────────────────────────────────────────
其它

· Apple 芯片（M 系列）装 osx-arm64 这个包，Intel 机器装 osx-x64 那个。
· 数据默认放在 ~/Library/Application Support/deepsleep。
· 启动不了时，它自己的日志也在这个目录：shell.log（新版还会有 crash.log），
  把里面的内容发出来就能定位问题。
· 只想跑服务、不开窗口（服务器 / SSH）：终端里执行
      /Applications/deepsleep.app/Contents/MacOS/deepsleep --headless
TXTEOF

# ---------------------------------------------------------------- 5. 签名（失败就停）
# 没有 Apple 开发者证书，只能 ad-hoc 签名（--sign -）。
# Apple Silicon 上"未签名"的二进制会被内核当场杀掉，表现是双击图标弹一下就没了、
# 连报错都不给 —— 所以这一步绝不能像以前那样用 `|| true` 糊过去。
SIGNED=1
xattr -cr "$DMGROOT/deepsleep.app" 2>/dev/null || true

# 不用 --deep（Apple 已废弃且行为不透明），改成显式签内层再签外层，结果可预期。
echo "  签名中（ad-hoc）…"
if ! codesign --force --sign - --timestamp=none \
       "$DMGROOT/deepsleep.app/Contents/MacOS/Photino.Native.dylib"; then
  echo "  ✗ 内层 Photino.Native.dylib 签名失败 —— 这台机器上 .app 一定起不来" >&2
  echo "    （最常见原因：runner 上 codesign 被 keychain 策略挡住，或文件被其他进程占用）" >&2
  SIGNED=0
fi

# ⚠ Photino 自带的这份 dylib 是 **fat 二进制，而它上游只给 arm64 分片签了名，
#   x86_64 分片是光的**（实测：两个包里 md5 完全一样，arm64 分片有 LC_CODE_SIGNATURE、
#   x86_64 分片没有）。嵌套代码"签一半"会让外层 bundle 的封印对不上，
#   Gatekeeper 报的就是「已损坏，无法打开」—— 而且这个提示右键绕过无效，
#   跟"未签名/未验证开发者"完全不是一回事，用户容易误判成包坏了。
#   `codesign --force --sign -` 会把每个分片都签上，所以这里签完立刻验证一遍。
if [ "$SIGNED" = 1 ]; then
  echo "    内层 dylib 重新签名后的校验："
  if ! codesign --verify --strict --verbose=2 \
         "$DMGROOT/deepsleep.app/Contents/MacOS/Photino.Native.dylib" 2>&1 | sed 's/^/      /'; then
    echo "  ✗ Photino.Native.dylib 签完仍然验证不过（很可能还有分片没签上）" >&2
    SIGNED=0
  fi
  codesign -dv --verbose=4 "$DMGROOT/deepsleep.app/Contents/MacOS/Photino.Native.dylib" 2>&1 \
    | sed 's/^/      /' || true
fi

if [ "$SIGNED" = 1 ] && ! codesign --force --sign - --timestamp=none "$DMGROOT/deepsleep.app"; then
  echo "  ✗ deepsleep.app 签名失败 —— 不能把这个包发出去" >&2
  SIGNED=0
fi
[ "$SIGNED" = 1 ] || exit 1

# ---------------------------------------------------------------- 6. 校验签名
# 说明：这里用 codesign --verify 而不是 spctl —— spctl 认的是 Apple 签发的证书，
# ad-hoc 签名一定被它判 rejected，那是预期的，只打印出来供参考，不作为失败条件。
if ! codesign --verify --strict --verbose=2 "$DMGROOT/deepsleep.app" 2>&1 | sed 's/^/    /'; then
  echo "  ✗ 签名校验没通过 —— 这样的包发出去用户双击就会失败" >&2
  exit 1
fi
if ! codesign --verify --deep --strict "$DMGROOT/deepsleep.app"; then
  echo "  ✗ 嵌套代码签名校验没通过" >&2
  exit 1
fi
echo "    ✓ 签名校验通过"
echo "    签名信息："
codesign -dv "$DMGROOT/deepsleep.app" 2>&1 | sed 's/^/      /' || true
echo "    Gatekeeper 评估（ad-hoc 签名被判 rejected 属预期，不是故障）："
spctl -a -vvv -t exec "$DMGROOT/deepsleep.app" 2>&1 | sed 's/^/      /' || true

# ---------------------------------------------------------------- 7. 出 dmg
hdiutil create -volname "deepsleep $VER" -srcfolder "$DMGROOT" -ov -format UDZO \
  -fs HFS+ "$OUT/deepsleep-$VER-$RID.dmg"
echo "  $OUT/deepsleep-$VER-$RID.dmg  $(du -h "$OUT/deepsleep-$VER-$RID.dmg" | cut -f1)"

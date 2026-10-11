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

# ---------------------------------------------------------------- 0. 工具自检
# 打包中途失败在日志里只剩一句 "exit code 1"，最难查。开头先把关键工具在不在打出来，
# 下次一眼就能分清是"工具缺了"还是"包不对"。
echo "  ── 工具自检 ──"
for t in tar lipo file codesign hdiutil sips iconutil plutil python3; do
  p="$(command -v "$t" 2>/dev/null || true)"
  printf "    %-10s %s\n" "$t" "${p:-（没有）}"
done

# ---------------------------------------------------------------- 1. 架构核对
# 交叉编译出来的东西最容易出的岔子就是"名字写着 arm64、里面其实是 x64"（或反过来）。
# 这种包在对应机器上就是双击没反应，而且现场极难看出来。
#
# ⚠ 这里刻意**不用 python3**：lipo 是 macOS 自带的（Xcode CLT 提供），一定在；
#   而 python3 在 macOS 上并不保证存在。第一版用了 python3，还写成"取不到就 exit 1"，
#   结果整条流水线 1 秒内就挂了 —— 旧脚本里 python3 只用于图标，失败只是跳过，
#   所以这个依赖从来没暴露过。**关键校验用的工具，必须是这个平台上一定有那个。**
echo "  ── 架构核对（$RID 应该是 $WANT_CPU）──"
EXE_ARCHS="$(lipo -archs "$APP/Contents/MacOS/deepsleep" 2>/dev/null || echo '')"
DYLIB_ARCHS="$(lipo -archs "$APP/Contents/MacOS/Photino.Native.dylib" 2>/dev/null || echo '')"
echo "    deepsleep 架构          = ${EXE_ARCHS:-（读不出来）}"
echo "    Photino.Native.dylib 架构 = ${DYLIB_ARCHS:-（读不出来）}"
if [ -z "$EXE_ARCHS" ]; then
  echo "    （file 看一眼：$(file "$APP/Contents/MacOS/deepsleep" 2>/dev/null || echo 取不到)）"
fi
ARCH_BAD=""
[ "$EXE_ARCHS" = "$WANT_CPU" ] || ARCH_BAD="可执行文件架构是 ${EXE_ARCHS:-空}，应该是 $WANT_CPU"
case " $DYLIB_ARCHS " in
  *" $WANT_CPU "*) ;;
  *) ARCH_BAD="${ARCH_BAD}${ARCH_BAD:+；}Photino.Native.dylib 里没有 $WANT_CPU（只有 ${DYLIB_ARCHS:-空}）" ;;
esac
if [ -n "$ARCH_BAD" ]; then
  echo "  ✗ $RID 包的架构不对，停在这里（打出来也没法用）：$ARCH_BAD" >&2
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
    # ⚠ 图标纯粹是外观：这里任何一步失败都只跳过图标，绝不能让整个打包挂掉。
    #   所以整段塞进一个子 shell 当 if 的条件（if 的条件上下文里 set -e 不生效）。
    if (
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
      iconutil -c icns "$SET" -o "$APP/Contents/Resources/deepsleep.icns"
    ); then
      ICON_LINE='  <key>CFBundleIconFile</key><string>deepsleep</string>'
      echo "    .icns 图标已生成"
    else
      echo "    图标没做出来，跳过（只是没有自定义图标，不影响程序能不能跑）"
    fi
  else
    echo "    从 .ico 里取 PNG 失败（python3：$(command -v python3 2>/dev/null || echo 没有)），跳过图标"
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
# cp -R 不带 -p 时，新建文件的权限会被 umask 削一道。macOS 默认 umask 022 不会碰执行位，
# 但别赌这个 —— 执行位一丢，App 双击就没反应，而且现场极难看懂。
# 必须在**签名之前**补，签完再改权限会让签名信息对不上。
chmod +x "$DMGROOT/deepsleep.app/Contents/MacOS/deepsleep" 2>/dev/null || true
chmod +x "$DMGROOT/deepsleep.app/Contents/MacOS/deepsleep.sh" 2>/dev/null || true
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

# ---------------------------------------------------------------- 6.5 交付前把 .app 验一遍
# 在**做成 .dmg 之前**把该验的都验掉：这里的文件是明文目录，不用挂载磁盘映像，
# 哪一项不对看得最清楚。
#
# ⚠ 失败一律用 `::error::` 前缀 —— 它会变成 GitHub Actions 的**注解**。
#   这一点很关键：Actions 的日志正文在 API 里读不到（要鉴权），但注解能直接读到，
#   所以"把诊断写进注解"是让远端失败可被远程诊断的唯一可靠办法。
VERIFY_BAD=0
vfail() { echo "::error::$RID：$1"; VERIFY_BAD=1; }

echo "  ── 交付前校验（$RID）──"
APP_IN="$DMGROOT/deepsleep.app"

# 1) 关键文件必须在（少任何一个，App 都起不来）
for f in Contents/Info.plist Contents/MacOS/deepsleep Contents/MacOS/Photino.Native.dylib \
         Contents/MacOS/ui/index.html Contents/MacOS/web/core/rd.html; do
  [ -f "$APP_IN/$f" ] || vfail "缺文件 $f"
done
# 2) 可执行位
[ -x "$APP_IN/Contents/MacOS/deepsleep" ] || vfail "Contents/MacOS/deepsleep 没有可执行位"
# 3) 架构
A="$(lipo -archs "$APP_IN/Contents/MacOS/deepsleep" 2>/dev/null || echo '')"
[ "$A" = "$WANT_CPU" ] || vfail "可执行文件架构是 ${A:-空}，应该是 $WANT_CPU"
DA="$(lipo -archs "$APP_IN/Contents/MacOS/Photino.Native.dylib" 2>/dev/null || echo '')"
case " $DA " in *" $WANT_CPU "*) ;; *) vfail "Photino.Native.dylib 里没有 $WANT_CPU（只有 ${DA:-空}）" ;; esac
# 4) 签名：整包 + 内层 dylib（dylib 是"签一半"的来源，单独再验一次）
if ! codesign --verify --deep --strict "$APP_IN" 2>&1 | tail -3; then
  vfail "签名校验没通过（用户那边会提示「已损坏」）"
fi
if ! codesign --verify --strict "$APP_IN/Contents/MacOS/Photino.Native.dylib" 2>&1 | tail -3; then
  vfail "内层 Photino.Native.dylib 签名不完整（多半有分片没签上）"
fi
# 5) 声明的最低系统版本不能低于窗口库的真实要求（实测 14.0）
MINOS="$(plutil -extract LSMinimumSystemVersion raw -o - "$APP_IN/Contents/Info.plist" 2>/dev/null || echo '')"
echo "    LSMinimumSystemVersion = ${MINOS:-读不到}"
if [ -z "$MINOS" ]; then
  vfail "Info.plist 里读不到 LSMinimumSystemVersion"
elif ! awk -v a="$MINOS" 'BEGIN{ exit !(a + 0 >= 14.0) }'; then
  vfail "LSMinimumSystemVersion=$MINOS 低于实际要求 14.0，装了会起不来"
fi
# 6) 给用户的说明要跟着包走
[ -f "$DMGROOT/使用说明.txt" ] || vfail "dmg 里没有 使用说明.txt"
# Applications 是**指向目录的符号链接**：这里只能用 -L，用 -f 会永远判假
[ -L "$DMGROOT/Applications" ] || vfail "dmg 里没有 Applications 快捷方式（用户拖不进去）"

if [ "$VERIFY_BAD" != 0 ]; then
  echo "  ✗ $RID 校验没过，不出这个包（出了用户也是双击没反应）" >&2
  exit 1
fi
echo "    ✓ 关键文件 / 架构 / 签名 / 最低系统版本 都通过"

# ---------------------------------------------------------------- 7. 出 dmg
hdiutil create -volname "deepsleep $VER" -srcfolder "$DMGROOT" -ov -format UDZO \
  -fs HFS+ "$OUT/deepsleep-$VER-$RID.dmg"
echo "  $OUT/deepsleep-$VER-$RID.dmg  $(du -h "$OUT/deepsleep-$VER-$RID.dmg" | cut -f1)"

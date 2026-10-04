#!/usr/bin/env bash
# 把 deepsleep 的 macOS 发布目录打成 deepsleep.app，再用 hdiutil 压成 .dmg。
# 只能在 macOS 上跑（Windows / Linux 没有 hdiutil 和 codesign），所以由
# .github/workflows/macos-dmg.yml 在 macos-14 runner 上调用；本机要打就自己拿 Mac 跑。
#
# 用法：mk-macos-dmg.sh <输入 tar.gz> <osx-arm64|osx-x64> <版本> <输出目录> [图标 ico]
set -euo pipefail

TAR="${1:?输入 tar.gz}"
RID="${2:?osx-arm64 或 osx-x64}"
VER="${3:?版本号}"
OUT="${4:?输出目录}"
ICO="${5:-}"

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

# 图标：从 src/deepsleep.ico 里挑出最大的那张 PNG，再用 iconutil 打成 .icns
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
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSRequiresAquaSystemAppearance</key><false/>
$ICON_LINE
</dict>
</plist>
PLISTEOF

# 没有 Apple 开发者证书，只能做 ad-hoc 签名（arm64 上没签名会被系统直接杀掉）
xattr -cr "$APP" 2>/dev/null || true
codesign --force --deep --sign - "$APP" 2>/dev/null || true

DMGROOT="$WORK/dmg"
mkdir -p "$DMGROOT"
cp -R "$APP" "$DMGROOT/"
ln -s /Applications "$DMGROOT/Applications"

hdiutil create -volname "deepsleep $VER" -srcfolder "$DMGROOT" -ov -format UDZO \
  -fs HFS+ "$OUT/deepsleep-$VER-$RID.dmg"
echo "  $OUT/deepsleep-$VER-$RID.dmg  $(du -h "$OUT/deepsleep-$VER-$RID.dmg" | cut -f1)"

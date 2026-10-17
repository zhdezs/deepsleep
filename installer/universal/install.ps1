# deepsleep 万能安装程序（Windows）
#
#   install.cmd              交互选择
#   install.cmd -Core        直接装内核版
#   install.cmd -Desktop     直接装桌面版
#   install.cmd -Dir D:\x    换安装目录
#
# 说明：
#   · 桌面版走官方 Setup.exe（带开始菜单、卸载项），不自己造一套安装逻辑。
#   · 内核版是绿色目录：解压到指定位置 + 建一个启动快捷方式，删目录即卸载。
#   · 包自带 .NET 运行时，不需要装 .NET。
[CmdletBinding()]
param(
  [switch]$Core,
  [switch]$Desktop,
  [string]$Dir = "",
  [switch]$Yes
)

$ErrorActionPreference = "Stop"
# Expand-Archive / Invoke-WebRequest 的进度条会把自己的刷新帧混进输出里，
# 中文本来就窄，结果整个控制台被冲成一片乱码。直接关掉。
$ProgressPreference = "SilentlyContinue"
$Version = "__VERSION__"
$Base = "https://github.com/zhdezs/deepsleep/releases/download/v$Version"
# 这个脚本有两种跑法：
#   1) 直接执行 install.ps1 —— 用自身所在目录当「本地安装包」查找目录
#   2) 被塞进单文件安装包里，用 -EncodedCommand 跑 —— 那时没有 $MyInvocation.Path，
#      由外层批处理把目录放在 DEEPSLEEP_HOME 里传进来
$Here = if ($env:DEEPSLEEP_HOME) { $env:DEEPSLEEP_HOME.TrimEnd('\') }
        else { Split-Path -Parent $MyInvocation.MyCommand.Path }

function Say($t) { Write-Host $t }
function Die($t) { Write-Host ""; Write-Host ("x " + $t) -ForegroundColor Red; exit 1 }

# 用 .NET 的解压 API，不用 Expand-Archive：
# Expand-Archive 会刷进度条，它的刷新帧会把中文输出冲成一片乱码
# （ProgressPreference=SilentlyContinue 在 5.1 上压不干净）。这个 API 没有任何输出。
function Expand-Zip($zip, $dest) {
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $dest)
}

# ---------- 1. 认系统与架构 ----------
$archRaw = $env:PROCESSOR_ARCHITECTURE
if (-not $archRaw) { $archRaw = "AMD64" }
$isWin11 = [Environment]::OSVersion.Version.Major -ge 10
$arch = if ($archRaw -eq "ARM64") { "arm64" } else { "x64" }

Say ""
Say "  deepsleep 安装程序 · v$Version"
Say "  Windows ($archRaw) → 目标 $arch"
if ($arch -eq "arm64") {
  Say ""
  Say "  检测到 Windows on ARM。目前 Windows 只发布了 x64 版本，"
  Say "  ARM 机器上会由系统模拟运行（能跑，但比原生慢）。"
}
Say ""

# ---------- 2. 选版本 ----------
if ($Core -and $Desktop) { Die "只能选一个：-Core 或 -Desktop" }
$edition = ""
if ($Core) { $edition = "core" }
elseif ($Desktop) { $edition = "desktop" }

if (-not $edition) {
  Say "  请选择要安装的版本："
  Say "    1) 桌面版  —— 有窗口，双击就能聊（默认）"
  Say "    2) 内核版  —— 无窗口，命令行启动、浏览器访问（适合服务器 / 想用网页版）"
  Write-Host "  输入 1 或 2 [1]: " -NoNewline
  if ($Yes) { $edition = "desktop"; Say "1（默认）" }
  else {
    $ans = Read-Host
    $edition = if ($ans -eq "2") { "core" } else { "desktop" }
  }
}

# ---------- 3. 定位安装包 ----------
if ($edition -eq "core") {
  $pkg = "deepsleep-core-$Version-win-x64.zip"
  if (-not $Dir) { $Dir = Join-Path $env:LOCALAPPDATA "Programs\deepsleep-core" }
} else {
  $pkg = "deepsleep-$Version-win-x64.zip"
  if (-not $Dir) { $Dir = Join-Path $env:LOCALAPPDATA "Programs\deepsleep" }
}

$local = Join-Path $Here $pkg
$tmp = Join-Path $env:TEMP $pkg

# ---------- 3b. 下载：GitHub 优先，太慢自动换 Gitee ----------
# 门槛 500 KB/s，连续 3 秒低于它就切源。GitHub 在国内经常「连得上但只有几十 KB/s」。
# 可以用环境变量覆盖下载源（内网镜像 / 自建 CDN / 测试用）
$Base = if ($env:DEEPSLEEP_GH_BASE) { $env:DEEPSLEEP_GH_BASE } else { $Base }
$GiteeBase = "https://gitee.com/zhdezs/deepsleep/releases/download/v$Version"
if ($env:DEEPSLEEP_GITEE_BASE) { $GiteeBase = $env:DEEPSLEEP_GITEE_BASE }
$Mirrors = @("https://ghproxy.net/", "https://gh-proxy.com/", "https://hub.gitmirror.com/")
$MinSpeed = 524288      # 字节/秒；524288 = 512 KiB ≈ 500 KB
$SpeedWindow = 3

# 用 curl.exe（Win10 1803+ 自带）下载：它原生支持「速度低于阈值就放弃」。
# 下不了或太慢返回 $false，并删掉半截文件。
function Fetch-One([string]$Url, [string]$Out) {
  if (Test-Path $Out) { Remove-Item $Out -Force -ErrorAction SilentlyContinue }
  $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
  if ($curl) {
    & $curl.Source -fL --retry 0 --connect-timeout 20 `
        --speed-limit $MinSpeed --speed-time $SpeedWindow -o $Out $Url 2>$null
    if ($LASTEXITCODE -eq 0 -and (Test-Path $Out) -and (Get-Item $Out).Length -gt 0) { return $true }
    Remove-Item $Out -Force -ErrorAction SilentlyContinue
    return $false
  }
  # 没有 curl.exe（很老的系统）：只能直下，不做速度判断
  try {
    $ProgressPreference = "SilentlyContinue"
    Invoke-WebRequest -Uri $Url -OutFile $Out -UseBasicParsing -TimeoutSec 300
    return $true
  } catch {
    Remove-Item $Out -Force -ErrorAction SilentlyContinue
    return $false
  }
}

if (Test-Path $local) {
  Say "  使用本地安装包：$local"
  $zip = $local
} else {
  Say "  从 GitHub 下载：$Base/$pkg"
  $zip = $tmp
  if (-not (Fetch-One "$Base/$pkg" $zip)) {
    Say "  GitHub 速度低于 500 KB/s 或连不上，改从 Gitee 下载…"
    if (Fetch-One "$GiteeBase/$pkg" $zip) {
      Say "  已从 Gitee 下载完成"
    } else {
      $ok = $false
      foreach ($m in $Mirrors) {
        Say "  Gitee 也不行，试加速镜像：$m"
        if (Fetch-One ($m + "$Base/$pkg") $zip) { $ok = $true; Say "  已从加速镜像下载完成"; break }
      }
      if (-not $ok) {
        Die "下载失败（GitHub / Gitee / 镜像都试过了）。可以手动下载后放到本文件旁边：`n    $Base/$pkg"
      }
    }
  }
}

# ---------- 4. 安装 ----------
if ($edition -eq "desktop") {
  # 桌面版交给官方 Setup.exe：它负责开始菜单、注册表卸载项、安装包校验
  $setup = Join-Path $Here "deepsleep-Setup.exe"
  if (-not (Test-Path $setup)) {
    Say "  本地没有 Setup.exe，改为直接解压到 $Dir（绿色版）"
    if (Test-Path $Dir) { Remove-Item $Dir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    Expand-Zip $zip $Dir
    Say ""
    Say "  OK 桌面版已解压到 $Dir"
    Say "     启动：$Dir\deepsleep.exe"
  } else {
    Say "  启动官方安装程序：$setup"
    Start-Process -FilePath $setup -Wait
    Say ""
    Say "  OK 桌面版安装程序已结束（是否装好请看它自己的提示）"
  }
} else {
  if (Test-Path $Dir) {
    Say "  已存在 $Dir，先清空（内核版是绿色目录，删了就是卸载）"
    Remove-Item $Dir -Recurse -Force
  }
  New-Item -ItemType Directory -Force -Path $Dir | Out-Null
  Expand-Zip $zip $Dir

  $exe = Join-Path $Dir "deepsleep-core.exe"
  if (-not (Test-Path $exe)) { Die "包里没找到 deepsleep-core.exe，安装包可能不对" }

  # 开始菜单快捷方式：内核版是控制台程序，快捷方式指向 cmd 才不会一闪就没
  try {
    $lnk = Join-Path ([Environment]::GetFolderPath("Programs")) "deepsleep 内核版.lnk"
    $sh = New-Object -ComObject WScript.Shell
    $s = $sh.CreateShortcut($lnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $Dir
    $s.IconLocation = $exe
    $s.Save()
    Say "  已添加开始菜单项「deepsleep 内核版」"
  } catch { Say "  （开始菜单项创建失败，直接运行 exe 也可以）" }

  Say ""
  Say "  OK 内核版已装好"
  Say "     启动：$exe"
  Say ""
  Say "  第一次启动会打印本机地址和配对令牌，浏览器打开就能用。"
  Say "  如果它自动换了端口（提示「端口被系统保留」），按它打印的地址打开即可。"
}

if (Test-Path $tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
Say ""

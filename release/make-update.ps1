<#
  OTA 更新清单生成脚本
  用法示例：
    # 1) 把新版本安装包放在 release 目录，然后生成清单（本地/共享盘分发）
    powershell -ExecutionPolicy Bypass -File make-update.ps1 -Version 1.0.1 -Notes "修复xxx；新增yyy"

    # 2) 已经上传到网站，用下载地址分发
    powershell -ExecutionPolicy Bypass -File make-update.ps1 -Version 1.0.1 -Notes "..." `
        -BaseUrl "https://example.com/deepsleep/"

  生成的 update.json 放到发布目录，客户端在 ⚙ 设置里把「更新源」指向它即可。

  GitHub 发布（推荐，客户端不再需要 update.json）：
    1) 在 GitHub 建一个仓库，把 deepsleep-Setup.exe 传到 Releases，tag 形如 v1.0.1
    2) 客户端 ⚙ 设置 → 更新源 填 owner/repo（或 https://github.com/owner/repo）
       客户端走 GitHub Releases API 读最新版 tag + 安装包资产，自动升级
    3) 想用 update.json 兜底：把本脚本生成的 update.json 提交到仓库根目录（main 或 master 分支）
#>
param(
    [string]$Version = "1.0.0",
    [string]$Notes = "",
    [string]$Installer = "",
    [string]$BaseUrl = "",
    [string]$OutFile = ""
)

$ErrorActionPreference = 'Stop'
$releaseDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($Installer)) { $Installer = Join-Path $releaseDir 'deepsleep-Setup.exe' }
if (-not (Test-Path -LiteralPath $Installer)) { throw "找不到安装包：$Installer" }
if ([string]::IsNullOrWhiteSpace($OutFile)) { $OutFile = Join-Path $releaseDir 'update.json' }

$hash = (Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash
$fileName = Split-Path -Leaf $Installer
if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    $url = (Resolve-Path -LiteralPath $Installer).Path       # 本地/共享盘路径
} else {
    if (-not $BaseUrl.EndsWith('/')) { $BaseUrl += '/' }
    $url = $BaseUrl + $fileName
}

$manifest = [ordered]@{
    version = $Version
    url     = $url
    sha256  = $hash
    notes   = $Notes
}
$json = $manifest | ConvertTo-Json -Depth 3
[System.IO.File]::WriteAllText($OutFile, $json, (New-Object System.Text.UTF8Encoding $false))

Write-Host "已生成更新清单：$OutFile"
Write-Host "  版本：$Version"
Write-Host "  地址：$url"
Write-Host "  校验：$hash"

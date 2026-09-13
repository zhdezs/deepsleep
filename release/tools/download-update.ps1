<#
  deepsleep · 断点续传下载安装包（GitHub 慢/断流的正解）
  ============================================================
  直连 GitHub 慢的本质是「一次连接要撑完 150+ MB」。
  本脚本用 curl 的 -C - 做断点续传：断了就重连接着下，
  并在多个镜像之间自动切换，直到下完为止。

  用法：
    powershell -ExecutionPolicy Bypass -File download-update.ps1
    powershell -ExecutionPolicy Bypass -File download-update.ps1 -Tag v1.0.7 -Out "D:\deepsleep-Setup.exe"
#>
param(
    [string]$Repo = 'zhdezs/deepsleep',
    [string]$Tag  = 'v1.0.7',
    [string]$File = 'deepsleep-Setup.exe',
    [string]$Out  = "$env:USERPROFILE\Downloads\deepsleep-Setup.exe"
)
$ErrorActionPreference = 'Continue'
$base = "https://github.com/$Repo/releases/download/$Tag/$File"
$cands = @(
    $base,
    "https://ghfast.top/$base",
    "https://ghproxy.net/$base",
    "https://gh-proxy.com/$base",
    "https://hub.gitmirror.com/$base"
)

Write-Host "目标：$Out" -ForegroundColor Cyan
$round = 0
while ($true) {
    $round++
    foreach ($u in $cands) {
        $have = if (Test-Path $Out) { (Get-Item $Out).Length } else { 0 }
        Write-Host ("`n[第 {0} 轮] {1}  （已下 {2:N1} MB，继续续传）" -f $round, ($u -split '/')[2], ($have / 1MB)) -ForegroundColor DarkGray
        & curl.exe -L -C - --retry 5 --retry-delay 2 --retry-all-errors --connect-timeout 20 -o $Out $u
        if ($LASTEXITCODE -eq 0) {
            $info = Get-Item $Out
            Write-Host ("下载完成：{0:N1} MB" -f ($info.Length / 1MB)) -ForegroundColor Green
            Write-Host ("SHA256：{0}" -f (Get-FileHash $Out -Algorithm SHA256).Hash)
            Write-Host "`n直接双击这个文件即可安装（配置/对话/记忆都不会动）。" -ForegroundColor Cyan
            exit 0
        }
    }
    if ($round -ge 30) { Write-Host "`n试了 30 轮仍未完成，请检查网络或改用其它镜像。" -ForegroundColor Yellow; exit 1 }
    Write-Host "本轮没成功，3 秒后接着续传…" -ForegroundColor Yellow
    Start-Sleep -Seconds 3
}
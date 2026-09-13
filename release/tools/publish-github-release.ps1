<#
  deepsleep · 一键发布新版本到 GitHub（OTA 用）
  ==================================================================
  做四件事：
    1. 拿令牌（优先环境变量 DEEPSLEEP_GITHUB_TOKEN，其次 DPAPI 加密文件
       data\github.token，都没有才弹窗让你粘贴，并可顺手加密保存）
    2. 建仓库（不存在才建）、建 Release（tag 形如 v1.0.2）、上传安装包资产
    3. 把客户端的「更新源」自动写成 owner/repo（config.json）
    4. 打印结果地址和验证方法
  令牌全程不回显、不落明文、不进日志。

  用法：
    双击运行（推荐，全图形引导）
    powershell -ExecutionPolicy Bypass -File publish-github-release.ps1 -Repo "用户名/deepsleep"
    powershell -ExecutionPolicy Bypass -File publish-github-release.ps1 -Installer "D:\release\deepsleep-Setup.exe" -Draft
#>
param(
    [string]$Repo = '',
    [string]$Installer = '',
    [string]$Target = '',
    [string]$Version = '',
    [string]$Tag = '',
    [string]$Notes = '',
    [string[]]$Asset = @(),
    [switch]$Draft,
    [switch]$Prerelease,
    [switch]$NoConfigPatch
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Net.Http

$TokenFileName = 'github.token'
$ApiBase = 'https://api.github.com'
$UploadBase = 'https://uploads.github.com'

function Say($t, $c = 'Gray') { Write-Host $t -ForegroundColor $c }
function Head($t) { Write-Host ''; Write-Host "==> $t" -ForegroundColor Cyan }

function Get-TargetDir {
    param([string]$Explicit)
    if ($Explicit -and (Test-Path -LiteralPath $Explicit)) { return (Resolve-Path -LiteralPath $Explicit).Path }
    $here = Split-Path -Parent $MyInvocation.MyCommand.Definition
    if (Test-Path (Join-Path $here 'deepsleep.exe')) { return $here }
    $def = Join-Path $env:LOCALAPPDATA 'Programs\deepsleep'
    if (Test-Path (Join-Path $def 'deepsleep.exe')) { return $def }
    return ''
}

function Unprotect-Token([string]$b64) {
    $blob = [Convert]::FromBase64String($b64)
    $bytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
        $blob, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    return [System.Text.Encoding]::UTF8.GetString($bytes)
}

function Protect-Token([string]$plain) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($plain)
    $blob = [System.Security.Cryptography.ProtectedData]::Protect(
        $bytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    return [Convert]::ToBase64String($blob)
}

function Lock-DownFile([string]$path) {
    try {
        $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        $acl = New-Object System.Security.AccessControl.FileSecurity
        $acl.SetAccessRuleProtection($true, $false)
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($me, 'FullControl', 'Allow')))
        Set-Acl -LiteralPath $path -AclObject $acl
    } catch { }
}

function Read-Config([string]$dir) {
    $p = Join-Path $dir 'data\config.json'
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    try { return @{ Path = $p; Json = (Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json) } }
    catch { return $null }
}

function Get-Token {
    param([string]$AppDir, [string]$RepoHint)
    $env_tok = [Environment]::GetEnvironmentVariable('DEEPSLEEP_GITHUB_TOKEN')
    if (-not [string]::IsNullOrWhiteSpace($env_tok)) { Say '使用环境变量里的令牌。' 'DarkGray'; return $env_tok.Trim() }

    if ($AppDir) {
        $p = Join-Path $AppDir "data\$TokenFileName"
        if (Test-Path -LiteralPath $p) {
            try {
                $tok = Unprotect-Token ((Get-Content -LiteralPath $p -Raw).Trim())
                if (-not [string]::IsNullOrWhiteSpace($tok)) { Say '使用已保存的 DPAPI 加密令牌。' 'DarkGray'; return $tok.Trim() }
            } catch { Say '已保存的令牌解不开（换过账号/机器？），请重新输入。' 'Yellow' }
        }
    }

    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'deepsleep · 发布到 GitHub'
    $form.ClientSize = New-Object System.Drawing.Size(560, 250)
    $form.StartPosition = 'CenterScreen'
    $form.TopMost = $true
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false; $form.MinimizeBox = $false
    $form.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text = "没有找到已保存的令牌，粘贴一个（勾选下面可加密保存，下次免输）：`n仓库：$RepoHint"
    $lbl.Location = New-Object System.Drawing.Point(16, 14)
    $lbl.Size = New-Object System.Drawing.Size(528, 44)

    $tb = New-Object System.Windows.Forms.TextBox
    $tb.Location = New-Object System.Drawing.Point(18, 66)
    $tb.Size = New-Object System.Drawing.Size(524, 26)
    $tb.UseSystemPasswordChar = $true
    $tb.Font = New-Object System.Drawing.Font('Consolas', 10)

    $chk = New-Object System.Windows.Forms.CheckBox
    $chk.Text = '用 Windows DPAPI 加密保存（只有本机当前账号能解开）'
    $chk.Location = New-Object System.Drawing.Point(18, 102)
    $chk.Size = New-Object System.Drawing.Size(524, 24)
    $chk.Checked = $true

    $ok = New-Object System.Windows.Forms.Button
    $ok.Text = '确定'; $ok.Location = New-Object System.Drawing.Point(360, 190)
    $ok.Size = New-Object System.Drawing.Size(88, 32)
    $ok.DialogResult = [System.Windows.Forms.DialogResult]::OK

    $no = New-Object System.Windows.Forms.Button
    $no.Text = '取消'; $no.Location = New-Object System.Drawing.Point(456, 190)
    $no.Size = New-Object System.Drawing.Size(88, 32)
    $no.DialogResult = [System.Windows.Forms.DialogResult]::Cancel

    $form.Controls.AddRange(@($lbl, $tb, $chk, $ok, $no))
    $form.AcceptButton = $ok; $form.CancelButton = $no
    $form.Add_Shown({ $tb.Focus() })
    if ($form.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return '' }
    $tok = $tb.Text.Trim()
    if ($chk.Checked -and $tok -and $AppDir) {
        try {
            $dataDir = Join-Path $AppDir 'data'
            New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
            $tp = Join-Path $dataDir $TokenFileName
            [System.IO.File]::WriteAllText($tp, (Protect-Token $tok), (New-Object System.Text.UTF8Encoding $false))
            Lock-DownFile $tp
            Say "令牌已加密保存：$tp" 'DarkGray'
        } catch { Say '令牌保存失败（不影响本次发布）。' 'Yellow' }
    }
    return $tok
}

function New-ApiClient([string]$token) {
    $c = New-Object System.Net.Http.HttpClient
    $c.Timeout = [TimeSpan]::FromMinutes(60)
    $c.DefaultRequestHeaders.UserAgent.ParseAdd('deepsleep-release')
    $c.DefaultRequestHeaders.Authorization =
        New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $token)
    $c.DefaultRequestHeaders.Accept.ParseAdd('application/vnd.github+json')
    return $c
}

function Api([System.Net.Http.HttpClient]$c, [string]$method, [string]$url, [string]$json = '') {
    $req = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::$method), $url
    if ($json) { $req.Content = New-Object System.Net.Http.StringContent($json, [System.Text.Encoding]::UTF8, 'application/json') }
    $resp = $c.SendAsync($req).GetAwaiter().GetResult()
    $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return @{ Code = [int]$resp.StatusCode; Body = $body; Resp = $resp }
}

# ------------------------------------------------------------------ 主流程

$appDir = Get-TargetDir $Target

if (-not $Installer) {
    $cands = @()
    if ($appDir) { $cands += (Join-Path (Split-Path -Parent $appDir) 'deepsleep-Setup.exe') }
    $cands += 'C:\Users\lichenghan\CodeBuddy\20260812123427\release\deepsleep-Setup.exe'
    foreach ($p in $cands) { if ($p -and (Test-Path -LiteralPath $p)) { $Installer = $p; break } }
}
if (-not $Installer -or -not (Test-Path -LiteralPath $Installer)) {
    Head '选择要发布的安装包'
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Title = '选择 deepsleep-Setup.exe'
    $dlg.Filter = 'deepsleep 安装包 (deepsleep-Setup.exe)|deepsleep-Setup.exe|可执行文件 (*.exe)|*.exe'
    if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { Say '已取消。' 'Yellow'; exit 1 }
    $Installer = $dlg.FileName
}
$Installer = (Resolve-Path -LiteralPath $Installer).Path

if (-not $Version) {
    $fv = (Get-Item -LiteralPath $Installer).VersionInfo.FileVersion
    if ($fv) { $parts = $fv.Split('.'); $Version = ($parts[0..([Math]::Min(2, $parts.Length - 1))] -join '.') }
    if (-not $Version) { $Version = '1.0.0' }
}
if (-not $Tag) { $Tag = "v$Version" }

if (-not $Repo -and $appDir) {
    $cfg = Read-Config $appDir
    if ($cfg -and $cfg.Json.UpdateUrl -match '^(?:https?://github\.com/)?([\w.\-]+)/([\w.\-]+?)(?:\.git)?/?$') {
        $Repo = "$($Matches[1])/$($Matches[2])"
    }
}
if (-not $Repo) {
    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'deepsleep · 发布到 GitHub'
    $form.ClientSize = New-Object System.Drawing.Size(520, 150)
    $form.StartPosition = 'CenterScreen'; $form.TopMost = $true
    $form.FormBorderStyle = 'FixedDialog'; $form.MaximizeBox = $false; $form.MinimizeBox = $false
    $form.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)
    $l1 = New-Object System.Windows.Forms.Label
    $l1.Text = '要发布到哪个仓库？（填 用户名/仓库名，例如 lichenghan/deepsleep，不存在会自动创建）'
    $l1.Location = New-Object System.Drawing.Point(16, 18); $l1.Size = New-Object System.Drawing.Size(488, 40)
    $t1 = New-Object System.Windows.Forms.TextBox
    $t1.Location = New-Object System.Drawing.Point(18, 62); $t1.Size = New-Object System.Drawing.Size(484, 26)
    $b1 = New-Object System.Windows.Forms.Button
    $b1.Text = '确定'; $b1.Location = New-Object System.Drawing.Point(410, 102)
    $b1.Size = New-Object System.Drawing.Size(92, 32); $b1.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $form.Controls.AddRange(@($l1, $t1, $b1)); $form.AcceptButton = $b1
    $form.Add_Shown({ $t1.Focus() })
    if ($form.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { Say '已取消。' 'Yellow'; exit 1 }
    $Repo = $t1.Text.Trim()
}
if ($Repo -match '^(?:https?://github\.com/)?([\w.\-]+)/([\w.\-]+?)(?:\.git)?/?$') { $Repo = "$($Matches[1])/$($Matches[2])" }

Head "发布 $Repo  $Tag"
Say "安装包：$Installer"
Say "版本号：$Version"

$token = Get-Token $appDir $Repo
if ([string]::IsNullOrWhiteSpace($token)) { Say '没有令牌，退出。' 'Yellow'; exit 1 }

$client = New-ApiClient $token

Head '校验令牌'
$me = Api $client 'Get' "$ApiBase/user"
if ($me.Code -ne 200) { Say "令牌无效（HTTP $($me.Code)）：$($me.Body)" 'Red'; exit 1 }
$login = ($me.Body | ConvertFrom-Json).login
$rate = (Api $client 'Get' "$ApiBase/rate_limit").Body | ConvertFrom-Json
Say "已登录：$login   API 限额：$($rate.resources.core.limit)/小时" 'Green'

Head '检查仓库'
$r = Api $client 'Get' "$ApiBase/repos/$Repo"
if ($r.Code -eq 404) {
    $ans = [System.Windows.Forms.MessageBox]::Show("仓库 $Repo 不存在，现在创建吗？（公开仓库，客户端才能下载）", 'deepsleep', 'YesNo', 'Question')
    if ($ans -ne [System.Windows.Forms.DialogResult]::Yes) { exit 1 }
    $name = $Repo.Split('/')[1]
    $cr = Api $client 'Post' "$ApiBase/user/repos" (@{ name = $name; private = $false; auto_init = $true; description = 'deepsleep AI 助手 · OTA 更新仓库' } | ConvertTo-Json)
    if ($cr.Code -notin @(201, 200)) { Say "创建失败（HTTP $($cr.Code)）：$($cr.Body)" 'Red'; exit 1 }
    Say "仓库已创建：$Repo" 'Green'
}
elseif ($r.Code -ne 200) { Say "读取仓库失败（HTTP $($r.Code)）：$($r.Body)" 'Red'; exit 1 }
else { Say "仓库存在：$Repo" 'DarkGray' }

Head "创建 Release $Tag"
$ex = Api $client 'Get' "$ApiBase/repos/$Repo/releases/tags/$Tag"
if ($ex.Code -eq 200) {
    $ans = [System.Windows.Forms.MessageBox]::Show("Release $Tag 已存在。删除后重建吗？（旧资产会被删掉）", 'deepsleep', 'YesNo', 'Warning')
    if ($ans -ne [System.Windows.Forms.DialogResult]::Yes) { Say '已取消。' 'Yellow'; exit 1 }
    $old = ($ex.Body | ConvertFrom-Json)
    $del = Api $client 'Delete' "$ApiBase/repos/$Repo/releases/$($old.id)"
    if ($del.Code -notin @(204, 200)) { Say "删除旧 Release 失败（HTTP $($del.Code)）" 'Red'; exit 1 }
}

if (-not $Notes) {
    $Notes = "deepsleep $Version" + [Environment]::NewLine + [Environment]::NewLine +
             "· 工具调用流程：用户输入 → 分析意图 → tool_call → 执行 → 结果回传 → 生成最终回复" + [Environment]::NewLine +
             "· 支持 OpenAI Responses API（/v1/responses）与地址自动补全" + [Environment]::NewLine +
             "· GitHub 令牌（DPAPI 加密）用于私有仓库与更高限额" + [Environment]::NewLine +
             "· 界面与稳定性修复"
}
$payload = @{
    tag_name         = $Tag
    name             = "deepsleep $Version"
    body             = $Notes
    draft            = [bool]$Draft
    prerelease       = [bool]$Prerelease
    target_commitish = 'main'
} | ConvertTo-Json
$created = Api $client 'Post' "$ApiBase/repos/$Repo/releases" $payload
if ($created.Code -notin @(201, 200)) {
    # 空仓库默认分支可能是 master
    $payload = $payload -replace '"main"', '"master"'
    $created = Api $client 'Post' "$ApiBase/repos/$Repo/releases" $payload
}
if ($created.Code -notin @(201, 200)) { Say "创建 Release 失败（HTTP $($created.Code)）：$($created.Body)" 'Red'; exit 1 }
$rel = $created.Body | ConvertFrom-Json
Say "Release 已创建：$($rel.html_url)" 'Green'

Head '上传资产'
$files = @($Installer) + $Asset
foreach ($f in $files) {
    if (-not (Test-Path -LiteralPath $f)) { Say "跳过（不存在）：$f" 'Yellow'; continue }
    $f = (Resolve-Path -LiteralPath $f).Path
    $name = Split-Path -Leaf $f
    $sizeMb = [math]::Round((Get-Item -LiteralPath $f).Length / 1MB, 1)
    Say "上传 $name ($sizeMb MB)…" 'DarkGray'
    $url = "$UploadBase/repos/$Repo/releases/$($rel.id)/assets?name=$([uri]::EscapeDataString($name))"
    $stream = [System.IO.File]::OpenRead($f)
    $content = New-Object System.Net.Http.StreamContent($stream)
    $content.Headers.ContentType = New-Object System.Net.Http.Headers.MediaTypeHeaderValue('application/octet-stream')
    $req = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::Post), $url
    $req.Content = $content
    $resp = $client.SendAsync($req).GetAwaiter().GetResult()
    $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $stream.Dispose(); $content.Dispose(); $req.Dispose()
    if ([int]$resp.StatusCode -notin @(201, 200)) { Say "上传失败（HTTP $([int]$resp.StatusCode)）：$body" 'Red'; exit 1 }
    Say "  ✓ $name" 'Green'
}

if (-not $NoConfigPatch) {
    Head '把「更新源」写进客户端配置'
    $patched = 0
    foreach ($d in @($appDir, 'C:\Users\lichenghan\CodeBuddy\20260812123427\src\dist\deepsleep-win-x64')) {
        if (-not $d -or -not (Test-Path (Join-Path $d 'deepsleep.exe'))) { continue }
        $cfg = Read-Config $d
        if (-not $cfg) { continue }
        try {
            $cfg.Json.UpdateUrl = $Repo
            $cfg.Json.AutoCheckUpdate = $true
            ($cfg.Json | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath $cfg.Path -Encoding UTF8
            Say "  ✓ $($cfg.Path)" 'Green'
            $patched++
        } catch { Say "  跳过 $($cfg.Path)：$($_.Exception.Message)" 'Yellow' }
    }
    if ($patched -eq 0) { Say '没有可写的 config.json（可以稍后在 ⚙ 设置里手填更新源）。' 'DarkGray' }
}

Head '完成'
Say "Release 页面：$($rel.html_url)" 'Green'
Write-Host '客户端：⚙ 设置 → 更新源 填 ' -NoNewline
Write-Host $Repo -ForegroundColor White -NoNewline
Write-Host ' （已自动写入），左下角「检查更新」即可升级。'

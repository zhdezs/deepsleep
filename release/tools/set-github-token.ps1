<#
  deepsleep · GitHub 令牌录入（DPAPI 加密，绝对安全）
  ==================================================================
  弹窗里粘贴令牌 → 用 Windows DPAPI（当前用户）加密 → 写到
  <安装目录>\data\github.token。令牌不会出现在 config.json、日志、
  命令行、窗口标题或任何明文文件里；只有「本机 + 本 Windows 账号」
  能解开（换电脑或换用户都解不开）。

  用法（双击最省事）：
    powershell -ExecutionPolicy Bypass -File set-github-token.ps1
    powershell -ExecutionPolicy Bypass -File set-github-token.ps1 -Target "D:\Apps\deepsleep"
    powershell -ExecutionPolicy Bypass -File set-github-token.ps1 -VerifyOnly
    powershell -ExecutionPolicy Bypass -File set-github-token.ps1 -Remove

  令牌权限建议（都选只读）：
    · 只用来「检查更新」公开仓库 → 勾 public_repo 即可（不带权限的令牌也能提高限额）
    · 私有仓库 OTA → classic 勾 repo（只读），fine-grained 给 Contents: Read
#>
param(
    [string[]]$Target = @(),
    [switch]$Remove,
    [switch]$VerifyOnly,
    [switch]$NoVerify
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$TokenFileName = 'github.token'

function Resolve-Targets {
    $list = New-Object System.Collections.Generic.List[string]
    foreach ($t in $Target) {
        if ([string]::IsNullOrWhiteSpace($t)) { continue }
        if (Test-Path -LiteralPath $t) { $list.Add((Resolve-Path -LiteralPath $t).Path) }
        else { Write-Host "跳过（不存在）：$t" -ForegroundColor Yellow }
    }
    if ($list.Count -eq 0) {
        $here = Split-Path -Parent $MyInvocation.MyCommand.Definition
        if (Test-Path (Join-Path $here 'deepsleep.exe')) { $list.Add($here) }
        $def = Join-Path $env:LOCALAPPDATA 'Programs\deepsleep'
        if ((Test-Path (Join-Path $def 'deepsleep.exe')) -and -not $list.Contains($def)) { $list.Add($def) }
    }
    return $list
}

function Protect-Token([string]$plain) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($plain)
    $blob = [System.Security.Cryptography.ProtectedData]::Protect(
        $bytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    return [Convert]::ToBase64String($blob)
}

function Unprotect-Token([string]$b64) {
    $blob = [Convert]::FromBase64String($b64)
    $bytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
        $blob, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    return [System.Text.Encoding]::UTF8.GetString($bytes)
}

function Lock-DownFile([string]$path) {
    try {
        $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        $acl = New-Object System.Security.AccessControl.FileSecurity
        $acl.SetAccessRuleProtection($true, $false)
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $me, 'FullControl', 'Allow')
        $acl.AddAccessRule($rule)
        Set-Acl -LiteralPath $path -AclObject $acl
        return $true
    } catch { return $false }
}

function Test-GitHubToken([string]$tok) {
    $h = @{
        Authorization = "Bearer $tok"
        'User-Agent'  = 'deepsleep-token-setup'
        Accept        = 'application/vnd.github+json'
    }
    try {
        $user = Invoke-RestMethod -Uri 'https://api.github.com/user' -Headers $h -TimeoutSec 20
        $rate = Invoke-RestMethod -Uri 'https://api.github.com/rate_limit' -Headers $h -TimeoutSec 20
        return @{ Ok = $true; Login = $user.login; Limit = $rate.resources.core.limit }
    } catch {
        return @{ Ok = $false; Error = $_.Exception.Message }
    }
}

function Show-TokenDialog {
    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'deepsleep · 录入 GitHub 令牌'
    $form.ClientSize = New-Object System.Drawing.Size(560, 250)
    $form.StartPosition = 'CenterScreen'
    $form.TopMost = $true
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)

    $tip = New-Object System.Windows.Forms.Label
    $tip.Text = "粘贴 GitHub 令牌（classic 或 fine-grained 都行），点保存。" + [Environment]::NewLine +
                "令牌会用 Windows DPAPI 加密后写到 data\github.token：" + [Environment]::NewLine +
                "· 不写进 config.json，不写日志，不回显" + [Environment]::NewLine +
                "· 只有本机 + 当前 Windows 账号能解密" + [Environment]::NewLine +
                "· 文件权限会收紧到只允许当前账号访问"
    $tip.Location = New-Object System.Drawing.Point(16, 14)
    $tip.Size = New-Object System.Drawing.Size(528, 110)

    $box = New-Object System.Windows.Forms.TextBox
    $box.Location = New-Object System.Drawing.Point(18, 132)
    $box.Size = New-Object System.Drawing.Size(524, 26)
    $box.UseSystemPasswordChar = $true
    $box.Font = New-Object System.Drawing.Font('Consolas', 10)

    $hint = New-Object System.Windows.Forms.Label
    $hint.Text = '看不到输入内容是对的（密码模式）。可以 Ctrl+V 粘贴。'
    $hint.ForeColor = [System.Drawing.Color]::Gray
    $hint.Location = New-Object System.Drawing.Point(18, 162)
    $hint.Size = New-Object System.Drawing.Size(524, 20)

    $ok = New-Object System.Windows.Forms.Button
    $ok.Text = '保存'
    $ok.Location = New-Object System.Drawing.Point(360, 196)
    $ok.Size = New-Object System.Drawing.Size(88, 32)
    $ok.DialogResult = [System.Windows.Forms.DialogResult]::OK

    $cancel = New-Object System.Windows.Forms.Button
    $cancel.Text = '取消'
    $cancel.Location = New-Object System.Drawing.Point(456, 196)
    $cancel.Size = New-Object System.Drawing.Size(88, 32)
    $cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel

    $form.Controls.AddRange(@($tip, $box, $hint, $ok, $cancel))
    $form.AcceptButton = $ok
    $form.CancelButton = $cancel
    $form.Add_Shown({ $box.Focus() })

    if ($form.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        return $box.Text.Trim()
    }
    return $null
}

# ------------------------------------------------------------------ 主流程

$targets = Resolve-Targets
if ($targets.Count -eq 0) {
    [System.Windows.Forms.MessageBox]::Show(
        "没找到 deepsleep 安装目录。" + [Environment]::NewLine +
        "请用 -Target 指定，例如：" + [Environment]::NewLine +
        "powershell -ExecutionPolicy Bypass -File set-github-token.ps1 -Target ""D:\Apps\deepsleep""",
        'deepsleep', 'OK', 'Warning') | Out-Null
    exit 1
}

if ($Remove) {
    foreach ($t in $targets) {
        $p = Join-Path $t "data\$TokenFileName"
        if (Test-Path -LiteralPath $p) {
            Remove-Item -LiteralPath $p -Force
            Write-Host "已删除令牌：$p" -ForegroundColor Green
        } else {
            Write-Host "本来就没有：$p" -ForegroundColor Gray
        }
    }
    exit 0
}

if ($VerifyOnly) {
    foreach ($t in $targets) {
        $p = Join-Path $t "data\$TokenFileName"
        if (-not (Test-Path -LiteralPath $p)) { Write-Host "$t ：未配置令牌" -ForegroundColor Gray; continue }
        try {
            $tok = Unprotect-Token (Get-Content -LiteralPath $p -Raw).Trim()
            $r = Test-GitHubToken $tok
            if ($r.Ok) {
                Write-Host "$t ：令牌有效，账号 $($r.Login)，限额 $($r.Limit)/小时" -ForegroundColor Green
            } else {
                Write-Host "$t ：校验失败 - $($r.Error)" -ForegroundColor Yellow
            }
        } catch {
            Write-Host "$t ：解密失败（换过账号或机器？）" -ForegroundColor Yellow
        }
    }
    exit 0
}

$token = Show-TokenDialog
if ([string]::IsNullOrWhiteSpace($token)) { exit 0 }

if (-not $NoVerify) {
    $r = Test-GitHubToken $token
    if ($r.Ok) {
        [System.Windows.Forms.MessageBox]::Show(
            "令牌有效。" + [Environment]::NewLine +
            "账号：$($r.Login)" + [Environment]::NewLine +
            "API 限额：$($r.Limit)/小时",
            'deepsleep', 'OK', 'Information') | Out-Null
    } else {
        $ans = [System.Windows.Forms.MessageBox]::Show(
            "令牌校验失败（可能是网络不通或令牌不对）：" + [Environment]::NewLine +
            "$($r.Error)" + [Environment]::NewLine + [Environment]::NewLine +
            "仍然保存吗？",
            'deepsleep', 'YesNo', 'Warning')
        if ($ans -ne [System.Windows.Forms.DialogResult]::Yes) { exit 0 }
    }
}

try {
    $b64 = Protect-Token $token
} catch {
    # DPAPI 失败（极少见：用户配置未加载 / 被模拟运行）→ 宁可不保存，也绝不落明文
    [System.Windows.Forms.MessageBox]::Show(
        "DPAPI 加密失败，已放弃保存（绝不明文落盘）：" + [Environment]::NewLine +
        "$($_.Exception.Message)" + [Environment]::NewLine + [Environment]::NewLine +
        "请在正常登录的桌面会话里再运行一次本脚本。",
        'deepsleep', 'OK', 'Error') | Out-Null
    exit 1
}
$token = $null
[System.GC]::Collect()

foreach ($t in $targets) {
    $dataDir = Join-Path $t 'data'
    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
    $p = Join-Path $dataDir $TokenFileName
    [System.IO.File]::WriteAllText($p, $b64, (New-Object System.Text.UTF8Encoding $false))
    $locked = Lock-DownFile $p
    Write-Host "已保存（DPAPI 加密）：$p" -ForegroundColor Green
    if ($locked) { Write-Host "  权限已收紧到当前账号" -ForegroundColor DarkGray }
}

[System.Windows.Forms.MessageBox]::Show(
    "令牌已加密保存，共写入 $($targets.Count) 个位置。" + [Environment]::NewLine +
    "重启 deepsleep 后，「检查更新」就会用这个令牌（私有仓库可用、限额更高）。",
    'deepsleep', 'OK', 'Information') | Out-Null

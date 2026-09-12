<#
  deepsleep 图形界面安装程序（手写实现，无第三方依赖）
  用法：双击「安装 deepsleep.cmd」，或命令行：
        powershell -ExecutionPolicy Bypass -File install.ps1
        powershell -ExecutionPolicy Bypass -File install.ps1 -Silent -InstallDir "D:\Apps\deepsleep" -NoDesktopShortcut -NoLaunch
#>
param(
    [string]$InstallDir = "",
    [switch]$Silent,
    [switch]$NoDesktopShortcut,
    [switch]$NoLaunch,
    [switch]$NoRegistry
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$AppName      = 'deepsleep'
$AppDisplay   = 'deepsleep AI 助手'
$AppVersion   = '1.0.0'
$Publisher    = 'deepsleep'
$ExeName      = 'deepsleep.exe'
$ScriptDir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$PackageDir   = Join-Path $ScriptDir 'package'
$DefaultDir   = Join-Path $env:LOCALAPPDATA ('Programs\' + $AppName)
if ([string]::IsNullOrWhiteSpace($InstallDir)) { $InstallDir = $DefaultDir }
$InstallDir   = [System.IO.Path]::GetFullPath($InstallDir)
$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $AppName

if (-not (Test-Path (Join-Path $PackageDir $ExeName))) {
    [System.Windows.Forms.MessageBox]::Show("找不到程序文件：$PackageDir\$ExeName`n请把安装器和 package 文件夹放在一起。",
        '安装失败', 'OK', 'Error') | Out-Null
    exit 1
}

# ---------------------------------------------------------------- 安装逻辑

function Get-InstallFiles {
    $package = $PackageDir
    $files = Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object {
        $rel = $_.FullName.Substring($package.Length).TrimStart('\')
        -not $rel.StartsWith('data\', 'OrdinalIgnoreCase') -and $rel -ne 'data'
    }
    return $files
}

function Install-App {
    param($Dest, [scriptblock]$OnProgress)

    $files = Get-InstallFiles
    $total = [Math]::Max(1, $files.Count)
    New-Item -ItemType Directory -Path $Dest -Force | Out-Null

    $i = 0
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($PackageDir.Length).TrimStart('\')
        $target = Join-Path $Dest $rel
        $dir = Split-Path -Parent $target
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Copy-Item -LiteralPath $f.FullName -Destination $target -Force
        $i++
        if ($OnProgress) { & $OnProgress ($i / $total * 70) $rel }
    }

    # 用户数据：首次安装复制技能与配置模板；升级安装保留用户已有的 Key / 对话 / 记忆 / 模型
    $srcData = Join-Path $PackageDir 'data'
    $dstData = Join-Path $Dest 'data'
    if (-not (Test-Path $dstData)) { New-Item -ItemType Directory -Path $dstData -Force | Out-Null }

    $srcSkills = Join-Path $srcData 'skills'
    if (Test-Path $srcSkills) {
        $dstSkills = Join-Path $dstData 'skills'
        if (-not (Test-Path $dstSkills)) { New-Item -ItemType Directory -Path $dstSkills -Force | Out-Null }
        foreach ($d in Get-ChildItem -LiteralPath $srcSkills -Directory) {
            $target = Join-Path $dstSkills $d.Name
            if (-not (Test-Path $target)) {
                Copy-Item -LiteralPath $d.FullName -Destination $target -Recurse -Force
            }
        }
    }

    $srcCfg = Join-Path $srcData 'config.json'
    $dstCfg = Join-Path $dstData 'config.json'
    if (-not (Test-Path $dstCfg) -and (Test-Path $srcCfg)) {
        Copy-Item -LiteralPath $srcCfg -Destination $dstCfg -Force
    }
    if ($OnProgress) { & $OnProgress 85 '写入用户数据目录' }

    # 卸载脚本随程序一起安装
    foreach ($n in @('uninstall.ps1', 'uninstall.cmd', '卸载 deepsleep.cmd')) {
        $src = Join-Path $ScriptDir $n
        if (Test-Path $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $Dest $n) -Force }
    }
    if ($OnProgress) { & $OnProgress 90 '创建快捷方式' }
}

function New-Shortcut {
    param([string]$LinkPath, [string]$Target, [string]$WorkDir)
    $ws = New-Object -ComObject WScript.Shell
    $sc = $ws.CreateShortcut($LinkPath)
    $sc.TargetPath = $Target
    $sc.WorkingDirectory = $WorkDir
    $sc.IconLocation = $Target
    $sc.Description = $AppDisplay
    $sc.Save()
}

function Install-Shortcuts {
    param($Dest, [bool]$Desktop)
    $exe = Join-Path $Dest $ExeName
    $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) ($AppDisplay + '.lnk')
    New-Shortcut -LinkPath $startMenu -Target $exe -WorkDir $Dest
    if ($Desktop) {
        $desktopLnk = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) ($AppDisplay + '.lnk')
        New-Shortcut -LinkPath $desktopLnk -Target $exe -WorkDir $Dest
    }
}

function Register-Uninstall {
    param($Dest)
    New-Item -Path $UninstallKey -Force | Out-Null
    $size = 0
    try { $size = [int](((Get-ChildItem -LiteralPath $Dest -Recurse -File | Measure-Object Length -Sum).Sum) / 1KB) } catch { }
    $uninst = 'powershell.exe -ExecutionPolicy Bypass -NoProfile -File "' + (Join-Path $Dest 'uninstall.ps1') + '"'
    Set-ItemProperty -Path $UninstallKey -Name DisplayName     -Value $AppDisplay
    Set-ItemProperty -Path $UninstallKey -Name DisplayVersion  -Value $AppVersion
    Set-ItemProperty -Path $UninstallKey -Name Publisher       -Value $Publisher
    Set-ItemProperty -Path $UninstallKey -Name InstallLocation -Value $Dest
    Set-ItemProperty -Path $UninstallKey -Name DisplayIcon     -Value (Join-Path $Dest $ExeName)
    Set-ItemProperty -Path $UninstallKey -Name UninstallString -Value $uninst
    Set-ItemProperty -Path $UninstallKey -Name QuietUninstallString -Value ($uninst + ' -Silent')
    Set-ItemProperty -Path $UninstallKey -Name EstimatedSize   -Value $size
    Set-ItemProperty -Path $UninstallKey -Name NoModify        -Value 1 -Type DWord
    Set-ItemProperty -Path $UninstallKey -Name NoRepair        -Value 1 -Type DWord
}

# ---------------------------------------------------------------- 静默安装

if ($Silent) {
    Write-Host "正在安装到 $InstallDir ..."
    Install-App -Dest $InstallDir -OnProgress { param($p, $n) }
    if (-not $NoRegistry) { Register-Uninstall -Dest $InstallDir }
    Install-Shortcuts -Dest $InstallDir -Desktop (-not $NoDesktopShortcut)
    Write-Host '安装完成。'
    if (-not $NoLaunch) { Start-Process (Join-Path $InstallDir $ExeName) }
    exit 0
}

# ---------------------------------------------------------------- 图形界面

[System.Windows.Forms.Application]::EnableVisualStyles()
[System.Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)

$font     = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)
$fontBold = New-Object System.Drawing.Font('Microsoft YaHei UI', 14, [System.Drawing.FontStyle]::Bold)

$form = New-Object System.Windows.Forms.Form
$form.Text            = "$AppDisplay 安装程序 $AppVersion"
$form.Size            = New-Object System.Drawing.Size(560, 430)
$form.StartPosition   = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox     = $false
$form.MinimizeBox     = $false
$form.Font            = $font
$form.BackColor       = [System.Drawing.Color]::FromArgb(246, 247, 249)

$title = New-Object System.Windows.Forms.Label
$title.Text     = $AppDisplay
$title.Font     = $fontBold
$title.Location = New-Object System.Drawing.Point(24, 20)
$title.AutoSize = $true
$form.Controls.Add($title)

$sub = New-Object System.Windows.Forms.Label
$sub.Text     = "版本 $AppVersion  ·  本地 AI 助手（工具调用 / 技能 / Agent 集群 / 长期记忆）"
$sub.Location = New-Object System.Drawing.Point(26, 52)
$sub.AutoSize = $true
$sub.ForeColor = [System.Drawing.Color]::FromArgb(110, 116, 124)
$form.Controls.Add($sub)

$lblDir = New-Object System.Windows.Forms.Label
$lblDir.Text     = '安装位置'
$lblDir.Location = New-Object System.Drawing.Point(26, 92)
$lblDir.AutoSize = $true
$form.Controls.Add($lblDir)

$txtDir = New-Object System.Windows.Forms.TextBox
$txtDir.Text     = $InstallDir
$txtDir.Location = New-Object System.Drawing.Point(28, 114)
$txtDir.Size     = New-Object System.Drawing.Size(400, 26)
$form.Controls.Add($txtDir)

$btnBrowse = New-Object System.Windows.Forms.Button
$btnBrowse.Text     = '浏览…'
$btnBrowse.Location = New-Object System.Drawing.Point(438, 113)
$btnBrowse.Size     = New-Object System.Drawing.Size(90, 28)
$btnBrowse.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = '选择安装位置'
    $dlg.SelectedPath = $txtDir.Text
    if ($dlg.ShowDialog() -eq 'OK') { $txtDir.Text = $dlg.SelectedPath }
})
$form.Controls.Add($btnBrowse)

$chkDesktop = New-Object System.Windows.Forms.CheckBox
$chkDesktop.Text     = '创建桌面快捷方式'
$chkDesktop.Checked  = (-not $NoDesktopShortcut)
$chkDesktop.Location = New-Object System.Drawing.Point(28, 152)
$chkDesktop.AutoSize = $true
$form.Controls.Add($chkDesktop)

$chkLaunch = New-Object System.Windows.Forms.CheckBox
$chkLaunch.Text     = '安装完成后立即启动'
$chkLaunch.Checked  = (-not $NoLaunch)
$chkLaunch.Location = New-Object System.Drawing.Point(220, 152)
$chkLaunch.AutoSize = $true
$form.Controls.Add($chkLaunch)

$tip = New-Object System.Windows.Forms.Label
$tip.Text = ("用户数据（API Key、对话历史、长期记忆、自训练模型）保存在安装目录的 data 文件夹内，升级安装不会覆盖。" +
             "`n每台电脑需要单独在 ⚙ 设置里填写自己的 API Key。")
$tip.Location = New-Object System.Drawing.Point(28, 180)
$tip.Size     = New-Object System.Drawing.Size(500, 46)
$tip.ForeColor = [System.Drawing.Color]::FromArgb(110, 116, 124)
$form.Controls.Add($tip)

$bar = New-Object System.Windows.Forms.ProgressBar
$bar.Location = New-Object System.Drawing.Point(28, 236)
$bar.Size     = New-Object System.Drawing.Size(500, 18)
$bar.Minimum  = 0
$bar.Maximum  = 100
$form.Controls.Add($bar)

$status = New-Object System.Windows.Forms.Label
$status.Text     = '准备就绪'
$status.Location = New-Object System.Drawing.Point(28, 262)
$status.Size     = New-Object System.Drawing.Size(500, 20)
$status.ForeColor = [System.Drawing.Color]::FromArgb(90, 96, 104)
$form.Controls.Add($status)

$btnInstall = New-Object System.Windows.Forms.Button
$btnInstall.Text     = '开始安装'
$btnInstall.Location = New-Object System.Drawing.Point(320, 320)
$btnInstall.Size     = New-Object System.Drawing.Size(100, 34)
$btnInstall.BackColor = [System.Drawing.Color]::FromArgb(7, 193, 96)
$btnInstall.ForeColor = [System.Drawing.Color]::White
$btnInstall.FlatStyle = 'Flat'
$form.Controls.Add($btnInstall)

$btnCancel = New-Object System.Windows.Forms.Button
$btnCancel.Text     = '取消'
$btnCancel.Location = New-Object System.Drawing.Point(428, 320)
$btnCancel.Size     = New-Object System.Drawing.Size(100, 34)
$btnCancel.Add_Click({ $form.Close() })
$form.Controls.Add($btnCancel)

$script:installed = $false

$btnInstall.Add_Click({
    $dest = $txtDir.Text.Trim()
    if ([string]::IsNullOrWhiteSpace($dest)) {
        [System.Windows.Forms.MessageBox]::Show('请填写安装位置。', '提示', 'OK', 'Warning') | Out-Null
        return
    }
    $dest = [System.IO.Path]::GetFullPath($dest)
    try {
        $probe = New-Item -ItemType Directory -Path $dest -Force
    } catch {
        [System.Windows.Forms.MessageBox]::Show("无法写入该目录：`n$dest", '提示', 'OK', 'Warning') | Out-Null
        return
    }

    $btnInstall.Enabled = $false
    $btnCancel.Enabled  = $false
    $btnBrowse.Enabled  = $false
    $txtDir.Enabled     = $false
    $chkDesktop.Enabled = $false
    $chkLaunch.Enabled  = $false

    $progressAction = {
        param($percent, $name)
        $bar.Value = [Math]::Min(100, [int]$percent)
        $status.Text = '正在复制：' + $name
        [System.Windows.Forms.Application]::DoEvents()
    }

    try {
        Install-App -Dest $dest -OnProgress $progressAction

        $bar.Value = 92
        $status.Text = '正在创建快捷方式…'
        [System.Windows.Forms.Application]::DoEvents()
        Install-Shortcuts -Dest $dest -Desktop ($chkDesktop.Checked)

        $bar.Value = 96
        $status.Text = '正在注册卸载信息…'
        [System.Windows.Forms.Application]::DoEvents()
        if (-not $NoRegistry) { Register-Uninstall -Dest $dest }

        $bar.Value = 100
        $status.Text = '安装完成'
        [System.Windows.Forms.Application]::DoEvents()
        $script:installed = $true

        [System.Windows.Forms.MessageBox]::Show(
            "安装完成！`n`n程序位置：$dest`n用户数据：$([System.IO.Path]::Combine($dest, 'data'))`n`n首次使用请在 ⚙ 设置里填写自己的 API Key（或启用本地 Ollama）。",
            '安装完成', 'OK', 'Information') | Out-Null

        if ($chkLaunch.Checked) { Start-Process (Join-Path $dest $ExeName) }
        $form.Close()
    } catch {
        $status.Text = '安装失败'
        [System.Windows.Forms.MessageBox]::Show("安装过程中出错：`n$($_.Exception.Message)", '安装失败', 'OK', 'Error') | Out-Null
        $btnInstall.Enabled = $true
        $btnCancel.Enabled  = $true
        $btnBrowse.Enabled  = $true
        $txtDir.Enabled     = $true
    }
})

[void]$form.ShowDialog()

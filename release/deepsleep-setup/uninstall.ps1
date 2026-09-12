<#
  deepsleep 图形界面卸载程序（手写实现）
  用法：双击安装目录里的「卸载 deepsleep.cmd」，或在"应用和功能"里点卸载；
        命令行静默卸载（默认保留用户数据）：
            powershell -ExecutionPolicy Bypass -File uninstall.ps1 -Silent
        连用户数据一起删除：
            powershell -ExecutionPolicy Bypass -File uninstall.ps1 -Silent -RemoveUserData
#>
param(
    [switch]$Silent,
    [switch]$RemoveUserData
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$AppName      = 'deepsleep'
$AppDisplay   = 'deepsleep AI 助手'
$ExeName      = 'deepsleep.exe'
$InstallDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $AppName

function Stop-App {
    Get-Process -Name $AppName -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill() } catch { }
    }
    Start-Sleep -Milliseconds 600
}

function Remove-Shortcuts {
    foreach ($lnk in @(
        (Join-Path ([Environment]::GetFolderPath('Programs')) ($AppDisplay + '.lnk')),
        (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) ($AppDisplay + '.lnk'))
    )) {
        if (Test-Path -LiteralPath $lnk) { Remove-Item -LiteralPath $lnk -Force -ErrorAction SilentlyContinue }
    }
}

function Remove-AllFiles {
    param([bool]$RemoveData)

    Stop-App
    if (-not $RemoveData) {
        # 保留用户数据：把 data 目录先挪到临时位置，删完程序文件再挪回来
        $dataDir = Join-Path $InstallDir 'data'
        $tempData = Join-Path $env:TEMP ('deepsleep_data_' + [Guid]::NewGuid().ToString('N'))
        $moved = $false
        if (Test-Path -LiteralPath $dataDir) {
            Move-Item -LiteralPath $dataDir -Destination $tempData -Force
            $moved = $true
        }
        Get-ChildItem -LiteralPath $InstallDir -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        if ($moved) { Move-Item -LiteralPath $tempData -Destination $dataDir -Force }
        return
    }
    Get-ChildItem -LiteralPath $InstallDir -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

function Remove-Registry {
    if (Test-Path $UninstallKey) { Remove-Item -Path $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($Silent) {
    Remove-Shortcuts
    Remove-Registry
    Remove-AllFiles -RemoveData ([bool]$RemoveUserData)
    Write-Host '卸载完成。'
    exit 0
}

# ---------------------------------------------------------------- 图形界面

[System.Windows.Forms.Application]::EnableVisualStyles()
[System.Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)

$font     = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)
$fontBold = New-Object System.Drawing.Font('Microsoft YaHei UI', 13, [System.Drawing.FontStyle]::Bold)

$form = New-Object System.Windows.Forms.Form
$form.Text            = "$AppDisplay 卸载程序"
$form.Size            = New-Object System.Drawing.Size(520, 300)
$form.StartPosition   = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox     = $false
$form.MinimizeBox     = $false
$form.Font            = $font
$form.BackColor       = [System.Drawing.Color]::FromArgb(246, 247, 249)

$title = New-Object System.Windows.Forms.Label
$title.Text     = "卸载 $AppDisplay"
$title.Font     = $fontBold
$title.Location = New-Object System.Drawing.Point(24, 22)
$title.AutoSize = $true
$form.Controls.Add($title)

$path = New-Object System.Windows.Forms.Label
$path.Text     = "程序位置：$InstallDir"
$path.Location = New-Object System.Drawing.Point(26, 58)
$path.Size     = New-Object System.Drawing.Size(460, 20)
$path.ForeColor = [System.Drawing.Color]::FromArgb(110, 116, 124)
$form.Controls.Add($path)

$chkKeep = New-Object System.Windows.Forms.CheckBox
$chkKeep.Text     = '保留用户数据（对话历史、长期记忆、API Key、自训练模型）'
$chkKeep.Checked  = $true
$chkKeep.Location = New-Object System.Drawing.Point(28, 92)
$chkKeep.Size     = New-Object System.Drawing.Size(460, 24)
$form.Controls.Add($chkKeep)

$note = New-Object System.Windows.Forms.Label
$note.Text = '取消勾选将连同 data 目录一起删除，删除后无法恢复。'
$note.Location = New-Object System.Drawing.Point(28, 122)
$note.Size = New-Object System.Drawing.Size(460, 20)
$note.ForeColor = [System.Drawing.Color]::FromArgb(180, 80, 80)
$form.Controls.Add($note)

$status = New-Object System.Windows.Forms.Label
$status.Text     = ''
$status.Location = New-Object System.Drawing.Point(28, 152)
$status.Size     = New-Object System.Drawing.Size(460, 20)
$status.ForeColor = [System.Drawing.Color]::FromArgb(90, 96, 104)
$form.Controls.Add($status)

$btnOk = New-Object System.Windows.Forms.Button
$btnOk.Text     = '卸载'
$btnOk.Location = New-Object System.Drawing.Point(280, 196)
$btnOk.Size     = New-Object System.Drawing.Size(100, 34)
$btnOk.BackColor = [System.Drawing.Color]::FromArgb(220, 70, 70)
$btnOk.ForeColor = [System.Drawing.Color]::White
$btnOk.FlatStyle = 'Flat'
$form.Controls.Add($btnOk)

$btnCancel = New-Object System.Windows.Forms.Button
$btnCancel.Text     = '取消'
$btnCancel.Location = New-Object System.Drawing.Point(388, 196)
$btnCancel.Size     = New-Object System.Drawing.Size(100, 34)
$btnCancel.Add_Click({ $form.Close() })
$form.Controls.Add($btnCancel)

$btnOk.Add_Click({
    $answer = [System.Windows.Forms.MessageBox]::Show(
        $(if ($chkKeep.Checked) { '确定卸载程序（保留用户数据）？' } else { '确定卸载程序并删除全部用户数据？此操作不可恢复。' }),
        '确认卸载', 'YesNo', 'Warning')
    if ($answer -ne 'Yes') { return }

    $btnOk.Enabled = $false
    $btnCancel.Enabled = $false
    $chkKeep.Enabled = $false
    $status.Text = '正在卸载…'
    [System.Windows.Forms.Application]::DoEvents()

    try {
        Remove-Shortcuts
        Remove-Registry
        $status.Text = '正在删除程序文件…'
        [System.Windows.Forms.Application]::DoEvents()
        Remove-AllFiles -RemoveData (-not $chkKeep.Checked)

        [System.Windows.Forms.MessageBox]::Show('卸载完成。', '完成', 'OK', 'Information') | Out-Null
        $form.Close()
    } catch {
        [System.Windows.Forms.MessageBox]::Show("卸载过程中出错：`n$($_.Exception.Message)", '出错', 'OK', 'Error') | Out-Null
        $btnOk.Enabled = $true
        $btnCancel.Enabled = $true
    }
})

[void]$form.ShowDialog()

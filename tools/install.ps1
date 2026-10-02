<#
.SYNOPSIS
    ZeOverlay 安装器 / 快捷方式 / 卸载。

.DESCRIPTION
    典型安装布局（第一层只放入口，依赖都在 app\）：

        <安装根>\                 ← 用户看到的目录，保持清爽
            启动 ZeOverlay.bat
            卸载.bat
            app\                  ← 程序本体：exe + 全部依赖 + models
                ZeOverlay.Gui.exe
                ...dll / runtime...

    本脚本可：
      1. -PickFolder  ：弹框选择「安装父目录」并把路径打印到 stdout（供自解压安装器调用）。
      2. 默认（无参） ：为已就位的程序创建桌面/开始菜单快捷方式（不复制文件）。
      3. -Uninstall   ：删除快捷方式并整体删除安装根目录（后台延迟删除，避免自身占用）。

.USAGE
    powershell -ExecutionPolicy Bypass -File install.ps1 -PickFolder
    powershell -ExecutionPolicy Bypass -File install.ps1 -AppDir "D:\Tools\ZeOverlay\app"
    powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall -AppDir "D:\Tools\ZeOverlay\app" -Root "D:\Tools\ZeOverlay"
#>
[CmdletBinding()]
param(
    # 程序本体目录（含 ZeOverlay.Gui.exe）。缺省自动探测：$PSScriptRoot\app 或 $PSScriptRoot。
    [string]$AppDir,

    # 安装根目录（卸载时整体删除）。缺省 = AppDir 的父目录。
    [string]$Root,

    [switch]$NoDesktopShortcut,
    [switch]$NoStartMenuShortcut,

    # 安装完成后立即启动。
    [switch]$Launch,

    # 卸载：删除快捷方式并删除安装根目录。
    [switch]$Uninstall,

    # 仅弹出「选择安装父目录」对话框并输出所选路径（供自解压安装器调用）。
    [switch]$PickFolder,

    # 在安装根目录写入「启动 ZeOverlay.bat」「卸载.bat」两个顶层入口（供自解压安装器调用）。
    [switch]$WriteLaunchers
)

$ErrorActionPreference = 'Stop'
$exeName = 'ZeOverlay.Gui.exe'
$appName = 'ZeOverlay'

function Write-OemFile {
    param([string]$Path, [string]$Text)

    # .bat 必须无 BOM + CRLF（PS 5.1 的 -Encoding OEM 会写 UTF-8 BOM，导致 cmd 解析错乱）。
    $normalized = ($Text -replace "`r`n", "`n") -replace "`n", "`r`n"
    [IO.File]::WriteAllText($Path, $normalized, [System.Text.Encoding]::GetEncoding(936))
}

function Get-DefaultAppDir {
    $nested = Join-Path $PSScriptRoot "app\$exeName"
    if (Test-Path $nested) { return (Join-Path $PSScriptRoot 'app') }
    if (Test-Path (Join-Path $PSScriptRoot $exeName)) { return $PSScriptRoot }
    return $null
}

function Get-ShortcutPaths {
    $paths = @()
    if (-not $NoDesktopShortcut) {
        $paths += (Join-Path ([Environment]::GetFolderPath('Desktop')) "$appName.lnk")
    }
    if (-not $NoStartMenuShortcut) {
        $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
        $paths += (Join-Path $startMenu "$appName.lnk")
    }
    return $paths
}

function New-Shortcuts {
    param([string]$ResolvedAppDir)

    $exe = Join-Path $ResolvedAppDir $exeName
    $shell = New-Object -ComObject WScript.Shell
    foreach ($lnk in (Get-ShortcutPaths)) {
        $dir = Split-Path -Parent $lnk
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $sc = $shell.CreateShortcut($lnk)
        $sc.TargetPath = $exe
        $sc.WorkingDirectory = $ResolvedAppDir
        $sc.IconLocation = "$exe,0"
        $sc.Description = 'CS2 ZE 神器列表识别 + 点击穿透叠加'
        $sc.Save()
        Write-Host "已创建快捷方式：$lnk"
    }
}

function Remove-Shortcuts {
    foreach ($lnk in (Get-ShortcutPaths)) {
        if (Test-Path $lnk) {
            Remove-Item $lnk -Force
            Write-Host "已删除快捷方式：$lnk"
        }
    }
}

# ---------- 写入顶层启动/卸载入口 ----------
if ($WriteLaunchers) {
    $target = if ($Root) { $Root } else { $PSScriptRoot }
    $target = (Resolve-Path $target).Path

    $launcherText = "@echo off`r`nstart `"`" `"%~dp0app\ZeOverlay.Gui.exe`"`r`n"
    Write-OemFile -Path (Join-Path $target '启动 ZeOverlay.bat') -Text $launcherText

    $uninstallText = "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0app\install.ps1`" -Uninstall -AppDir `"%~dp0app`" -Root `"%~dp0`"`r`npause`r`n"
    Write-OemFile -Path (Join-Path $target '卸载.bat') -Text $uninstallText

    Write-Host "已写入启动/卸载入口：$target"
    exit 0
}

# ---------- 仅选择安装父目录 ----------
if ($PickFolder) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = '选择安装位置：将在该目录下新建「ZeOverlay」文件夹并展开程序'
    $dialog.ShowNewFolderButton = $true
    $start = [Environment]::GetFolderPath('Desktop')
    if ([string]::IsNullOrWhiteSpace($start)) { $start = $env:USERPROFILE }
    $dialog.SelectedPath = $start
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
        exit 1
    }
    Write-Output $dialog.SelectedPath
    exit 0
}

# ---------- 卸载 ----------
if ($Uninstall) {
    $app = if ($AppDir) { $AppDir } else { Get-DefaultAppDir }
    $root = if ($Root) { $Root } elseif ($app) { Split-Path -Parent $app } else { $PSScriptRoot }
    $root = (Resolve-Path $root).Path

    Remove-Shortcuts

    Write-Host "安装目录：$root"
    # 后台延迟删除：本脚本/批处理可能正被占用，等 1~2 秒再整体删除。
    Start-Process -FilePath $env:ComSpec `
        -ArgumentList '/c', "ping -n 2 127.0.0.1 >nul & rmdir /s /q `"$root`"" `
        -WindowStyle Hidden
    Write-Host "卸载完成（目录将在后台删除）。" -ForegroundColor Green
    return
}

# ---------- 创建快捷方式（安装） ----------
$app = if ($AppDir) { $AppDir } else { Get-DefaultAppDir }
if (-not $app -or -not (Test-Path (Join-Path $app $exeName))) {
    throw "找不到程序（$exeName）。请用 -AppDir 指定程序目录，或把本脚本放在程序目录里。"
}
$app = (Resolve-Path $app).Path

New-Shortcuts -ResolvedAppDir $app

Write-Host ""
Write-Host "安装完成：$(Join-Path $app $exeName)" -ForegroundColor Green
Write-Host "首次运行请用 Ctrl+Shift+R 框选列表区域完成标定。"

if ($Launch) {
    Start-Process -FilePath (Join-Path $app $exeName) -WorkingDirectory $app
}

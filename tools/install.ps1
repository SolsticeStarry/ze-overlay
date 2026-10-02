<#
.SYNOPSIS
    ZeOverlay 便携安装器：把自包含发布目录安装到本机，并创建快捷方式。

.DESCRIPTION
    发布包已自带 .NET 运行时，安装只做两件事：
      1. 复制文件到安装目录（默认 %LOCALAPPDATA%\ZeOverlay）。
      2. 创建桌面 / 开始菜单快捷方式，并写一个卸载入口。

    脚本默认把「自己所在目录」当作发布源；因此可直接放在发布包根目录双击
    「安装到本机.bat」运行，也可以指定 -Source 指向别处。

.USAGE
    # 默认安装（源=本脚本所在目录）
    powershell -ExecutionPolicy Bypass -File install.ps1

    # 指定安装位置、跳过桌面快捷方式
    powershell -ExecutionPolicy Bypass -File install.ps1 -InstallDir D:\Tools\ZeOverlay -NoDesktopShortcut

    # 卸载
    powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    # 发布源目录（含 ZeOverlay.Gui.exe）。默认取本脚本所在目录。
    [string]$Source,

    # 安装目录。默认 %LOCALAPPDATA%\ZeOverlay。
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'ZeOverlay'),

    [switch]$NoDesktopShortcut,
    [switch]$NoStartMenuShortcut,

    # 安装完成后立即启动。
    [switch]$Launch,

    # 卸载：删除快捷方式与安装目录。
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$exeName = 'ZeOverlay.Gui.exe'
$appName = 'ZeOverlay'

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

function Remove-Shortcuts {
    foreach ($lnk in (Get-ShortcutPaths)) {
        if (Test-Path $lnk) {
            Remove-Item $lnk -Force
            Write-Host "已删除快捷方式：$lnk"
        }
    }
}

# ---------- 卸载 ----------
if ($Uninstall) {
    Remove-Shortcuts
    if (Test-Path $InstallDir) {
        Remove-Item $InstallDir -Recurse -Force
        Write-Host "已删除安装目录：$InstallDir"
    } else {
        Write-Host "安装目录不存在，跳过：$InstallDir"
    }
    Write-Host "卸载完成。" -ForegroundColor Green
    return
}

# ---------- 解析源目录 ----------
if (-not $Source) {
    if (Test-Path (Join-Path $PSScriptRoot $exeName)) {
        $Source = $PSScriptRoot
    } else {
        # 从仓库 tools/ 运行时，回退到默认发布目录。
        $Source = Join-Path (Split-Path -Parent $PSScriptRoot) 'publish\ZeOverlay'
    }
}

if (-not (Test-Path (Join-Path $Source $exeName))) {
    throw "发布源不合法（找不到 $exeName）：$Source`n请先运行 tools\publish.ps1，或把本脚本放到发布目录里。"
}
$Source = (Resolve-Path $Source).Path

Write-Host "源目录：$Source"
Write-Host "安装到：$InstallDir"

# ---------- 复制文件 ----------
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
if ($Source -ne (Resolve-Path $InstallDir).Path) {
    # 保留用户已有配置：不覆盖已存在的 config.json / watchlist.json / shots / logs。
    $preserve = @('config.json', 'watchlist.json', 'shots', 'logs')
    foreach ($name in $preserve) {
        $existing = Join-Path $InstallDir $name
        if (Test-Path $existing) {
            $bak = Join-Path $InstallDir ("~{0}" -f $name)
            if (Test-Path $bak) { Remove-Item $bak -Recurse -Force }
            Move-Item $existing $bak
        }
    }

    robocopy $Source $InstallDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy 失败（退出码 $LASTEXITCODE）" }

    foreach ($name in $preserve) {
        $bak = Join-Path $InstallDir ("~{0}" -f $name)
        if (Test-Path $bak) {
            $target = Join-Path $InstallDir $name
            if (Test-Path $target) { Remove-Item $target -Recurse -Force }
            Move-Item $bak $target
        }
    }
} else {
    Write-Host "源与目标相同，跳过复制。"
}

# ---------- 创建快捷方式 ----------
$exe = Join-Path $InstallDir $exeName
$shell = New-Object -ComObject WScript.Shell
foreach ($lnk in (Get-ShortcutPaths)) {
    $dir = Split-Path -Parent $lnk
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $sc = $shell.CreateShortcut($lnk)
    $sc.TargetPath = $exe
    $sc.WorkingDirectory = $InstallDir
    $sc.IconLocation = "$exe,0"
    $sc.Description = 'CS2 ZE 神器列表识别 + 点击穿透叠加'
    $sc.Save()
    Write-Host "已创建快捷方式：$lnk"
}

# ---------- 写入卸载入口 ----------
$uninstallBat = Join-Path $InstallDir '卸载.bat'
@"
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Uninstall
pause
"@ | Set-Content -Path $uninstallBat -Encoding OEM

Write-Host ""
Write-Host "安装完成：$exe" -ForegroundColor Green
Write-Host "首次运行请用 Ctrl+Shift+R 框选列表区域完成标定。"

if ($Launch) {
    Start-Process -FilePath $exe -WorkingDirectory $InstallDir
}

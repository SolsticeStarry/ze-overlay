#Requires -Version 5.1
<#
.SYNOPSIS
    把发布目录打包成**单个自解压安装程序 .exe**（Windows 内置 IExpress）。

.DESCRIPTION
    产物运行后：
      1. 自解压到临时目录（内含 payload.zip / install.cmd / install.ps1）；
      2. 弹出「选择安装位置」文件夹对话框（可新建文件夹，默认 %LOCALAPPDATA%\ZeOverlay）；
      3. 在该目录展开全部文件，并创建桌面 / 开始菜单快捷方式与卸载入口。

    之所以用 IExpress：目标机无需预装 7-Zip / NSIS / .NET，Windows 自带；
    payload 已压缩，IExpress 不再二次压缩（InsideCompressed=0），构建快、体积小。

.USAGE
    powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Zip
    powershell -ExecutionPolicy Bypass -File tools\make-installer.ps1
    powershell -ExecutionPolicy Bypass -File tools\make-installer.ps1 -PublishDir publish\ZeOverlay -Out publish\ZeOverlay-Setup.exe
    powershell -ExecutionPolicy Bypass -File tools\make-installer.ps1 -SignPfx cert.pfx -SignPfxPassword ***
#>
[CmdletBinding()]
param(
    # 已发布的运行目录（默认 publish\ZeOverlay）。
    [string]$PublishDir,

    # 输出安装器路径（默认 publish\ZeOverlay-Setup.exe）。
    [string]$Out,

    # 安装器图标（默认 src\ZeOverlay.Gui\app.ico）；给空字符串可禁用。
    [string]$Icon,

    # 可选代码签名。
    [string]$SignPfx,
    [string]$SignPfxPassword,
    [string]$SignThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

Add-Type -Namespace ZeSetup -Name Win -MemberDefinition @'
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern System.IntPtr BeginUpdateResource(string fileName, bool deleteExisting);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateResource(System.IntPtr h, System.IntPtr type, System.IntPtr name, short lang, byte[] data, int size);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool EndUpdateResource(System.IntPtr h, bool discard);
'@

function Write-OemFile {
    param([string]$Path, [string]$Text)

    # .cmd 必须无 BOM + CRLF（PS 5.1 的 -Encoding OEM 会写 UTF-8 BOM，导致 cmd 解析错乱）。
    $normalized = ($Text -replace "`r`n", "`n") -replace "`n", "`r`n"
    [IO.File]::WriteAllText($Path, $normalized, [System.Text.Encoding]::GetEncoding(936))
}

function Set-ExeIcon {
    param([string]$Exe, [string]$IconFile)

    if (-not (Test-Path $IconFile)) { Write-Warning "图标不存在，跳过：$IconFile"; return }

    $ico = [IO.File]::ReadAllBytes($IconFile)
    if ($ico.Length -lt 6 -or [BitConverter]::ToUInt16($ico, 2) -ne 1) {
        Write-Warning "不是合法的 .ico，跳过图标替换。"
        return
    }

    $count = [BitConverter]::ToUInt16($ico, 4)
    $images = New-Object System.Collections.Generic.List[object]
    for ($i = 0; $i -lt $count; $i++) {
        $e = 6 + $i * 16
        $len = [BitConverter]::ToUInt32($ico, $e + 8)
        $off = [BitConverter]::ToUInt32($ico, $e + 12)
        if ($off + $len -gt $ico.Length) { continue }
        $data = New-Object byte[] $len
        [Array]::Copy($ico, $off, $data, 0, $len)
        $images.Add([pscustomobject]@{
                Id       = $i + 1
                Data     = $data
                Width    = $ico[$e]
                Height   = $ico[$e + 1]
                Colors   = $ico[$e + 2]
                Planes   = [BitConverter]::ToUInt16($ico, $e + 4)
                BitCount = [BitConverter]::ToUInt16($ico, $e + 6)
            })
    }
    if ($images.Count -eq 0) { Write-Warning "ico 内没有图像，跳过。"; return }

    # RT_GROUP_ICON 目录：6 字节 + 每项 14 字节。
    $grp = New-Object System.Collections.Generic.List[byte]
    $grp.AddRange([byte[]]@(0, 0, 1, 0, [byte]($count -band 0xFF), [byte](($count -shr 8) -band 0xFF)))
    foreach ($im in $images) {
        $grp.Add([byte]$im.Width); $grp.Add([byte]$im.Height); $grp.Add([byte]$im.Colors); $grp.Add(0)
        $grp.Add([byte]($im.Planes -band 0xFF)); $grp.Add([byte](($im.Planes -shr 8) -band 0xFF))
        $grp.Add([byte]($im.BitCount -band 0xFF)); $grp.Add([byte](($im.BitCount -shr 8) -band 0xFF))
        $l = $im.Data.Length
        $grp.Add([byte]($l -band 0xFF)); $grp.Add([byte](($l -shr 8) -band 0xFF))
        $grp.Add([byte](($l -shr 16) -band 0xFF)); $grp.Add([byte](($l -shr 24) -band 0xFF))
        $grp.Add([byte]($im.Id -band 0xFF)); $grp.Add([byte](($im.Id -shr 8) -band 0xFF))
    }

    $h = [ZeSetup.Win]::BeginUpdateResource($Exe, $false)
    if ($h -eq [IntPtr]::Zero) { Write-Warning "替换图标失败（BeginUpdateResource）。"; return }

    $ok = $true
    try {
        foreach ($im in $images) {
            if (-not [ZeSetup.Win]::UpdateResource($h, [IntPtr]3, [IntPtr]$im.Id, 0, $im.Data, $im.Data.Length)) { $ok = $false }
        }
        $group = $grp.ToArray()
        if (-not [ZeSetup.Win]::UpdateResource($h, [IntPtr]14, [IntPtr]1, 0, $group, $group.Length)) { $ok = $false }
    }
    catch {
        $ok = $false
    }
    finally {
        # 失败则丢弃改动，保证不会产出损坏的 exe。
        [void][ZeSetup.Win]::EndUpdateResource($h, -not $ok)
    }

    if ($ok) { Write-Host "已替换安装器图标。" } else { Write-Warning "替换图标失败，保留默认图标。" }
}

function Invoke-Sign {
    param([string]$File)

    if (-not $SignPfx -and -not $SignThumbprint) { return }

    $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    if (-not $signtool) {
        $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
        $signtool = Get-ChildItem $kits -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
    }
    if (-not $signtool) { Write-Warning "未找到 signtool.exe，跳过签名。"; return }

    $signArgs = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')
    if ($SignThumbprint) { $signArgs += @('/sha1', $SignThumbprint) } else { $signArgs += @('/f', $SignPfx) }
    if ($SignPfxPassword) { $signArgs += @('/p', $SignPfxPassword) }
    $signArgs += $File
    & $signtool @signArgs
    if ($LASTEXITCODE -ne 0) { Write-Warning "签名失败：$File" } else { Write-Host "已签名：$File" }
}

# ---------------- 主流程 ----------------
$root = Split-Path -Parent $PSScriptRoot
if (-not $PublishDir) { $PublishDir = Join-Path $root 'publish\ZeOverlay' }
if (-not $Out) { $Out = Join-Path $root 'publish\ZeOverlay-Setup.exe' }
if (-not $Icon) { $Icon = Join-Path $root 'src\ZeOverlay.Gui\app.ico' }

$PublishDir = (Resolve-Path $PublishDir).Path
$exeName = 'ZeOverlay.Gui.exe'
if (-not (Test-Path (Join-Path $PublishDir $exeName))) {
    throw "发布目录不合法（缺少 $exeName）：$PublishDir`n请先运行 tools\publish.ps1。"
}

$iexpress = Join-Path $env:SystemRoot 'System32\iexpress.exe'
if (-not (Test-Path $iexpress)) { throw "找不到 IExpress：$iexpress" }

$outDir = Split-Path -Parent $Out
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$stage = Join-Path $env:TEMP ('ze-setup-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null

try {
    Write-Host "暂存目录：$stage"

    # 1) 载荷：整个发布目录压成一个 zip。
    $payload = Join-Path $stage 'payload.zip'
    Write-Host "压缩载荷中（$((Get-ChildItem $PublishDir -Recurse -File).Count) 个文件）..."
    Compress-Archive -Path (Join-Path $PublishDir '*') -DestinationPath $payload -CompressionLevel Optimal
    Write-Host ("  payload.zip = {0} MB" -f [math]::Round((Get-Item $payload).Length / 1MB, 1))

    # 2) 引导 cmd：选目录 → 展开 → 建快捷方式。
    $installCmd = Join-Path $stage 'install.cmd'
    $installCmdText = @'
@echo off
setlocal enableextensions enabledelayedexpansion
set "HERE=%~dp0"
set "PAYLOAD=%HERE%payload.zip"
set "LOG=%TEMP%\ze-install.log"
echo [%DATE% %TIME%] start ZE_INSTALL_DIR="%ZE_INSTALL_DIR%" ZE_INSTALL_ROOT="%ZE_INSTALL_ROOT%" HERE="%HERE%" > "%LOG%"

rem 目标根目录：
rem   ZE_INSTALL_DIR  = 精确目录（CI 用，直接用）
rem   否则取 ZE_INSTALL_ROOT 或弹框选择“安装到哪个目录/盘”，再在其下新建 ZeOverlay 子文件夹
set "TARGET=!ZE_INSTALL_DIR!"
if not defined TARGET (
  set "ROOT=!ZE_INSTALL_ROOT!"
  if not defined ROOT (
    for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%install.ps1" -PickFolder`) do set "ROOT=%%I"
  )
  echo picked_root="!ROOT!" >> "%LOG%"
  if not defined ROOT (
    echo cancelled >> "%LOG%"
    echo.
    echo 已取消安装。
    if not defined ZE_QUIET pause
    exit /b 1
  )
  for %%A in ("!ROOT!") do set "LEAF=%%~nxA"
  set "TARGET=!ROOT!\ZeOverlay"
  if /I "!LEAF!"=="ZeOverlay" set "TARGET=!ROOT!"
)

rem 程序本体（含全部依赖）解压到 app\，第一层只留启动器与卸载入口。
set "APPDIR=!TARGET!\app"
echo target="!TARGET!" app="!APPDIR!" >> "%LOG%"
echo 安装位置: !TARGET!
if not exist "!APPDIR!" mkdir "!APPDIR!"

echo 正在展开文件，请稍候...
echo expanding >> "%LOG%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath '%PAYLOAD%' -DestinationPath '!APPDIR!' -Force" >> "%LOG%" 2>&1
if errorlevel 1 (
  echo 解压失败。>> "%LOG%"
  echo 解压失败。
  if not defined ZE_QUIET pause
  exit /b 1
)

rem 顶层入口（用 PowerShell 以 Unicode 安全方式写入，避免 cmd 写坏中文文件名）
powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%install.ps1" -WriteLaunchers -Root "!TARGET!" >> "%LOG%" 2>&1

set "OPTS="
if defined ZE_QUIET set "OPTS=-NoDesktopShortcut -NoStartMenuShortcut"
if defined ZE_LAUNCH set "OPTS=%OPTS% -Launch"

echo 正在创建快捷方式...
powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%install.ps1" -AppDir "!APPDIR!" %OPTS% >> "%LOG%" 2>&1

echo.
echo 安装完成：!APPDIR!\ZeOverlay.Gui.exe
echo done >> "%LOG%"
if not defined ZE_QUIET pause
exit /b 0
'@
    Write-OemFile -Path $installCmd -Text $installCmdText

    # 3) 安装逻辑脚本（选目录 / 复制 / 快捷方式 / 卸载）。
    Copy-Item (Join-Path $root 'tools\install.ps1') (Join-Path $stage 'install.ps1') -Force

    # 4) 生成 IExpress 的 SED 描述。
    #    注意：SED 必须是 ASCII，且 TargetName 不能含非 ASCII 字符，
    #    故先构建到暂存目录（纯 ASCII），成功后再移动到最终路径。
    $buildExe = Join-Path $stage 'ZeOverlay-Setup.exe'
    $files = @('payload.zip', 'install.cmd', 'install.ps1')
    $sourceList = ($files | ForEach-Object -Begin { $i = 0 } -Process { "%FILE$i%="; $i++ }) -join "`r`n"
    $strings = ($files | ForEach-Object -Begin { $i = 0 } -Process { "FILE$i=`"$_`""; $i++ }) -join "`r`n"

    $sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$buildExe
FriendlyName=ZeOverlay Setup
AppLaunched=cmd /c install.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles
[SourceFiles]
SourceFiles0=$stage\
[SourceFiles0]
$sourceList
[Strings]
$strings
"@

    $sedPath = Join-Path $stage 'setup.sed'
    Set-Content -Path $sedPath -Value $sed -Encoding ASCII

    # 5) 构建（/N 用 SED，/Q 静默）。
    if (Test-Path $Out) { Remove-Item $Out -Force }
    if (Test-Path $buildExe) { Remove-Item $buildExe -Force }
    Write-Host "IExpress 构建中..."
    $proc = Start-Process -FilePath $iexpress -ArgumentList @('/N', '/Q', $sedPath) -PassThru -Wait
    if ($proc.ExitCode -ne 0 -or -not (Test-Path $buildExe)) {
        throw "IExpress 构建失败（退出码 $($proc.ExitCode)）。SED：$sedPath"
    }

    # 从 ASCII 暂存路径移到最终路径（最终路径可能含中文）。
    Move-Item $buildExe $Out -Force

    if ($Icon -and (Test-Path $Icon)) { Set-ExeIcon -Exe $Out -IconFile $Icon }

    Invoke-Sign -File $Out

    $sizeMb = [math]::Round((Get-Item $Out).Length / 1MB, 1)
    Write-Host ""
    Write-Host "=== 完成 ===" -ForegroundColor Green
    Write-Host ("安装器：{0}  （{1} MB，单文件自解压）" -f $Out, $sizeMb)
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

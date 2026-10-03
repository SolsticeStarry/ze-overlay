<#
.SYNOPSIS
    构建 ZeOverlay 的自包含（self-contained）单文件夹发布包。

.DESCRIPTION
    产物布局（默认）：
        publish/ZeOverlay/                 自包含运行目录（exe + 运行库 + models/）
        publish/ZeOverlay-cli/             可选，-IncludeCli 时生成
        publish/ZeOverlay-<rid>.zip        可选，-Zip 时生成

    关键点：
    * self-contained + 指定 RID：目标机无需安装 .NET 运行时。
    * 单文件夹（PublishSingleFile=false）：config.json / watchlist.json / shots /
      logs 仍固定在 exe 旁可写（见 docs/DESIGN.md §3），避免解压到 %TEMP% 后
      运行时数据到处乱跑。
    * 运行时不需要检测（det）模型：发布副本里剔除，省 2.3 MB。
    * 调试符号（*.pdb）不进发布包。

.USAGE
    powershell -ExecutionPolicy Bypass -File tools\publish.ps1
    powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Zip -IncludeCli
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$RuntimeIdentifier = 'win-x64',

    # 同时发布离线工具 ZeOverlay.Cli（默认不发布，终端用户不需要）。
    [switch]$IncludeCli,

    # 生成可分发的 zip。
    [switch]$Zip,

    # 预编译（ReadyToRun）：启动略快，体积更大。默认关闭。
    [switch]$ReadyToRun,

    # NuGet 走本地代理（本机直连不稳时用）。
    [string]$Proxy,

    # 代码签名（可选）：提供 PFX 文件或证书指纹（二者其一）；不提供则跳过。
    # 例：-SignPfx cert.pfx -SignPfxPassword ***   或   -SignThumbprint <sha1>
    [string]$SignPfx,
    [string]$SignPfxPassword,
    [string]$SignThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

# 写 .bat/.cmd：必须**无 BOM + CRLF**，否则 cmd 会把 BOM 当成命令而解析错乱。
# 注意：PowerShell 5.1 的 `Set-Content -Encoding OEM` 会写 UTF-8 BOM，不能用。
function Write-OemFile {
    param([string]$Path, [string]$Text)

    $normalized = ($Text -replace "`r`n", "`n") -replace "`n", "`r`n"
    [IO.File]::WriteAllText($Path, $normalized, [System.Text.Encoding]::GetEncoding(936))
}

if ($Proxy) {
    $env:HTTP_PROXY = $Proxy
    $env:HTTPS_PROXY = $Proxy
    Write-Host "已设置代理：$Proxy"
}

$root = Split-Path -Parent $PSScriptRoot
$guiProj = Join-Path $root 'src\ZeOverlay.Gui\ZeOverlay.Gui.csproj'
$cliProj = Join-Path $root 'src\ZeOverlay.Cli\ZeOverlay.Cli.csproj'
$outRoot = Join-Path $root 'publish'
$guiOut = Join-Path $outRoot 'ZeOverlay'

# 发布到此仓库内目录；先清掉旧产物，避免残留已删除的文件。
if (Test-Path $outRoot) {
    Remove-Item $outRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $outRoot -Force | Out-Null

$common = @(
    '-c', $Configuration,
    '-r', $RuntimeIdentifier,
    '--self-contained', 'true',
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '-p:DebugType=none',
    '-p:DebugSymbols=false',
    '-p:GenerateDocumentationFile=false',
    '-p:ErrorOnDuplicatePublishOutputFiles=false'
)
if ($ReadyToRun) { $common += '-p:PublishReadyToRun=true' }

function Publish-Project {
    param([string]$Project, [string]$Output)

    Write-Host ""
    Write-Host "=== 发布 $([IO.Path]::GetFileNameWithoutExtension($Project)) -> $Output ===" -ForegroundColor Cyan
    & dotnet publish $Project @common -o $Output
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败：$Project（退出码 $LASTEXITCODE）"
    }

    # 精简：运行时不需要 det 模型；调试符号不进发布包。
    $det = Join-Path $Output 'models\ch_PP-OCRv3_det_infer.onnx'
    if (Test-Path $det) {
        Remove-Item $det -Force
        Write-Host "  已剔除运行时无用的检测模型：ch_PP-OCRv3_det_infer.onnx（-2.3 MB）"
    }

    # 精简：WinForms 从未被本 WPF 应用引用（实测删掉后仍正常启动），但 WindowsDesktop
    # 框架会把它带进自包含发布包（约 21 MB）。
    $winForms = @(
        'System.Windows.Forms.dll',
        'System.Windows.Forms.Design.dll',
        'System.Windows.Forms.Primitives.dll',
        'System.Windows.Forms.Design.Editors.dll'
    )
    $pruned = 0
    foreach ($name in $winForms) {
        $path = Join-Path $Output $name
        if (Test-Path $path) { Remove-Item $path -Force; $pruned++ }
    }
    if ($pruned -gt 0) {
        Write-Host "  已剔除未引用的 WinForms 程序集（-$pruned 个，约 21 MB）"
    }

    Get-ChildItem $Output -Recurse -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force
}

function Invoke-Sign {
    param([string]$File)

    if (-not $SignPfx -and -not $SignThumbprint) { return }
    if (-not (Test-Path $File)) { Write-Warning "签名目标不存在：$File"; return }

    $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    if (-not $signtool) {
        $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
        $signtool = Get-ChildItem $kits -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
    }
    if (-not $signtool) {
        Write-Warning "未找到 signtool.exe（需 Windows SDK），跳过代码签名。"
        return
    }

    $signArgs = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')
    if ($SignThumbprint) { $signArgs += @('/sha1', $SignThumbprint) }
    else { $signArgs += @('/f', $SignPfx) }
    if ($SignPfxPassword) { $signArgs += @('/p', $SignPfxPassword) }
    $signArgs += $File

    & $signtool @signArgs
    if ($LASTEXITCODE -ne 0) { Write-Warning "代码签名失败：$File" } else { Write-Host "已签名：$File" }
}

Publish-Project -Project $guiProj -Output $guiOut

# 精简：运行时默认只用 v6，v3 rec 仅留仓库作开发回退，不进发布包（-10.2 MB）。
$legacyRec = Join-Path $guiOut 'models\ch_PP-OCRv3_rec_infer.onnx'
if (Test-Path $legacyRec) {
    Remove-Item $legacyRec -Force
    Write-Host "  已剔除发布包里的旧 v3 rec 模型（-10.2 MB；改用 v6）"
}

# 便携启动器（双击即用，无控制台窗口）。
$launcher = Join-Path $guiOut '启动 ZeOverlay.bat'
$launcherText = @'
@echo off
rem 便携启动：切到本目录，避免工作目录影响。真正的数据目录始终是 exe 所在目录。
cd /d "%~dp0"
start "" "ZeOverlay.Gui.exe"
'@
Write-OemFile -Path $launcher -Text $launcherText

# 自包含安装器：把 install.ps1 与一个双击入口一起放进发布包。
$installer = Join-Path $root 'tools\install.ps1'
if (Test-Path $installer) {
    Copy-Item $installer (Join-Path $guiOut 'install.ps1') -Force
    $installBat = Join-Path $guiOut '安装到本机.bat'
    $installBatText = @'
@echo off
rem 双击运行：为当前目录的程序创建桌面/开始菜单快捷方式（不复制文件）。
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
pause
'@
    Write-OemFile -Path $installBat -Text $installBatText
}

if ($IncludeCli) {
    $cliOut = Join-Path $outRoot 'ZeOverlay-cli'
    Publish-Project -Project $cliProj -Output $cliOut
}

# 可选代码签名：在打包 zip 之前签，保证 zip 内也是签好的 exe。
Invoke-Sign (Join-Path $guiOut 'ZeOverlay.Gui.exe')

# 统计与可选打包。
$guiFiles = Get-ChildItem $guiOut -Recurse -File
$guiMb = [math]::Round(($guiFiles | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host ""
Write-Host "=== 完成 ===" -ForegroundColor Green
Write-Host ("GUI  ：{0}  （{1} 个文件，{2} MB）" -f $guiOut, $guiFiles.Count, $guiMb)

if ($Zip) {
    $zipPath = Join-Path $outRoot ("ZeOverlay-{0}.zip" -f $RuntimeIdentifier)
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Write-Host "打包中：$zipPath ..."
    Compress-Archive -Path (Join-Path $guiOut '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host ("ZIP  ：{0}  （{1} MB）" -f $zipPath, $zipMb)
}

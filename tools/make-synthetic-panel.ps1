# 生成「仿真实神器列表」合成图，用于在拿到真机截图前验证行剖分链路。
#
# 两种用法：
#   1) 默认（推荐）：只画列表本身，尺寸按 ROI 量级 —— 这才是分析时真正会喂进去的图。
#   2) -IncludeChat ：额外把左下角聊天栏画进来，**仅用于验证「ROI 框宽了」的告警**。
#      聊天栏每帧都在变且没有识别价值，绝不应该出现在真实 ROI 里。
param(
    [string]$Out = "docs\ref\synthetic_roi.png",
    [int]$Width = 560,
    [int]$Height = 300,
    [switch]$IncludeChat
)

Add-Type -AssemblyName System.Drawing

$lines = @(
    '皇家口粮 [R]1/1 亦陌雕',
    '紫色瓶中闪电 [R] 用户6744311',
    '爆闪相机 [R] （一个真正的man）',
    '滋水枪 [R] Mr.YYYX',
    '爆闪相机 [12] 真就一颗a',
    '黑色瓶中闪电 [R]1/1 不会狙娱乐',
    '手电筒 [R] 游戏尘寰丶',
    '灵体定位仪 [20] 小柒Lucky',
    '袋装火盐 [R] 街望',
    '桶装杏仁水 [R] 豆浆机洗脚',
    '滋水枪 [R] 楚门潇'
)

if ($IncludeChat) {
    $Width = 1920
    $Height = 1080
    $listLeft = 1381
    $listTop = 230
} else {
    $listLeft = 6
    $listTop = 8
}

$bmp = New-Object System.Drawing.Bitmap $Width, $Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.TextRenderingHint = 'AntiAliasGridFit'

# 背景：模拟游戏场景（暗绿灰渐变，亮度不均），刻意不用纯色，以验证梯度法对底色不敏感。
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point 0, 0),
    (New-Object System.Drawing.Point $Width, $Height),
    [System.Drawing.Color]::FromArgb(255, 78, 74, 52),
    [System.Drawing.Color]::FromArgb(255, 24, 26, 22))
$g.FillRectangle($bg, 0, 0, $Width, $Height)

$font = New-Object System.Drawing.Font('Microsoft YaHei', 20, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$fmt = New-Object System.Drawing.StringFormat
$fmt.Alignment = 'Near'

$pitch = 26
for ($i = 0; $i -lt $lines.Count; $i++) {
    $y = $listTop + $i * $pitch
    $outline = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230, 0, 0, 0))
    foreach ($d in @(@(-1, -1), @(1, -1), @(-1, 1), @(1, 1))) {
        $g.DrawString($lines[$i], $font, $outline, ($listLeft + $d[0]), ($y + $d[1]), $fmt)
    }
    $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 245, 245, 240))
    $g.DrawString($lines[$i], $font, $fill, $listLeft, $y, $fmt)
}

if ($IncludeChat) {
    # 反向用例：把聊天栏也画进来，用来验证「横向多区域」告警能触发。
    $noiseFont = New-Object System.Drawing.Font('Microsoft YaHei', 18, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $chat = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 230, 120))
    $g.DrawString('[EXG社区] 鸟鸦先生对我说 捡起了神器 桶装杏仁水', $noiseFont, $chat, 90, 900, $fmt)
    $g.DrawString('[EXG社区] 楚门潇 捡起了神器 滋水枪', $noiseFont, $chat, 90, 930, $fmt)
}

$g.Dispose()

$full = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Out))
$dir = Split-Path -Parent $full
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Output "wrote $full  ($Width x $Height, chat=$IncludeChat)"

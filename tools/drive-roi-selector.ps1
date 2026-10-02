# 仅用于本项目自测：用 Win32 输入 API 驱动「ZeOverlay 自己的」框选覆盖层。
# 不针对任何第三方程序（尤其不针对 cs2.exe）。
param(
    [int]$X1 = 300,
    [int]$Y1 = 260,
    [int]$X2 = 900,
    [int]$Y2 = 620,
    [int]$StepDelayMs = 25
)

Add-Type -Namespace ZeTest -Name Mouse -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)]
public struct POINT { public int X; public int Y; }

[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

public const uint LEFTDOWN = 0x0002;
public const uint LEFTUP = 0x0004;
public const byte VK_RETURN = 0x0D;
public const uint KEYEVENTF_KEYUP = 0x0002;
'@

$m = [ZeTest.Mouse]

$m::SetCursorPos($X1, $Y1) | Out-Null
Start-Sleep -Milliseconds 200
$m::mouse_event($m::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)

$steps = 24
for ($i = 1; $i -le $steps; $i++) {
    $x = [int]($X1 + ($X2 - $X1) * $i / $steps)
    $y = [int]($Y1 + ($Y2 - $Y1) * $i / $steps)
    $m::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds $StepDelayMs
}

$m::mouse_event($m::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 250

# 确认（Enter）
$m::keybd_event($m::VK_RETURN, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 60
$m::keybd_event($m::VK_RETURN, 0, $m::KEYEVENTF_KEYUP, [IntPtr]::Zero)

Write-Output "DRAG_DONE $X1,$Y1 -> $X2,$Y2"

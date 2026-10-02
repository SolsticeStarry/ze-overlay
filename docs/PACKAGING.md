# 打包与安装（M6）

> 目标：**自包含单文件夹**（目标机无需装 .NET），并提供一个可双击的**启动安装包**。
> 决策依据与性能数字见 `DESIGN.md` §5 M6。

## 为什么是「单文件夹」而不是「单文件」

运行时数据（`config.json` / `watchlist.json` / `models/` / `shots/` / `logs/`）按设计
**固定在 exe 旁且可写**，路径取自 `Paths.ForExecutable()`（`AppContext.BaseDirectory`）。

若把 `models/` 一起打进单文件、运行时解压到 `%TEMP%`，`config`/`shots`/`logs` 会跟着跑到
临时目录，违背「数据在 exe 旁」的设计，也让用户找不到自己的配置与截图。
因此采用：**一个自包含文件夹**（exe + 运行库 + `models/`），双击即用。

## 命令

```powershell
# 发布（默认 GUI；.NET 运行时已内嵌，无需目标机安装 .NET）
powershell -ExecutionPolicy Bypass -File tools\publish.ps1

# 需要 zip 分发包 + 同时发布离线 CLI
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Zip -IncludeCli

# NuGet 直连不稳时走本地代理
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Proxy http://127.0.0.1:7897

# 代码签名（可选，需 Windows SDK 的 signtool.exe）：PFX 或证书指纹二选一
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Zip -SignPfx cert.pfx -SignPfxPassword ***
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Zip -SignThumbprint <sha1>
```

产物（`publish/` 已在 `.gitignore` 中）：

```
publish/ZeOverlay/                  自包含运行目录
    ZeOverlay.Gui.exe
    models/ch_PP-OCRv3_rec_infer.onnx
    models/ppocr_keys_v1.txt
    启动 ZeOverlay.bat              便携启动（双击）
    install.ps1                     安装器
    安装到本机.bat                  双击安装（复制到本机 + 建快捷方式）
publish/ZeOverlay-cli/              仅 -IncludeCli 时生成
publish/ZeOverlay-win-x64.zip       仅 -Zip 时生成
```

发布脚本会**精简**运行时用不到的东西：

| 剔除项 | 原因 | 省下 |
|---|---|---|
| `models/ch_PP-OCRv3_det_infer.onnx` | 项目不接检测（det）模型，只用 rec | 2.3 MB |
| `*.pdb` | 发布包不需要调试符号 | 若干 |

## 安装 / 卸载

安装器只做两件事：把发布目录复制到本机；建桌面 / 开始菜单快捷方式。

```powershell
# 双击发布包里的「安装到本机.bat」，或命令行：
powershell -ExecutionPolicy Bypass -File install.ps1

# 指定位置、跳过桌面快捷方式、装完即启动
powershell -ExecutionPolicy Bypass -File install.ps1 -InstallDir D:\Tools\ZeOverlay -NoDesktopShortcut -Launch

# 卸载（删快捷方式 + 安装目录）
powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
```

- 默认安装到 `%LOCALAPPDATA%\ZeOverlay`。
- 覆盖安装时**保留**已有的 `config.json` / `watchlist.json` / `shots` / `logs`。
- 安装目录内会生成 `卸载.bat`。

## 实测（2026-10-02，win-x64，Release）

| 项 | 值 |
|---|---|
| 发布产物 | 283 个文件，**214.9 MB**（含 WPF + onnxruntime + DirectML） |
| zip（Optimal） | **91.8 MB** |
| 自包含启动 | ✅ 目标机无需 .NET；启动日志正常 |
| **DirectML 在单文件夹下加载** | ✅ 日志 `PP-OCRv3-rec(6624 类)+DML`，无缺 DLL 崩溃 |
| 安装/卸载可逆 | ✅ 复制、桌面/开始菜单快捷方式、卸载均验证通过 |

## 单文件安装程序（.exe，给最终用户）

把发布目录打成**一个自解压 `.exe`**，双击即可安装；用 Windows 自带的 IExpress，目标机无需预装任何东西。

```powershell
# 先发布，再打包安装器（默认读 publish\ZeOverlay，输出 publish\ZeOverlay-Setup.exe）
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Zip
powershell -ExecutionPolicy Bypass -File tools\make-installer.ps1

# 指定路径 / 顺带签名
powershell -ExecutionPolicy Bypass -File tools\make-installer.ps1 -Out publish\ZeOverlay-Setup.exe -SignPfx cert.pfx -SignPfxPassword ***
```

安装器运行流程：

1. 自解压到 `%TEMP%\IXP000.TMP`（内含 `payload.zip` / `install.cmd` / `install.ps1`）；
2. 弹出**「选择安装位置」文件夹对话框**（可点「新建文件夹」，默认 `%LOCALAPPDATA%\ZeOverlay`）；
3. 在该目录**展开全部文件**（若目录不存在会自动新建）；
4. 创建桌面 / 开始菜单快捷方式，并写入 `卸载.bat`。

| 项 | 值 |
|---|---|
| 安装器 | `publish\ZeOverlay-Setup.exe`，**约 90.7 MB**（内嵌 92 MB payload.zip） |
| 展开结果 | 283 文件 / 215 MB（含 `DirectML.dll`、`models/`） |
| 卸载 | 安装目录内 `卸载.bat`，或 `install.ps1 -Uninstall` |

**无人值守测试**：设置环境变量后运行可跳过对话框（供 CI 使用）——

```powershell
$env:ZE_INSTALL_DIR = "$env:TEMP\ze-test"; $env:ZE_QUIET = '1'
& publish\ZeOverlay-Setup.exe
```

## 已知限制 / 待办

- **代码签名**：发布脚本支持 `-SignPfx` / `-SignThumbprint`（需 Windows SDK 的 `signtool.exe`）；
  **未提供证书时自动跳过**，此时首次运行可能触发 SmartScreen 提示（属预期）。
- **自定义图标**：已内置 `src/ZeOverlay.Gui/app.ico`；exe 与预览/设置窗口均使用它。
- 体积 214.9 MB 的大头是 WPF（`Microsoft.Windows.SDK.NET.dll` 23.7 MB、
  `PresentationFramework` 15.4 MB、`System.Windows.Forms` 12.9 MB 等）与原生库。
  WPF **不支持 `PublishTrimmed`**，故不做裁剪；如需更小可评估去掉 Windows SDK 投影依赖。
- `-ReadyToRun` 可换更快的冷启动，代价是体积更大，默认关闭。

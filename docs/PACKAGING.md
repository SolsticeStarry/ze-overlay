# 打包与安装（M6）

> 目标：**自包含单文件夹**（目标机无需装 .NET），并提供一个可双击的**启动安装包**。
> 决策依据与性能数字见 `DESIGN.md` §5 M6。

## 为什么是「单文件夹」而不是「单文件」

运行时数据（`config.json` / `watchlist.json` / `models/` / `shots/` / `logs/`）按设计
**固定在 exe 旁且可写**，路径取自 `Paths.ForExecutable()`（`AppContext.BaseDirectory`）。

若把 `models/` 一起打进单文件、运行时解压到 `%TEMP%`，`config`/`shots`/`logs` 会跟着跑到
临时目录，违背「数据在 exe 旁」的设计，也让用户找不到自己的配置与截图。
因此采用：**一个自包含文件夹**（exe + 运行库 + `models/`），双击即用。

## 安装布局要求（硬性约束）

安装器必须满足以下要求，任何打包改动都不得破坏：

1. **由用户选择安装目录**：弹出「选择安装位置」文件夹对话框，用户选的是**父目录 / 盘符**（可新建文件夹）。
2. **在所选目录下新建一个文件夹**（固定名 `ZeOverlay`；若所选目录本身就叫 `ZeOverlay` 则直接用），**所有内容都展开进这个文件夹**，而不是散落到用户所选目录里。
3. **安装根第一层必须干净**：只允许放用户入口（`启动 ZeOverlay.bat`、`卸载.bat`）与程序子目录 `app\`。
4. **程序本体与全部 `.dll` 依赖放到子目录里**：`ZeOverlay\app\` 内放 `ZeOverlay.Gui.exe`、所有 DLL / 运行时、`models\`；**不允许把依赖铺在安装根第一层**。
5. 运行时数据（`config.json` / `watchlist.json` / `shots` / `logs`）落在 exe 旁，即 `ZeOverlay\app\`。

```
<用户选择的父目录>\
    ZeOverlay\                 ← 新建的安装根，第一层只有下面这些
        启动 ZeOverlay.bat      ← 用户入口
        卸载.bat               ← 用户入口
        app\                   ← 程序本体：exe + 全部 .dll 依赖 + models（依赖都在这一层子目录）
            ZeOverlay.Gui.exe
            ...dll / runtime...
            models\
```

> 落实位置：`tools/make-installer.ps1` 生成的自解压引导 `install.cmd` 里
> `APPDIR=<TARGET>\app`（解压进子目录），`install.ps1 -WriteLaunchers` 只在安装根写两个入口；
> 见下方「单文件安装程序」一节。

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
    models/ch_PP-OCRv6_rec_infer.onnx   默认模型
    models/ppocrv6_dict.txt             v6 字典
    启动 ZeOverlay.bat              便携启动（双击）
    install.ps1                     安装器
    安装到本机.bat                  双击安装（复制到本机 + 建快捷方式）
publish/ZeOverlay-cli/              仅 -IncludeCli 时生成
publish/ZeOverlay-win-x64.zip       仅 -Zip 时生成
```

发布脚本会**精简**运行时用不到的东西（`publish.ps1` 的 `Publish-Project`）：

| 剔除项 | 原因 | 省下 |
|---|---|---|
| `models/ch_PP-OCRv3_det_infer.onnx` | 项目不接检测（det）模型，只用 rec | 2.3 MB |
| `System.Windows.Forms*.dll`（4 个） | 本 WPF 应用从不引用 WinForms，但 WindowsDesktop 框架会带进来；实测删掉后仍正常启动 | ~21 MB |
| `models/ch_PP-OCRv3_rec_infer.onnx` | 运行时默认只用 v6；v3 仅留仓库作开发回退 | 10.2 MB |
| `*.pdb` | 发布包不需要调试符号 | 若干 |

> 仍无法轻易剔除的大块（约占发布 204 MB 的一半）：`Microsoft.Windows.SDK.NET.dll` 23.7 MB（仅系统 OCR 回退用，见下）、
> `DirectML.dll` 17.7 MB + `onnxruntime.dll` 16.5 MB（GPU 加速必需）、WPF 核心几十 MB（不支持裁剪）、v6 模型 20.3 MB。

## 安装 / 卸载

安装器（`install.ps1`）负责创建/删除快捷方式；它**不复制文件**，只对已就位的程序建快捷方式。

```powershell
# 为程序目录创建桌面 / 开始菜单快捷方式（缺省自动探测 exe 所在目录）
powershell -ExecutionPolicy Bypass -File install.ps1 -AppDir D:\Tools\ZeOverlay\app

# 跳过桌面快捷方式、装完即启动
powershell -ExecutionPolicy Bypass -File install.ps1 -AppDir D:\Tools\ZeOverlay\app -NoDesktopShortcut -Launch

# 卸载（删快捷方式 + 整个安装根目录）
powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall -AppDir D:\Tools\ZeOverlay\app -Root D:\Tools\ZeOverlay
```

- 快捷方式指向 `app\ZeOverlay.Gui.exe`，工作目录为 `app\`。
- `config.json` / `watchlist.json` / `shots` / `logs` 运行时生成在 **exe 旁（即 `app\`）**。

## 实测（2026-10-03，win-x64，Release，含 PP-OCR v6）

| 项 | 值 |
|---|---|
| 发布产物 | 280 个文件，**204 MB**（含 WPF + onnxruntime + DirectML + v6 模型） |
| zip（Optimal） | **93.3 MB** |
| 自包含启动 | ✅ 目标机无需 .NET；启动日志正常 |
| **DirectML 在单文件夹下加载** | ✅ 日志 `PP-OCRv6-rec(18709 类)+DML`，无缺 DLL 崩溃 |
| 安装/卸载可逆 | ✅ 复制、桌面/开始菜单快捷方式、卸载均验证通过 |
| 精简后对比 | 235.3 → **204 MB**（剔除 WinForms ~21 MB + v3 rec 10.2 MB） |

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
2. 弹出**「选择安装位置」文件夹对话框**，这里选的是**父目录 / 盘符**（默认桌面）；
3. 在所选目录下**自动新建 `ZeOverlay` 子文件夹**（若所选目录本身已叫 `ZeOverlay` 则直接用它）；
4. 程序本体（`exe` + 全部依赖 + `models`）解压进 `ZeOverlay\app\`；
5. `ZeOverlay\` **第一层只写两个入口**（`启动 ZeOverlay.bat`、`卸载.bat`）并创建桌面 / 开始菜单快捷方式。

```
<所选父目录>\ZeOverlay\        ← 用户看到的目录，保持清爽
    启动 ZeOverlay.bat          ← 双击启动
    卸载.bat                   ← 卸载
    app\                       ← 程序本体：exe + 全部 DLL / 运行时 / models
        ZeOverlay.Gui.exe
        ...
```

例：选择「桌面」→ 安装到 `桌面\ZeOverlay\`，其第一层只有 2 个 bat + `app\`，
依赖再也不会铺满桌面。

| 项 | 值 |
|---|---|
| 安装器 | `publish\ZeOverlay-Setup.exe`，**92.2 MB**（内嵌 93.3 MB payload.zip） |
| 展开结果 | 280 文件 / 204 MB（含 `DirectML.dll`、`models\ch_PP-OCRv6_rec_infer.onnx`） |
| 卸载 | 安装目录内 `卸载.bat`，或 `install.ps1 -Uninstall` |

**无人值守测试**：设置环境变量后运行可跳过对话框（供 CI 使用）——

```powershell
# ZE_INSTALL_ROOT：父目录；安装器会在其下建 ZeOverlay 子文件夹
$env:ZE_INSTALL_ROOT = "$env:TEMP"; $env:ZE_QUIET = '1'
& publish\ZeOverlay-Setup.exe      # → %TEMP%\ZeOverlay\

# ZE_INSTALL_DIR：精确目录（直接用，不再加子文件夹）
# $env:ZE_INSTALL_DIR = "$env:TEMP\my-ze"; $env:ZE_QUIET = '1'
```

## 已知限制 / 待办

- **代码签名**：发布脚本支持 `-SignPfx` / `-SignThumbprint`（需 Windows SDK 的 `signtool.exe`）；
  **未提供证书时自动跳过**，此时首次运行可能触发 SmartScreen 提示（属预期）。
- **自定义图标**：已内置 `src/ZeOverlay.Gui/app.ico`；exe 与预览/设置窗口均使用它。
- 体积 214.9 MB 的大头是 WPF（`Microsoft.Windows.SDK.NET.dll` 23.7 MB、
  `PresentationFramework` 15.4 MB、`System.Windows.Forms` 12.9 MB 等）与原生库。
  WPF **不支持 `PublishTrimmed`**，故不做裁剪；如需更小可评估去掉 Windows SDK 投影依赖。
- `-ReadyToRun` 可换更快的冷启动，代价是体积更大，默认关闭。

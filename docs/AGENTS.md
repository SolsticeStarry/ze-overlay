# AGENTS.md（项目级）

> 通用工作方式见全局指令 `~/.config/opencode/instructions/proactive-tooling.md`，此处只写本项目特有的内容。

## 文档地图

| 文档 | 内容 |
|---|---|
| **`DESIGN.md`** | 决策与设计：用户指令 → 处置、被实测推翻的假设、各阶段要点、未实现意图。**先看这个** |
| `REAL_SAMPLES.md` | 真机样本转录与像素量测（回归集来源） |
| `PACKAGING.md` | 自包含打包 + 启动安装包（M6）：命令、布局、实测体积、限制 |
| `AGENTS.md`（本文） | 命令、**代码分层（14 模块）**、约束、已知坑 |

## 项目

CS2（ZE 模式）**神器列表识别 + 点击穿透叠加**。
C# / .NET 8（`net8.0-windows10.0.19041.0`）+ WPF，识别用 **PP-OCRv3 ONNX**。

```
截屏 → ROI 裁剪 → 行剖分 + 等距网格吸附 → 逐行 PP-OCR
     → 括号锚定解析 → 行槽位跟踪(+本地倒计时外推) → 名单过滤 → 预览窗口 + 穿透叠加
```

**进度：M0~M5 完成；M6 进行中**（识别 12 行 602ms→~70ms，DirectML 默认开启；自包含单文件夹打包 + 启动安装包 + 自定义图标已完成，见 `PACKAGING.md`；代码签名需自备证书；int8 静态量化实测**否决**）。设置窗口已支持排序/显示风格/刷新频率/热键配置。

## 命令

```powershell
dotnet build ZeOverlay.slnx         # 注意是 .slnx（.NET 10 SDK 新格式）
dotnet test  ZeOverlay.slnx         # 141 个用例（Core 133 + Platform 8）

$gui = "src\ZeOverlay.Gui\bin\Debug\net8.0-windows10.0.19041.0\ZeOverlay.Gui.exe"
$cli = "src\ZeOverlay.Cli\bin\Debug\net8.0-windows10.0.19041.0\ZeOverlay.Cli.exe"

& $gui                              # 主程序（GUI）
& $gui --select-roi                 # 只做标定（GUI 功能）

& $cli --capture-once --frames 20   # 采集自检（退出码 0/2/3）
& $cli --analyze-image <png> [--roi x,y,w,h] [--glyphs]   # 行剖分 / 字形切分（--glyphs 出可视化图）
& $cli --ocr <png> [--roi ...]      # OCR 原始行（含预处理对比诊断）
& $cli --recognize <png> [--roi ...] [--ppocr]            # 完整识别链路离线跑
& $cli --bench-ocr <png> [--roi ...] [--model <onnx>] [--threads N] [--repeat N] [--no-spin] [--ep cpu|dml] [--fixed-width N] [--batch N] [--dml-device N] [--out <txt>]  # 识别耗时拆解（墙钟/CPU/空闲/阶段）
```

## 打包 / 安装（M6）

```powershell
# 自包含单文件夹发布（目标机无需装 .NET）；-Zip 出 zip，-IncludeCli 附带离线 CLI
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 [-Zip] [-IncludeCli] [-Proxy http://127.0.0.1:7897]
# 代码签名（可选，需 Windows SDK 的 signtool）：PFX 或证书指纹
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Zip [-SignPfx cert.pfx -SignPfxPassword ***] [-SignThumbprint <sha1>]
# 安装到本机 + 建快捷方式；-Uninstall 卸载
powershell -ExecutionPolicy Bypass -File tools\install.ps1 [-InstallDir <dir>] [-Uninstall]
```

详见 `PACKAGING.md`。

## 量化（M6，已否决，仅存档）

```powershell
# 建隔离环境（本项目 Python 工具链）
python -m venv .venv-quant
.venv-quant\Scripts\pip install onnxruntime==1.24.4 onnx numpy pillow
# 静态 int8 量化（校准集来自 docs/ref/*.rows.json 的真实行）
.venv-quant\Scripts\python tools\quantize-ppocr-static.py models\ch_PP-OCRv3_rec_infer.onnx models\ch_PP-OCRv3_rec_infer.int8.onnx --ref-dir docs\ref --fixed-width 640 --op-types Conv,MatMul --preprocess
```

⚠️ 实测结论（2026-10-02）：**精度崩塌 + 两个 EP 都更慢**，已放弃。用 `--model <int8.onnx>` 配 `--bench-ocr` 复现。详见 `DESIGN.md` §3.1 / §5。

## 热键（实际生效值；注册失败会自动回退到候选）

| 功能 | 生效 | 首选 |
|---|---|---|
| 显示/隐藏预览 | `Ctrl+Shift+O` | `Ctrl+Alt+O`（被占用，回退） |
| 显示/隐藏叠加 | `Ctrl+Alt+H` | — |
| 拖动叠加 | `Ctrl+Shift+D` | `Ctrl+Alt+D`（被占用） |
| 重新框选 ROI | `Ctrl+Shift+R` | `Ctrl+Alt+R`（被占用） |
| 保存当前帧 | `Ctrl+Alt+S` | — |
| 退出 | `Ctrl+Alt+Q` | — |

## 运行时目录（固定在 exe 旁）

| 路径 | 内容 |
|---|---|
| `config.json` | 配置 + 标定结果 + 叠加位置（原子写入，损坏自动备份回退） |
| `watchlist.json` | 关注名单（≤10）+ 模糊阈值；**名单为空 = 全部显示** |
| `models/` | PP-OCR 模型 + 字典（随 exe 复制） |
| `shots/`、`shots/changes/` | 截图留档；列表变化前后成对帧（翻页样本） |
| `logs/app.log` | 结构化日志（超限轮转）；搜 `[跟踪表]` 看完整跟踪状态 |

## 代码分层

| 项目 | 职责 |
|---|---|
| `src/ZeOverlay.Shared` | **共用数据**：`ImageFrame`/`PixelRect`/`RelativeRect`、各阶段 DTO、枚举、`ListRules`、管线契约 `IStage<,>` |
| `src/ZeOverlay.Infrastructure` | 配置/日志/工具 + `Pipeline`（S1–S6 顺序） |
| `src/ZeOverlay.Win32` | 跨 Windows 阶段共享的 Win32：`Native`/`Monitors`/`Locator`/`Png`/`Input`/离线标注 |
| `src/ZeOverlay.Stage.ScreenCapture` | **S0** 采集（`Gdi` + `Shots` + `IScreenCaptureStage`） |
| `src/ZeOverlay.Stage.RowAnalysis` | **S1** 行分析（`Analyzer`/`Grid`/`Cache` + `IRowAnalysisStage`） |
| `src/ZeOverlay.Stage.GlyphSegmentation` | **S2** 字形定界+裁剪（`Segmenter` + `IGlyphSegmentationStage`） |
| `src/ZeOverlay.Stage.Recognition` | **S3** 识别（`PpOcrEngine`/`SystemOcrEngine`/`Decoder`/`PpOcrInput` + `IRecognitionStage`/`IRowRecognizer`） |
| `src/ZeOverlay.Stage.Parsing` | **S4** 解析（`Parser` + `IParsingStage`） |
| `src/ZeOverlay.Stage.Tracking` | **S5** 跟踪（`Tracker`/`Structure` + `ITrackingStage`） |
| `src/ZeOverlay.Stage.Matching` | **S6** 名单匹配（`Matcher` + `IMatchingStage`） |
| `src/ZeOverlay.Stage.Presentation` | **S7** 展示/叠加（WPF，`IPresentationStage`） |
| `src/ZeOverlay.Gui` | 组合根：装配阶段 + 采集线程/节流/标定/热键/持久化（`Host.*`） |
| `src/ZeOverlay.Cli` | 控制台离线工具：`--capture-once` / `--analyze-image` / `--ocr` / `--recognize` / `--bench-ocr` |
| `tests/ZeOverlay.Tests` | xUnit 聚合测试（141 用例） |

## 本项目专属约束

1. **绝不把 GUI 工具指向 `cs2.exe`** —— 反作弊风险，也不是测试目标。
2. GUI 白名单 `OPENCODE_CU_WINDOW_ALLOWLIST` 只加**本项目自己的测试程序**（当前仅 `notepad.exe`）。
3. **不要提前声称识别准确率或 FPS** —— 一切数字必须真机实测。
4. 本机环境：单显示器 **1920×1080 @100%**。
5. **叠加窗口绝不能覆盖识别区（ROI）** —— 否则截屏会把叠加自己抓进识别区形成自反馈；已有自动守卫。

## 推荐做法

| 情境 | 建议 |
|---|---|
| 识别/解析逻辑测试 | **合成图 + 假后端**的单元测试，不要靠 GUI 自动化 |
| 集成/端到端验证 | `opencode-computer-use` 截真实 ROI + `visual-verification` 差分 |
| 离线量测/回归 | `ZeOverlay.Cli` 的 `--analyze-image`（含 `--glyphs` 可视化）、`--recognize --ppocr`、`--bench-ocr` |
| 翻页/内容变化样本 | 开着主程序正常游玩即可，自动进 `shots/changes/`；日志搜 `[列表观测]` |
| 跟踪表排查 | 日志搜 `[跟踪表]`（每 15s 一次全表，含状态/倒计时/来源/miss） |
| 驱动框选覆盖层 | `tools/drive-roi-selector.ps1`（仅作用于本项目自己的窗口） |
| 性能/占用 | `dotnet-diagnostics`；对照 `DESIGN.md` §4 |

## 已知坑

| 现象 | 原因 | 处理 |
|---|---|---|
| 汉字被粘成一块 | `MinGap` 默认 2px 太大（该 UI 汉字间隙仅 1~2px） | `MinGap = 0` |
| 只盯坐标数字调参 | 曾在阈值上白跑三次 | **先放大看图**定位真正的作用量，再改一个参数；切分类改动必须用 `--glyphs` 目视验证 |
| 行数被噪声带着乱跳 | 场景亮物件/窗口边缘被当成行 | 等距网格吸附 + 离群剔除；评分惩罚空洞槽位 |
| 大量重复条目 | 用「名称+玩家名」做身份 + PP-OCR 行末幻觉 | 改**行槽位**身份；按**字形实际边界**裁剪；置信度 <0.6 丢弃 |
| 倒计时/uses 闪烁 | OCR 间歇漏读 `n/m` | 同槽位同神器时没读到就保留旧值（粘滞） |
| `Ctrl+Alt+R` / `Ctrl+Alt+D` 注册失败 | 被其它程序占用 | 已内置候选回退，以状态栏与日志为准 |
| NuGet / GitHub 直连失败 | 本机走本地代理且时通时断 | 设 `HTTP(S)_PROXY=http://127.0.0.1:7897` 重试；模型改从 **PyPI** 取 |
| 识别 12 行要 ~0.6s | CTC 解码用 `Tensor<T>` 多维索引器逐元素取；`FillInput` 每行列坐标重复算 | 已修（读底层 span + 预计算列）→ **~0.15s**；见 `DESIGN.md` §5 M6 |
| int8 动态量化没效果还更慢 | 该模型 36 Conv，`quantize_dynamic` 只量化 MatMul | 别用动态量化；要 int8 必须**静态 + 校准集** |
| CPU 占用偏高 | ORT 工作线程在两次 Run 间**自旋** | `Recognition.AllowSpinning=false`（默认）；线程数 `Recognition.IntraOpThreads` |
| 本机 CPU 计时不可信 | `Process.TotalProcessorTime` 失真（忙等 300ms 只读到 31~94ms） | CPU 结论以真机/外部计数器为准，别信单进程读数 |
| DML 运行崩溃 `0x80070057` | 没带对版本的 `DirectML.dll`，回退加载了 `System32` 的旧版 | 复制 `Microsoft.AI.DirectML` 的 win-x64 `DirectML.dll` 到 exe 旁（`ZeOverlay.Gui.csproj` / `ZeOverlay.Cli.csproj` 已配） |
| DML 比 CPU 还慢 | 每行宽度不同 → DML 反复重编译算子 | `Recognition.FixedInputWidth=640`（或 `--fixed-width 640`） |
| int8 静态量化后 C# 加载崩 `QLinearMul ... Scale and Zero-point must be a scalar` | 逐元素算子被量化 + per-channel scale 非标量；且**原模型权重存在 `Constant` 节点**（不是 initializer），动态量化只能碰到 MatMul | 只量化 `Conv,MatMul` 并 `--preprocess` 折叠 BN 才可加载；但精度仍崩，最终**放弃 int8** |
| 量化模型体积几乎不变 | 权重是 `Constant` 节点，ORT 量化器需先 `quant_pre_process` 转 initializer | 别无脑量化；见 `tools/quantize-ppocr-static.py` 注释 |
| WPF 窗口 `Icon="app.ico"` 运行时报资源找不到 | 相对 pack URI 解析到**入口程序集**，而图标嵌在 Presentation 程序集 | 用显式 URI：`pack://application:,,,/ZeOverlay.Stage.Presentation;component/app.ico` |
| 解决方案文件名是 `.slnx` | .NET 10 SDK 新默认 | `dotnet build ZeOverlay.slnx` |

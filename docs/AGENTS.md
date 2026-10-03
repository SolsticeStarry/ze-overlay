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
C# / .NET 8（`net8.0-windows10.0.19041.0`）+ WPF，识别用 **PP-OCR ONNX**（`models/` 里按 **v6 → v4 → v3** 自动选，默认 v6；v3 会把括号数字读丢/读多）。

```
截屏 → ROI 裁剪 → 行剖分 + 等距网格吸附 → 逐行 PP-OCR
     → 括号锚定解析 → 行槽位跟踪(+本地倒计时外推) → 名单过滤 → 预览窗口 + 穿透叠加
```

**进度：M0~M5 完成；M6 进行中**（识别 12 行 602ms→~70ms，DirectML 默认开启；自包含单文件夹打包 + 启动安装包 + 自定义图标已完成，见 `PACKAGING.md`；代码签名需自备证书；int8 静态量化实测**否决**）。设置窗口已支持排序/显示风格/刷新频率/热键配置。

**多社区服适配**：**逐行自动判断行格式**（本服括号 `[R]`/`[数字]` vs 社区服连写 `就绪/NNs/n/m/∞`），**无需选档案/语法**；跟踪器按「是否有服务器标号」自动切换身份（标号身份不分页 / 行槽位身份分页）。详见 `DESIGN.md` §3.8。

## 命令

```powershell
dotnet build ZeOverlay.slnx         # 注意是 .slnx（.NET 10 SDK 新格式）
dotnet test  ZeOverlay.slnx         # 320 个用例

$gui = "src\ZeOverlay.Gui\bin\Debug\net8.0-windows10.0.19041.0\ZeOverlay.Gui.exe"
$cli = "src\ZeOverlay.Cli\bin\Debug\net8.0-windows10.0.19041.0\ZeOverlay.Cli.exe"

& $gui                              # 主程序（GUI）
& $gui --select-roi                 # 只做标定（GUI 功能）

& $cli --capture-once --frames 20   # 采集自检（退出码 0/2/3）
& $cli --analyze-image <png> [--roi x,y,w,h] [--glyphs]   # 行剖分 / 字形切分（--glyphs 出可视化图）
& $cli --ocr <png> [--roi ...]      # OCR 原始行（含预处理对比诊断）
& $cli --recognize <png> [--roi ...] [--ppocr] [--parser bracket|plain]   # 完整识别链路离线跑（--parser plain 跑社区服语法）
& $cli --bench-ocr <png> [--roi ...] [--model <onnx>] [--threads N] [--repeat N] [--no-spin] [--ep cpu|dml] [--fixed-width N] [--batch N] [--dml-device N] [--out <txt>]  # 识别耗时拆解（墙钟/CPU/空闲/阶段）

# 双服可视化：确定性场景时间线 + 连续随机仿真（输出 artifacts\*.html）
dotnet run --project tools\ScenarioViz\ScenarioViz.csproj -c Release
# 自动截图验收（页面打开即自动循环播放，无需手动点）：
# chrome --headless=new --user-data-dir=<临时目录> --virtual-time-budget=5000 `
#        --screenshot=out.png "file:///.../scenario-simulation.html?server=fys"
```

## 打包 / 安装（M6）

```powershell
# 自包含单文件夹发布（目标机无需装 .NET）；-Zip 出 zip，-IncludeCli 附带离线 CLI
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 [-Zip] [-IncludeCli] [-Proxy http://127.0.0.1:7897]
# 打包成**单个自解压安装器 .exe**（IExpress；用户自选路径、自动展开、建快捷方式）
powershell -ExecutionPolicy Bypass -File tools\make-installer.ps1 [-Out <exe>] [-Icon <ico>]
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
| `config.json` | 配置 + 叠加位置 + ROI + **神器名表 `Vocabulary`**（原子写入，损坏自动备份回退）。首次启动会把旧的 `Profiles` 折叠成单配置 |
| `watchlist.json` | 关注名单（≤10）+ 模糊阈值；**名单为空 = 全部显示** |
| `models/` | PP-OCR 模型 + 字典（随 exe 复制） |
| `shots/`、`shots/changes/` | 截图留档；列表变化前后成对帧（翻页样本） |
| `logs/app.log` | 结构化日志（超限轮转）；搜 `[跟踪表]` 看完整跟踪状态 |

## 代码分层

| 项目 | 职责 |
|---|---|
| `src/ZeOverlay.Shared` | **共用数据**：`ImageFrame`/`PixelRect`/`RelativeRect`、各阶段 DTO、枚举、`ListRules`、`ParserMode`、自动选档 `ProfileDetector`、管线契约 `IStage<,>` |
| `src/ZeOverlay.Infrastructure` | 配置/日志/工具 + `Pipeline`（S1–S6 顺序）+ 服务器档案（`Profiles`：默认值/迁移/转换） |
| `src/ZeOverlay.Win32` | 跨 Windows 阶段共享的 Win32：`Native`/`Monitors`/`Locator`/`Png`/`Input`/离线标注 |
| `src/ZeOverlay.Stage.ScreenCapture` | **S0** 采集（`Gdi` + `Shots` + `IScreenCaptureStage`） |
| `src/ZeOverlay.Stage.RowAnalysis` | **S1** 行分析（`Analyzer`/`Grid`/`Cache` + `IRowAnalysisStage`） |
| `src/ZeOverlay.Stage.GlyphSegmentation` | **S2** 字形定界+裁剪（`Segmenter` + `IGlyphSegmentationStage`） |
| `src/ZeOverlay.Stage.Recognition` | **S3** 识别（`PpOcrEngine`/`SystemOcrEngine`/`Decoder`/`PpOcrInput` + `IRecognitionStage`/`IRowRecognizer`） |
| `src/ZeOverlay.Stage.Parsing` | **S4** 解析（`Parser`：`bracket` 方括号锚定 / `plain` 社区服后缀状态 + `IParsingStage`） |
| `src/ZeOverlay.Stage.Tracking` | **S5** 跟踪（`Tracker`/`Structure` + `ITrackingStage`） |
| `src/ZeOverlay.Stage.Matching` | **S6** 名单匹配（`Matcher` + `IMatchingStage`） |
| `src/ZeOverlay.Stage.Presentation` | **S7** 展示/叠加（WPF，`IPresentationStage`） |
| `src/ZeOverlay.Gui` | 组合根：装配阶段 + 采集线程/节流/标定/热键/持久化（`Host.*`） |
| `src/ZeOverlay.Cli` | 控制台离线工具：`--capture-once` / `--analyze-image` / `--ocr` / `--recognize` / `--bench-ocr` |
| `tests/ZeOverlay.Tests` | xUnit 聚合测试（320 用例）；`Scenarios/` = exg/fys 双服假 HUD 场景回放 + 连续随机浸泡（`RandomizedServerSimulation`：平均 ~12 条、同类多神器、EXG 12+ 翻页（~5s 一次）、场景色漂移、tick 抖动；150 种子扫描 + 2000 组随机参数模糊测试） |

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
| 生成的 `.bat/.cmd` 一运行就报一堆「不是内部或外部命令」 | PS 5.1 的 `Set-Content -Encoding OEM` **会写 UTF-8 BOM**，cmd 把 BOM 当命令 | 用 `Write-OemFile`（`[IO.File]::WriteAllText` + cp936 + CRLF，无 BOM）；见三个打包脚本 |
| 新服神器简称被当状态、玩家名和状态粘一起 | 用本服括号语法解析社区服连写文本：`人闪1` 的 `1` 被当冷却 | 该服档案用 `ParserMode=plain`；解析器从右取状态、从左取简称 |
| 新服 `∞` 状态显示成冷却/无状态 | PP-OCR 字典无 `∞`，读成孤立 `0` 或 `8` | Plain 解析把「末尾未被数字/斜杠包围的 1~2 位孤立数字」当无限（就绪） |
| 新服 `1.5s` 冷却变成小数/异常 | 冷却可带小数 | 已明确**丢弃小数只取整数**（`1.5s`→1） |
| 新服一屏 >12 行时整帧观测被丢弃 | `Host.Recognition` 曾硬编 `ListRules.MaxRowsPerPage=12` | 已改为按当前档案 `MaxRowsPerPage`（社区服 16） |
| 切换服务器后叠加还在用旧 ROI / 旧名单 | 档案未切换 | 自动选档（命中 <2 才探测其它档案，连续两次胜出才切）或 `Ctrl+Alt+P` 手动切；新服需先切过去框选一次 ROI |
| IExpress 安装器反复运行失败/无反应 | 它固定解压到 `%TEMP%\IXP000.TMP`，上次异常退出残留会被占用 | 清掉 `IXP000.TMP` 再试；正常退出会自清理 |
| 解决方案文件名是 `.slnx` | .NET 10 SDK 新默认 | `dotnet build ZeOverlay.slnx` |
| 同类多神器（`水枪1/2/3`）回流后某个标号条目丢失 | 「同行位借标号」无法区分 OCR 读花与真实兄弟；跨帧借用还会借到暂时不可见的兄弟 | **标号一律采信画面读数；读丢（null）一律跳过该行、不猜**。回归 `SameNameMultipleIndices_Reflow_KeepsEachSibling`、`MissingIndex_IsSkippedInsteadOfGuessingLabel`、`RandomizedStream_ManySeeds_NoViolations`、`Fuzz_RandomOptions_FindsNoViolations` |
| 冷却走完再次使用时倒计时卡在"就绪" | 「冷却只降不升」的防跳变把**再次使用**的新长冷却当成 OCR 读花拒收（1Hz 采样看不到中间的 `[R]` 帧） | 外推已 ≈0（`previous <= 1`）时接受新冷却；回归 `ReUse_WhenCooldownNearlyDone_IsAccepted` |
| 冷却读数被防跳变误杀（旧条目残留） | 旧神器移除后，新神器**复用同一 `(名称,标号)`**；旧条目未超时仍在，新冷却被当成"只降不升"的尖峰拒收 | **上一帧未连续观测（`Source=Extrapolated`）时不做防跳变**；连续观测（`Live`）才启用。回归 `Fuzz_RandomOptions_FindsNoViolations` |
| 某一帧标号全丢后身份错乱 | `byLabel` 原来逐帧由"本帧有无标号行"决定，全丢的一帧会翻成行槽位身份、造出槽位键条目 | 身份模式**粘性**：只要还有标号身份条目存在就保持标号模式 |
| 名单匹配**张冠李戴**（实机复现） | `BuildRecognitionText` 的缓存键 `EntryKey` 用 `P{页}#{行位}`；标号身份下不同条目会撞同一行位 ⇒ 命中结果串台 | 抽到 `Shared.TrackerKeys.Identity`：标号身份用「名称+标号」、行槽位身份用「页+槽位」；回归 `TrackerKeysTests` |
| 连写服被降回 2Hz/500ms（重构回归） | 档案折叠后 `ServerProfileConfig.CaptureFps/RecognitionIntervalMs` 不再被读取 | 运行时**自适应**：识别到带标号的行即按 `Recognition.CommunityIntervalMs`（默认 **300ms**）自动提速采集+识别，括号服沿用配置 |
| 连写服日志刷「疑似翻页」 | 结构观测按行数骤降判翻页，但社区服无翻页 | 连写模式下不再追加「疑似翻页」，只记行数/内容变化 |
| 连写服**长倒计时经常瞬间跳 `[R]`**（实机反馈） | 冷却显示 `49s`，OCR 偶尔把结尾 `s` 读丢成 `49`；解析器「`∞` 被读成数字」的兜底把**任意 1~2 位结尾数字**都当无限 ⇒ 变就绪 | 兜底收紧为**只认单个 `0`/`8`**（实测的 `∞` 误读）；`49` 之类解析失败被丢弃、由本地外推继续走冷却。回归 `ParsePlain_DroppedCooldownSuffix_IsNotTreatedAsReady` |
| 同名多神器**上下跳变**（`爆闪1` 窜到 `爆闪2` 下面） | 叠加排序在同名同组时用**行位**分先后，连写服回流让行位互换 | 排序收尾改用「页 → **标号** → 行位」，同名条目恒按 1、2、3… 排。回归 `Watchlist_SameNameMultiples_OrderByServerIndex_NotChurningSlot`、`Cooldown_SameNameMultiples_OrderByServerIndex` |
| 亮底（白背景）下条目"消失"/读花 | ① 本服行整段丢方括号（`袋装火盐门3小小猪头`）被 `ParseAuto` 丢弃 → 该槽位不更新；② 名字被读花成 `袋装火盐门`，名单阈值 0.85 不认 → 叠加里被滤掉（跟踪器其实还有） | ① `ParseAuto` 最后兜底**裸状态**（仅当行不以连写服状态结尾）；② `ParsingStage` 用名表**纠错**（`CanonicalizeName`：`袋装火盐门`→`袋装火盐`）；③ 识别输入对**低对比裁剪**做分位数拉伸（`PpOcrInput.BuildContrastStretch`）。样本 `docs/ref/暂停.png`，回归 `ParseAuto_RecoversBracketLineWithLostBrackets`、`CanonicalizeName_*`、`BuildContrastStretch_OnlyForLowContrast` |
| **两位数倒计时显示成个位数 / 个位被吞**（`[15]`→`5`、`[42]`→`4`） | PP-OCRv3 把开括号+数字读花（`[1`→`门`、`52`→`521`）；v3 对该 UI 的 `[数字]` 既吞位又加位，每帧不一致 | **换 PP-OCRv6 rec**（`PpOcrModels` 自动 v6→v4→v3；样本 7/7 全对且置信 0.96~1.00）+ 冷却**下界防跳变**（掉 >2s 视为吞位）+ 冷却**多帧投票**（`CooldownVote`：最新读数与近 3 帧中位差 ≥2 判读花、取中位；重置清历史）。回归 `CooldownVote_*`、`ImplausibleDrop_DroppedTensDigit_IsIgnored`、`PpOcrModelsTests` |
| 模型路径写死 v3、换模型要改代码 | `Host`/`Cli` 曾硬编码 `ch_PP-OCRv3_rec_infer.onnx` + `ppocr_keys_v1.txt` | `PpOcrModels.Resolve(modelsDir, ModelFile, KeysFile)`：v6→v4→v3 自动挑；v5/v6 用独立字典 `ppocrv{5,6}_dict.txt`；`Recognition.ModelFile/KeysFile` 可显式覆盖 |
| **游戏内轻微卡顿**（实机反馈） | 连写服自动提速到 **5Hz 采集 / 200ms 识别**：DirectML OCR 每 200ms 抢一次 GPU、GDI BitBlt 每 200ms 拷一次游戏窗口、叠加每 200ms 重绘一次。实测 overlay 持续占 ~6% GPU / ~700MB 显存 | ① 提速降到 **300ms**（可调 `Recognition.CommunityIntervalMs`，150–1000）；② 叠加内容签名不变就**跳过重建**（`OverlayWindow.UpdateEntries`）；③ 预览窗口不可见时**不上传位图**。倒计时靠独立 tick 外推，降频不影响到手速度 |
| **莫名其妙出现 `大火盐7`/`爆闪7` 重复** | 标号被 OCR 读花（`1`→`7/8/9`）⇒ 多出一个 `(名称,标号)` 身份 ⇒ 幽灵条目，按消失规则挂满 6s。列表剧烈滚动时更易发生 | 叠加**两帧确认过滤幽灵**（`Host.Recognition`：`LastSeen <= FirstSeen` 的条目先不显示）；真实条目下一帧即出现。根因仍是标号单帧误读，彻底解决需标号跨帧稳定 |
